"""``python -m aerie_trading.collect`` - what a collector CronJob runs.

One entry point with subcommands rather than one module per collector, for the
same reason ``control/__main__.py`` is a module rather than a console script:
the container's ``command:`` names something that exists in the source tree and
runs identically outside the image.

**The provider is constructed here and nowhere else.** This is the composition
root for a collection: it reads ``Settings``, builds the configured provider,
opens the Ledger, builds a ``LakeWriter`` stamped with this build's revision,
and hands all of that to a collector that knows none of it. When Phase 8 adds
Schwab, ``_build_provider`` gains a branch and nothing else in this package
changes - which is the phase's acceptance criterion made checkable by having
exactly one place it could fail.

**Exit codes are the interface with Kubernetes.** A CronJob's success is its
pod's exit status, and that is what ``kube_cronjob_status_last_successful_time``
records and what the collection-health alerts read. So:

===  =========================================================================
0    the collection ran and is recorded.
1    the collection failed. The ``ingest_run`` row says how.
2    the collection was **refused** - a holiday, a closed market. Distinct on
     purpose: a refusal is the collector working correctly, but a Job that
     exited 0 on Thanksgiving would advance the last-success metric over a day
     nothing was collected, and one that exited 1 would page someone about a
     public holiday.
===  =========================================================================

A CronJob treats any non-zero exit as a failure, so exit 2 does show up as a
failed Job. That is the intended reading: the schedule fired on a day it should
not have, which is a fact about the schedule worth seeing in the Job history
without it being an incident. ``deploy/cluster/trading/lake/`` sets
``backoffLimit: 0`` on the chain collector for exactly this reason - retrying a
refusal cannot make the market open.
"""

from __future__ import annotations

import argparse
import logging
import sys
from collections.abc import Sequence
from datetime import UTC, date, datetime, timedelta

from aerie_trading.collect.bars import BarCollector
from aerie_trading.collect.chains import ChainCollector
from aerie_trading.collect.runs import LedgerRunLog, RunRecord
from aerie_trading.db.engine import create_ledger_engine
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.logging import configure_logging
from aerie_trading.providers.base import Interval, MarketClosed, MarketDataProvider
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.revision import read_revision
from aerie_trading.settings import Settings, get_settings

logger = logging.getLogger(__name__)

