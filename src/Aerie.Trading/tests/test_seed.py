"""The seed job, and the two properties a deploy depends on.

docs/plans/trading.md Phase 7 gates on *"re-running the seed job changes
nothing and destroys nothing"*, and that is not one claim but two: the second
run must not enqueue the demo again, and it must not touch anything the
operator added. Both are asserted here against a real Ledger, because both are
about what a query finds.

The collection step is exercised separately and over a *short* window: what is
being tested is that the seed asks the Lake before filling it, and stretching
the fixture to the demo's five years would make the test slow without
asserting anything the collector's own tests do not.
"""

from __future__ import annotations

from datetime import UTC, date, datetime, timedelta
from pathlib import Path

import pytest
from sqlalchemy import text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.collect.config import CollectionConfig
from aerie_trading.honesty.config import WalkForwardSpec
from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.runs.demo import DEMO_SWEEPS
from aerie_trading.runs.sweep import SweepSpec, enqueue_sweep, plan_sweep
from aerie_trading.seed.seeding import ensure_history, seed
from aerie_trading.settings import Settings
from aerie_trading.strategies import SPECS

SYMBOL = "ZVZZT"
REVISION = "e" * 40


def settings(lake_root: Path) -> Settings:
    return Settings(  # pyright: ignore[reportCallIssue]
        lake_root=lake_root,
        collection=CollectionConfig(bar_symbols=(SYMBOL,)),
    )


@pytest.fixture
def provider() -> SyntheticMarketDataProvider:
    return SyntheticMarketDataProvider()


def counts(ledger: Engine) -> dict[str, int]:
    with ledger.connect() as connection:
        return {
            table: int(connection.execute(text(f"SELECT count(*) FROM {table}")).scalar_one())
            for table in ("strategy", "sweep", "run", "param_set", "data_source")
        }


# -- what a first boot produces ----------------------------------------------


