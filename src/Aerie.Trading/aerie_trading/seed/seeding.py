"""The three steps, and the questions each of them asks before acting.

See ``seed/__init__.py`` for what this is and why it computes rather than
inserts. This module is the mechanics.
"""

from __future__ import annotations

import logging
from collections.abc import Sequence
from dataclasses import dataclass, field
from datetime import UTC, date, datetime, timedelta

from sqlalchemy import text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.collect.bars import BarCollector
from aerie_trading.collect.runs import LedgerRunLog
from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval, MarketDataProvider

# The build stamped onto the bars this job writes. The writer's own revision
# rather than the caller's, because that is what produced the file.
from aerie_trading.revision import read_revision
from aerie_trading.runs.catalog import ensure_strategy
from aerie_trading.runs.demo import demo_sweeps
from aerie_trading.runs.sweep import SweepSpec, enqueue_sweep, plan_sweep
from aerie_trading.settings import Settings
from aerie_trading.strategies import SPECS

__all__ = [
    "COVERAGE_SLACK_DAYS",
    "SeedReport",
    "ensure_history",
    "ensure_strategies",
    "ensure_sweeps",
    "seed",
]

logger = logging.getLogger(__name__)

#: How far the Lake's first and last bar may fall inside the window before the
#: history counts as missing. Ten calendar days: a window that begins on a
#: holiday weekend legitimately has no bar for a week, and re-collecting five
#: years of Parquet on every deploy because of new year's day would be a seed
#: job that is not idempotent in the only way that costs anything.
COVERAGE_SLACK_DAYS = 10


@dataclass
class SeedReport:
    """What the seed did, one line per step.

    Kept as counts rather than as a boolean, because the interesting question
    on the second deploy is not *"did it work"* - it is *"did it do anything"*,
    and the honest answer has to be able to be zero.
    """

    strategies: int = 0
    #: Sessions of bars collected. Zero when the Lake already had the window.
    bars: int = 0
    #: Names of the sweeps this run enqueued. Empty on every deploy after the
    #: first, which is the property the plan's gate checks.
    sweeps: list[str] = field(default_factory=lambda: [])
    #: Names it found already seeded and left alone.
    existing: list[str] = field(default_factory=lambda: [])

    @property
    def changed(self) -> bool:
        return bool(self.strategies or self.bars or self.sweeps)


def seed(
    engine: Engine,
    settings: Settings,
    provider: MarketDataProvider,
    revision: str | None = None,
    collect: bool = True,
) -> SeedReport:
    """Register the strategies, fill the Lake if it is empty, enqueue the demo.

    ``collect=False`` is for the installation whose Lake is filled some other
    way - a restored volume, a longer backfill somebody ran by hand - and for
    the tests that have no business generating five years of Parquet to assert
    something about a sweep. It is not a default, because the case this job
    exists for is the cold cluster.
    """
    report = SeedReport()
    specs = demo_sweeps(settings.collection.bar_symbols, settings.honesty.walk_forward)

    report.strategies = ensure_strategies(engine)
    if collect:
        report.bars = ensure_history(engine, settings, provider, specs)
    ensure_sweeps(engine, settings, provider, specs, revision, report)

    logger.info(
        "Seed complete",
        extra={
            "Strategies": report.strategies,
            "Bars": report.bars,
            "Enqueued": ",".join(report.sweeps),
            "AlreadySeeded": ",".join(report.existing),
        },
    )
    return report


def ensure_strategies(engine: Engine) -> int:
    """A ``strategy`` row for every strategy this build ships.

    Returns how many were written or refreshed, which is all of them - the row
    carries the description and the JSON schema, and a build that edited either
    must not leave a UI describing the old one. See
    ``runs/catalog.ensure_strategy``: the history of what a strategy *was* is
    carried by the revision on each run, which is the column that can honestly
    hold it.
    """
    with Session(engine) as session, session.begin():
        for spec in SPECS:
            ensure_strategy(session, spec)
    return len(SPECS)