#: Exit code for a refused collection. See the module docstring.
EXIT_REFUSED = 2


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="python -m aerie_trading.collect",
        description="Collect market data into the lake.",
    )
    # required=True, so an invocation naming no collector fails loudly. A
    # default subcommand here would mean a typo'd CronJob argument silently
    # collecting the wrong thing on a schedule.
    subcommands = parser.add_subparsers(dest="collector", required=True)

    bars = subcommands.add_parser("bars", help="OHLCV bars for the configured symbols.")
    bars.add_argument(
        "--mode",
        choices=("incremental", "latest", "backfill"),
        default="incremental",
        help=(
            "incremental collects today's session and refuses a day the market was shut;"
            " latest collects the most recent session, whenever it was;"
            " backfill collects a date range."
        ),
    )
    bars.add_argument(
        "--interval",
        action="append",
        dest="intervals",
        choices=tuple(interval.value for interval in Interval),
        help="Bar size; repeatable. Defaults to the configured bar_intervals.",
    )
    bars.add_argument("--start", type=date.fromisoformat, help="Backfill start date (inclusive).")
    bars.add_argument("--end", type=date.fromisoformat, help="Backfill end date (inclusive).")
    bars.add_argument(
        "--on",
        type=date.fromisoformat,
        help="Treat this as today. Defaults to the current UTC date.",
    )

    chains = subcommands.add_parser("chains", help="Option boards for the configured watchlist.")
    chains.add_argument(
        "--at",
        type=datetime.fromisoformat,
        help="The instant to snapshot. Defaults to now. Must be timezone-aware if given.",
    )
    chains.add_argument(
        "--session",
        type=date.fromisoformat,
        help="Collect every scheduled snapshot of this whole session instead of one.",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    arguments = build_parser().parse_args(argv)
    settings = get_settings()
    configure_logging(settings.log_level)

    provider = build_provider(settings)
    revision = read_revision()
    writer = LakeWriter(
        root=settings.lake_root,
        provenance=Provenance(provider.name, revision.revision),
    )
    # The engine is created per invocation and disposed on the way out: this is
    # a batch process that runs for seconds and exits, so a pool that outlives
    # it is a pool nothing reuses.
    engine = create_ledger_engine(settings)
    log = LedgerRunLog(engine)

    try:
        if arguments.collector == "bars":
            records = _run_bars(arguments, settings, provider, writer, log)
        else:
            records = _run_chains(arguments, settings, provider, writer, log)
    except MarketClosed as refusal:
        logger.warning("Collection refused: %s", refusal)
        return EXIT_REFUSED
    finally:
        engine.dispose()

    rows = sum(record.rows_written for record in records)
    gaps = sum(len(record.gaps) for record in records)
    logger.info(
        "Collection complete",
        extra={"Rows": rows, "Gaps": gaps, "Runs": len(records)},
    )
    return 0


def build_provider(settings: Settings) -> MarketDataProvider:
    """The configured market data source.

    One implementation today, and the branch that chooses between two is the
    only line Phase 8 adds to this package. Deliberately not a registry keyed
    on a settings string: a provider needs its own configuration, so a lookup
    table would still need a branch to build one, and the branch on its own is
    honest about how many there are.
    """
    return SyntheticMarketDataProvider(settings.synthetic)


def _run_bars(
    arguments: argparse.Namespace,
    settings: Settings,
    provider: MarketDataProvider,
    writer: LakeWriter,
    log: LedgerRunLog,
) -> list[RunRecord]:
    collector = BarCollector(provider=provider, writer=writer, log=log)
    symbols = settings.collection.bar_symbols
    intervals = (
        tuple(Interval(value) for value in arguments.intervals)
        if arguments.intervals
        else settings.collection.bar_intervals
    )

    # One run per interval, not one run covering both. They write to different
    # partitions and can fail independently, and a single `ingest_run` row
    # covering a minute-bar collection that worked and a daily one that did not
    # would report the whole thing as failed.
    records: list[RunRecord] = []
    for interval in intervals:
        if arguments.mode == "backfill":
            end = arguments.end or _today(arguments)
            start = arguments.start or _backfill_start(provider, settings, end)
            records.append(collector.backfill(symbols, interval, start, end))
        elif arguments.mode == "latest":
            # Catch-up. Whatever the most recent session was, collect it - which
            # is what a person re-running a missed collection means, and is
            # idempotent if that session is already complete.
            records.append(collector.latest_session(symbols, interval, on=_today(arguments)))
        else:
            # What the after-the-close CronJob runs. *Today's* session, and a
            # refusal if today was not one: a holiday run that quietly
            # re-collected yesterday would exit 0 and advance the last-success
            # metric over a day nothing was collected.
            records.append(collector.incremental(symbols, interval, _today(arguments)))
    return records


def _run_chains(
    arguments: argparse.Namespace,
    settings: Settings,
    provider: MarketDataProvider,
    writer: LakeWriter,
    log: LedgerRunLog,
) -> list[RunRecord]:
    collector = ChainCollector(provider=provider, writer=writer, log=log)
    watchlist = settings.collection.chain_watchlist
    every = settings.collection.chain_snapshot_minutes
    at_close = settings.collection.chain_snapshot_at_close

    if arguments.session is not None:
        sessions = provider.market_hours(arguments.session, arguments.session)
        if not sessions:
            raise MarketClosed(f"{arguments.session} is not a session")
        return [collector.collect_session(watchlist, sessions[0], every, include_close=at_close)]

    moment = arguments.at if arguments.at is not None else datetime.now(UTC)
    if moment.tzinfo is None:
        raise SystemExit("--at must be timezone-aware, for example 2026-01-02T15:00:00+00:00")
    return [collector.snapshot(watchlist, moment)]


def _today(arguments: argparse.Namespace) -> date:
    """The day this run is for.

    Overridable with ``--on`` so that a re-run of a missed collection names the
    day it is catching up on, and so that a test can be Thanksgiving without
    waiting for November.
    """
    supplied: date | None = getattr(arguments, "on", None)
    return supplied if supplied is not None else datetime.now(UTC).date()


def _backfill_start(provider: MarketDataProvider, settings: Settings, end: date) -> date:
    """How far back an unbounded backfill goes.

    ``backfill_sessions`` sessions, counted on the calendar rather than
    approximated in calendar days: 252 sessions is a year of trading and 252
    days is eight months of it, and the difference is four months of missing
    history that nothing would report.
    """
    wanted = settings.collection.backfill_sessions
    # A generous calendar window to count sessions inside. 1.6 days per session
    # comfortably covers weekends and holidays without a loop.
    span = int(wanted * 1.6) + 14
    sessions = provider.market_hours(end - timedelta(days=span), end)
    if not sessions:
        raise MarketClosed(f"no sessions in the {span} days before {end}")
    return sessions[max(0, len(sessions) - wanted)].session


if __name__ == "__main__":
    sys.exit(main())