def test_the_first_run_registers_the_shipped_strategies_and_enqueues_the_demo(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    # The whole point of the phase: a control panel deployed against an empty
    # database is a shipped framework, and this is what stops it being one.
    report = seed(ledger, settings(tmp_path), provider, revision=REVISION, collect=False)

    assert report.strategies == len(SPECS)
    assert sorted(report.sweeps) == sorted(spec.name for spec in DEMO_SWEEPS)
    after = counts(ledger)
    assert after["strategy"] == len(SPECS)
    assert after["sweep"] == len(DEMO_SWEEPS)
    # Queue rows, and not a single result: the demo is computed by the real
    # workers through the real engine. Committed result rows would be a
    # decoration that proves nothing and rots the first time a metric
    # definition changes.
    assert after["run"] > 0
    with ledger.connect() as connection:
        statuses = connection.execute(text("SELECT DISTINCT status FROM run")).scalars().all()
        metrics = connection.execute(text("SELECT count(*) FROM run_metric")).scalar_one()
    assert set(statuses) == {"queued"}
    assert metrics == 0


def test_everything_it_wrote_is_marked_as_its_own(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    seed(ledger, settings(tmp_path), provider, revision=REVISION, collect=False)

    with ledger.connect() as connection:
        unmarked = connection.execute(
            text("SELECT count(*) FROM sweep WHERE NOT seeded")
        ).scalar_one()
    assert unmarked == 0


def test_the_demo_carries_this_installations_universe(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    # Not the constants' universe. An installation that replaced its symbols
    # would otherwise get a seeded demo whose every run failed with "collect
    # before backtesting", which is a worse first impression than no demo.
    seed(ledger, settings(tmp_path), provider, revision=REVISION, collect=False)

    with ledger.connect() as connection:
        universes = connection.execute(text("SELECT DISTINCT symbols FROM run")).scalars().all()
    assert {tuple(entry) for entry in universes} == {(SYMBOL,)}


# -- what a second deploy must not do ----------------------------------------


def test_running_it_again_changes_nothing(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    # This runs on *every* deploy. "Changes nothing" therefore has to mean
    # nothing: no second grid, no second sweep row, no re-enqueued baseline.
    configured = settings(tmp_path)
    seed(ledger, configured, provider, revision=REVISION, collect=False)
    before = counts(ledger)

    again = seed(ledger, configured, provider, revision=REVISION, collect=False)

    assert again.sweeps == []
    assert sorted(again.existing) == sorted(spec.name for spec in DEMO_SWEEPS)
    assert again.changed is False or again.strategies > 0
    assert counts(ledger) == before


def test_it_leaves_an_operators_sweep_alone_even_under_the_same_name(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    # `sweep.name` is deliberately not unique, so an operator can and will
    # collide with a demo's name. The flag rather than the name is what makes
    # the reconcile safe - the plan is explicit that the seed *"must never
    # touch a strategy, sweep or run the owner added"*.
    configured = settings(tmp_path)
    seed(ledger, configured, provider, revision=REVISION, collect=False)
    mine = SweepSpec(
        name=DEMO_SWEEPS[0].name,
        strategy="buy_and_hold",
        symbols=(SYMBOL,),
        window_start=datetime(2024, 1, 1, tzinfo=UTC),
        window_end=datetime(2024, 6, 1, tzinfo=UTC),
        walk_forward=None,
    )
    plan = plan_sweep(mine)
    with Session(ledger) as session, session.begin():
        source_id = session.execute(text("SELECT id FROM data_source")).scalar_one()
        my_sweep = enqueue_sweep(session, plan, plan.total, data_source_id=source_id)
    before = counts(ledger)

    seed(ledger, configured, provider, revision=REVISION, collect=False)

    assert counts(ledger) == before
    with ledger.connect() as connection:
        still_mine = connection.execute(
            text("SELECT seeded FROM sweep WHERE id = :id"), {"id": my_sweep}
        ).scalar_one()
    assert still_mine is False


def test_a_seeded_sweep_an_operator_cancelled_is_not_re_enqueued(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    # Cancelling the demo is a decision, and a deploy that undid it would be a
    # job arguing with the operator once per push. Re-seeding it is an edit
    # here, or a `sweep` row deleted by hand - both of which are somebody
    # deciding rather than a schedule.
    configured = settings(tmp_path)
    seed(ledger, configured, provider, revision=REVISION, collect=False)
    with ledger.begin() as connection:
        connection.execute(text("UPDATE sweep SET cancelled_at = now()"))
        connection.execute(text("UPDATE run SET status = 'cancelled' WHERE status = 'queued'"))
    before = counts(ledger)

    again = seed(ledger, configured, provider, revision=REVISION, collect=False)

    assert again.sweeps == []
    assert counts(ledger) == before


# -- the history the demo reads ----------------------------------------------


def short_specs() -> tuple[SweepSpec, ...]:
    """The demo's shape over three months, which is what a test can afford."""
    return (
        SweepSpec(
            name="short-demo",
            strategy="buy_and_hold",
            symbols=(SYMBOL,),
            interval=Interval.ONE_DAY,
            window_start=datetime(2024, 1, 1, tzinfo=UTC),
            window_end=datetime(2024, 4, 1, tzinfo=UTC),
            walk_forward=WalkForwardSpec(folds=2, train_multiple=2),
        ),
    )


def test_an_empty_lake_is_filled_so_the_seeded_runs_have_something_to_read(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    # Without this the seeded leaderboard on a cold cluster is twenty runs that
    # all failed, on the front page, until somebody notices.
    written = ensure_history(ledger, settings(tmp_path), provider, short_specs())

    assert written > 0
    with LakeReader(tmp_path) as reader:
        bars = reader.bars(
            [SYMBOL],
            Interval.ONE_DAY,
            datetime(2024, 1, 1, tzinfo=UTC),
            datetime(2024, 4, 1, tzinfo=UTC),
        )
    assert not bars.is_empty()


def test_a_lake_that_already_has_the_window_is_left_alone(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    # Five years of generated Parquet on every deploy is the cost of getting
    # this check wrong, and it is invisible - the writes are idempotent, so the
    # only symptom is a job that takes minutes for no reason.
    writer = LakeWriter(root=tmp_path, provenance=Provenance("synthetic", REVISION))
    sessions = provider.market_hours(date(2023, 12, 1), date(2024, 4, 30))
    writer.write_bars(
        provider.bars(
            [SYMBOL],
            Interval.ONE_DAY,
            sessions[0].open,
            sessions[-1].close + timedelta(minutes=1),
        )
    )

    assert ensure_history(ledger, settings(tmp_path), provider, short_specs()) == 0


def test_a_symbol_the_lake_is_missing_makes_the_window_uncovered(
    ledger: Engine, tmp_path: Path, provider: SyntheticMarketDataProvider
) -> None:
    # A universe that gained a symbol has a Lake that is full for the old ones
    # and empty for the new one. A coverage check on the total would call that
    # covered - a seeded leaderboard with one symbol silently missing from
    # every run.
    writer = LakeWriter(root=tmp_path, provenance=Provenance("synthetic", REVISION))
    sessions = provider.market_hours(date(2023, 12, 1), date(2024, 4, 30))
    writer.write_bars(
        provider.bars(
            [SYMBOL],
            Interval.ONE_DAY,
            sessions[0].open,
            sessions[-1].close + timedelta(minutes=1),
        )
    )
    widened = (short_specs()[0].model_copy(update={"symbols": (SYMBOL, "ZWZZT")}),)

    assert ensure_history(ledger, settings(tmp_path), provider, widened) > 0