def ensure_history(
    engine: Engine,
    settings: Settings,
    provider: MarketDataProvider,
    specs: Sequence[SweepSpec],
) -> int:
    """Collect the demo's window into the Lake, unless it is already there.

    **Why the seed collects at all.** The demo sweeps read five years of daily
    bars. On a cold cluster the Lake is an empty PVC and the collector CronJob
    fires after the next close, collecting one session - so without this, the
    seeded leaderboard is twenty runs that all failed, on the front page, until
    somebody notices and runs a backfill by hand. The plan's gate is *"open the
    control panel without running anything, and find every screen populated"*,
    and that is not satisfiable without the history the runs read.

    **Why it is safe to skip.** The lake's writes are idempotent per partition
    (``lake/writer.py``), so re-collecting would be correct but not free: it is
    five years of generated Parquet on every deploy. The check below is what
    makes the common case - a deploy onto a cluster that already has the
    data - do nothing at all.
    """
    window = _window(specs)
    if window is None:
        return 0
    start, end, symbols, interval = window

    with LakeReader(settings.lake_root) as reader:
        if _covered(reader, symbols, interval, start, end):
            logger.info(
                "The Lake already covers the demo window; collecting nothing",
                extra={"From": str(start.date()), "To": str(end.date())},
            )
            return 0

    logger.info(
        "Backfilling the demo window",
        extra={
            "From": str(start.date()),
            "To": str(end.date()),
            "Symbols": ",".join(symbols),
        },
    )
    writer = LakeWriter(
        root=settings.lake_root,
        provenance=Provenance(provider.name, read_revision().revision),
    )
    collector = BarCollector(provider=provider, writer=writer, log=LedgerRunLog(engine))
    # Inclusive of both ends, which is what `backfill` takes - and the window's
    # end is an exclusive instant, so the last day it covers is the one before.
    record = collector.backfill(symbols, interval, start.date(), (end - timedelta(days=1)).date())
    return record.rows_written


def ensure_sweeps(
    engine: Engine,
    settings: Settings,
    provider: MarketDataProvider,
    specs: Sequence[SweepSpec],
    revision: str | None,
    report: SeedReport,
) -> None:
    """Enqueue each demo sweep that this job has not already enqueued.

    One transaction for the whole batch, matching ``runs/__main__.py``: the
    demo is a grid *and* the baseline it is measured against, and a leaderboard
    holding the grid without the baseline is a half-seeded state the control
    panel would then have to render.

    The lookup is ``(name, seeded)`` - see the package docstring on why the
    flag rather than the name is what makes this safe.
    """
    # Imported here rather than at module scope for the reason the runs CLI
    # gives: this is the one call that needs a provider row, and the import
    # graph is what keeps the worker's image from paying for pandas.
    from aerie_trading.providers.registry import ensure_data_source

    with Session(engine) as session, session.begin():
        source = ensure_data_source(session, provider)
        session.flush()
        seeded = _seeded_names(session)
        for spec in specs:
            if spec.name in seeded:
                report.existing.append(spec.name)
                continue
            plan = plan_sweep(spec)
            enqueue_sweep(
                session,
                plan,
                # The handshake, satisfied by the code that owns the number.
                # `runs/demo.py` is explicit that the demo's size is a decision
                # made once in code, so the code is what confirms it - which is
                # the same exception `python -m aerie_trading.runs demo` makes.
                confirm=plan.total,
                data_source_id=source.id,
                revision=revision,
                ceiling=settings.runs.max_sweep_runs,
                seeded=True,
            )
            report.sweeps.append(spec.name)


def _seeded_names(session: Session) -> set[str]:
    return {
        str(row.name) for row in session.execute(text("SELECT name FROM sweep WHERE seeded")).all()
    }


def _window(
    specs: Sequence[SweepSpec],
) -> tuple[datetime, datetime, tuple[str, ...], Interval] | None:
    """The span, universe and interval the whole demo needs collected.

    The union across the specs rather than the first one's, because the demo is
    two sweeps and a future edit could legitimately widen one of them. A
    mismatch of intervals is the one thing this cannot express, and it raises
    rather than collecting the wrong one silently.
    """
    if not specs:
        return None
    intervals = {spec.interval for spec in specs}
    if len(intervals) != 1:
        raise ValueError(
            f"the demo sweeps ask for {sorted(interval.value for interval in intervals)};"
            " the seed collects one interval, so make them agree or teach it both"
        )
    symbols: list[str] = []
    for spec in specs:
        symbols.extend(symbol for symbol in spec.symbols if symbol not in symbols)
    return (
        min(spec.window_start for spec in specs),
        max(spec.window_end for spec in specs),
        tuple(symbols),
        intervals.pop(),
    )


def _covered(
    reader: LakeReader,
    symbols: Sequence[str],
    interval: Interval,
    start: datetime,
    end: datetime,
) -> bool:
    """Whether the Lake already holds this window, for every symbol.

    Per symbol rather than in aggregate, and both ends rather than a row count.
    A universe that gained a symbol since the last deploy has a Lake that is
    full for four of five and empty for the fifth, and a check on the total
    would call that covered - which is a seeded leaderboard with one strategy
    silently missing from every run.
    """
    coverage = reader.bar_coverage(symbols, interval, start, end)
    if coverage.is_empty():
        return False
    slack = timedelta(days=COVERAGE_SLACK_DAYS)
    for symbol in symbols:
        sessions = coverage.filter(coverage["symbol"] == symbol)["session"]
        if sessions.is_empty():
            return False
        first: date = sessions.min()  # pyright: ignore[reportAssignmentType]
        last: date = sessions.max()  # pyright: ignore[reportAssignmentType]
        if _at(first) > start + slack or _at(last) < end - slack:
            return False
    return True


def _at(day: date) -> datetime:
    return datetime(day.year, day.month, day.day, tzinfo=UTC)
