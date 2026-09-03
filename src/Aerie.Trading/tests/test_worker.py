"""The worker, the lake and the queue joined up - and Phase 5's first gate.

*"A 1,000-run sweep completes."*

These tests run the whole vertical: a provider writes Parquet, a sweep is
planned and enqueued into Postgres, and workers claim runs, read the lake,
backtest, and write blotters and metrics back. Everything below skips unless
``TRADING_TEST_DATABASE_URL`` names a scratch database - see
``tests/conftest.py``.

The thousand-run sweep is deliberately over a *short* history. What is being
measured is the queue and the loop around it - a thousand claims, a thousand
fenced completions, no run executed twice - and stretching each backtest to a
decade of bars would make the test slow without asserting anything the engine's
own tests do not already.
"""

from __future__ import annotations

import threading
from collections.abc import Sequence
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, date, datetime, timedelta
from decimal import Decimal
from pathlib import Path

import pytest
from sqlalchemy import text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.db.models import DataSource, ParamSet, Run, RunStatus, Strategy
from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.runs.config import RunnerConfig
from aerie_trading.runs.costs import CostSpec
from aerie_trading.runs.queue import RunQueue, sweep_progress
from aerie_trading.runs.sweep import SweepSpec, enqueue_sweep, plan_sweep
from aerie_trading.runs.worker import HistoryCache, Worker

SYMBOL = "ZVZZT"
FIRST_SESSION = date(2025, 1, 2)
LAST_SESSION = date(2025, 6, 30)
WINDOW = (datetime(2025, 1, 1, tzinfo=UTC), datetime(2025, 7, 1, tzinfo=UTC))
REVISION = "c" * 40


@pytest.fixture(scope="session")
def lake_root(tmp_path_factory: pytest.TempPathFactory) -> Path:
    """Half a year of daily bars, written the way the collectors write them."""
    root: Path = tmp_path_factory.mktemp("worker-lake")
    provider = SyntheticMarketDataProvider()
    writer = LakeWriter(
        root=root,
        provenance=Provenance("synthetic", REVISION, datetime(2025, 7, 1, tzinfo=UTC)),
    )
    sessions = provider.market_hours(FIRST_SESSION, LAST_SESSION)
    writer.write_bars(
        provider.bars(
            [SYMBOL],
            Interval.ONE_DAY,
            sessions[0].open,
            sessions[-1].close + timedelta(minutes=1),
        )
    )
    return root


@pytest.fixture
def source_id(ledger: Engine) -> int:
    with Session(ledger) as session, session.begin():
        source = DataSource(name="synthetic", description="test", config={})
        session.add(source)
        session.flush()
        return source.id


def spec_for_grid(**overrides: object) -> SweepSpec:
    values: dict[str, object] = {
        "name": "test",
        "strategy": "ma_crossover",
        "grid": {"fast": (Decimal(5), Decimal(10)), "slow": (Decimal(20), Decimal(40))},
        "symbols": (SYMBOL,),
        "interval": Interval.ONE_DAY,
        "window_start": WINDOW[0],
        "window_end": WINDOW[1],
        "costs": CostSpec(),
    }
    values.update(overrides)
    return SweepSpec.model_validate(values)


def enqueue(ledger: Engine, source_id: int, spec: SweepSpec) -> tuple[int, int]:
    plan = plan_sweep(spec)
    with Session(ledger) as session, session.begin():
        sweep_id = enqueue_sweep(
            session, plan, confirm=plan.total, data_source_id=source_id, ceiling=100_000
        )
    return sweep_id, plan.total


def drain(ledger: Engine, lake_root: Path, worker: str = "worker-a") -> tuple[int, int, int]:
    """Run one worker until the queue is empty. Returns (claimed, ok, failed)."""
    with LakeReader(lake_root) as reader:
        loop = Worker(
            queue=RunQueue(ledger, worker=worker, lease_seconds=900),
            cache=HistoryCache(reader, maxsize=2),
            revision=REVISION,
            config=RunnerConfig(poll_seconds=0.01),
        )
        report = loop.run(max_idle_polls=1)
    return report.claimed, report.succeeded, report.failed


def counts(ledger: Engine) -> dict[str, int]:
    with ledger.connect() as connection:
        rows = (
            connection.execute(text("SELECT status, COUNT(*) AS n FROM run GROUP BY status"))
            .mappings()
            .all()
        )
    return {str(row["status"]): int(row["n"]) for row in rows}


# -- the vertical -----------------------------------------------------------


def test_a_worker_drains_a_sweep_and_records_what_it_produced(
    ledger: Engine, source_id: int, lake_root: Path
) -> None:
    sweep_id, total = enqueue(ledger, source_id, spec_for_grid())

    claimed, succeeded, failed = drain(ledger, lake_root)

    assert (claimed, succeeded, failed) == (total, total, 0)
    assert counts(ledger) == {RunStatus.SUCCEEDED.value: total}
    assert sweep_progress(ledger, sweep_id).is_complete

    with ledger.connect() as connection:
        rows = (
            connection.execute(
                text(
                    "SELECT bars, aerie_revision, data_fingerprint, result_fingerprint"
                    "  FROM run ORDER BY id"
                )
            )
            .mappings()
            .all()
        )
        metrics = connection.execute(
            text("SELECT COUNT(DISTINCT run_id) FROM run_metric")
        ).scalar_one()

    # Every run read the same bars over the same window, so the data
    # fingerprints agree - which is the property that makes a leaderboard over
    # them a comparison rather than a coincidence.
    assert len({str(row["data_fingerprint"]) for row in rows}) == 1
    # And every run produced a different answer, because the parameters differ.
    assert len({str(row["result_fingerprint"]) for row in rows}) == total
    assert all(row["aerie_revision"] == REVISION for row in rows)
    assert all(int(row["bars"]) > 100 for row in rows)
    assert metrics == total


def test_the_history_is_read_from_the_lake_once_for_a_whole_sweep(
    ledger: Engine, source_id: int, lake_root: Path
) -> None:
    # Without this, a thousand-run sweep is a thousand DuckDB queries over the
    # same Parquet before a single backtest starts.
    _, total = enqueue(ledger, source_id, spec_for_grid())

    with LakeReader(lake_root) as reader:
        cache = HistoryCache(reader, maxsize=2)
        loop = Worker(
            queue=RunQueue(ledger, worker="worker-a"),
            cache=cache,
            revision=REVISION,
            config=RunnerConfig(poll_seconds=0.01),
        )
        loop.run(max_idle_polls=1)

    assert cache.misses == 1
    assert cache.hits == total - 1


def test_a_run_naming_a_strategy_this_build_does_not_ship_fails_the_run_alone(
    ledger: Engine, source_id: int, lake_root: Path
) -> None:
    """A deploy that is behind must not take a worker down with it.

    The strategy is resolved from the registry by name at execution time, so a
    run enqueued by a newer build is a ``LookupError`` on one run rather than
    a crash loop on the pod. The rest of the sweep still drains.
    """
    enqueue(ledger, source_id, spec_for_grid())
    _enqueue_orphan(ledger, source_id)

    claimed, succeeded, failed = drain(ledger, lake_root)

    assert succeeded == 4
    # Three attempts at the orphan before it gives up, all by the same worker.
    assert failed == 3
    assert claimed == 7
    assert counts(ledger) == {RunStatus.SUCCEEDED.value: 4, RunStatus.FAILED.value: 1}

    with ledger.connect() as connection:
        error = connection.execute(
            text("SELECT error FROM run WHERE status = 'failed'")
        ).scalar_one()
    assert "no strategy named 'departed'" in str(error)


def _enqueue_orphan(ledger: Engine, source_id: int) -> None:
    """A run whose ``strategy`` row names something the registry does not have.

    Written by hand rather than through ``enqueue_sweep``, because the launcher
    correctly refuses to create one - the state under test is a row that a
    *previous* build enqueued legitimately.
    """
    with Session(ledger) as session, session.begin():
        strategy = Strategy(
            name="departed", description="shipped by a newer build", params_schema={}
        )
        session.add(strategy)
        session.flush()
        params = ParamSet(strategy_id=strategy.id, params={}, params_hash="0" * 64)
        session.add(params)
        session.flush()
        session.add(
            Run(
                strategy_id=strategy.id,
                param_set_id=params.id,
                data_source_id=source_id,
                symbols=[SYMBOL],
                interval=Interval.ONE_DAY.value,
                window_start=WINDOW[0],
                window_end=WINDOW[1],
                starting_cash=Decimal(100_000),
                costs=CostSpec().to_blob(),
                status=RunStatus.QUEUED.value,
                # Last, so the sweep above drains first and the failure is
                # visibly not what stopped it.
                priority=900,
            )
        )


def test_a_worker_that_dies_mid_sweep_loses_no_runs_and_duplicates_none(
    ledger: Engine, source_id: int, lake_root: Path
) -> None:
    """The gate, end to end rather than statement by statement.

    One worker claims every run and then dies - which, from the queue's point
    of view, is exactly "stops renewing its lease and never writes a result".
    A second worker then drains, and the assertion is on both halves at once:
    every run finished (nothing lost) and every run has exactly one blotter
    (nothing duplicated).
    """
    _, total = enqueue(ledger, source_id, spec_for_grid())
    doomed = RunQueue(ledger, worker="worker-doomed", lease_seconds=900)
    for _ in range(total):
        assert doomed.claim() is not None
    assert counts(ledger) == {RunStatus.RUNNING.value: total}

    with ledger.begin() as connection:
        connection.execute(text("UPDATE run SET lease_expires_at = now() - interval '1 hour'"))

    claimed, succeeded, failed = drain(ledger, lake_root, worker="worker-successor")

    assert (claimed, succeeded, failed) == (total, total, 0)
    assert counts(ledger) == {RunStatus.SUCCEEDED.value: total}
    with ledger.connect() as connection:
        blotters = (
            connection.execute(text("SELECT run_id, COUNT(*) AS n FROM trade GROUP BY run_id"))
            .mappings()
            .all()
        )
        leased_by = connection.execute(text("SELECT DISTINCT leased_by FROM run")).scalars().all()
    # One blotter per run that traded, and the unique index on
    # (run_id, sequence) is what would have caught a second one.
    assert all(int(row["n"]) > 0 for row in blotters)
    assert set(leased_by) == {"worker-successor"}


# -- the gate ---------------------------------------------------------------


def test_a_thousand_run_sweep_completes(ledger: Engine, source_id: int, lake_root: Path) -> None:
    """Phase 5's first gate, at the size the plan names.

    Forty fast windows by twenty-five slow ones, every combination valid
    because the largest fast is below the smallest slow - so this is a
    thousand runs rather than a thousand-and-something pruned to a number
    nobody chose. Two workers drain it concurrently, which is also the only
    honest way to assert that ``SKIP LOCKED`` scales past one claimer.
    """
    fast = tuple(Decimal(value) for value in range(2, 42))
    slow = tuple(Decimal(value) for value in range(45, 70))
    sweep_id, total = enqueue(
        ledger, source_id, spec_for_grid(name="thousand", grid={"fast": fast, "slow": slow})
    )
    assert total == 1_000

    reports = _drain_concurrently(ledger, lake_root, workers=2)

    assert sum(report[1] for report in reports) == 1_000
    assert sum(report[2] for report in reports) == 0
    assert counts(ledger) == {RunStatus.SUCCEEDED.value: 1_000}
    assert sweep_progress(ledger, sweep_id).is_complete

    with ledger.connect() as connection:
        # Nothing ran twice. `run` rows are unique by construction; what this
        # checks is that no run was *recorded* twice, which is the thing the
        # lease fence exists to guarantee.
        duplicate_blotters = connection.execute(
            text(
                "SELECT COUNT(*) FROM ("
                "  SELECT run_id, sequence FROM trade GROUP BY run_id, sequence HAVING COUNT(*) > 1"
                ") AS duplicates"
            )
        ).scalar_one()
        metric_runs = connection.execute(
            text("SELECT COUNT(DISTINCT run_id) FROM run_metric")
        ).scalar_one()
        # And both workers actually did some of it, or this measured one
        # worker with a spectator beside it.
        by_worker = (
            connection.execute(text("SELECT leased_by, COUNT(*) AS n FROM run GROUP BY leased_by"))
            .mappings()
            .all()
        )

    assert duplicate_blotters == 0
    assert metric_runs == 1_000
    assert len(by_worker) == 2
    assert all(int(row["n"]) > 0 for row in by_worker)


def _drain_concurrently(
    ledger: Engine, lake_root: Path, workers: int
) -> Sequence[tuple[int, int, int]]:
    # A barrier so the workers start claiming at the same moment. Without it
    # the first one can drain the whole queue before the second has opened its
    # DuckDB connection, and the concurrency this test is about would be
    # asserted by a test that never had any.
    gate = threading.Barrier(workers)

    def one(index: int) -> tuple[int, int, int]:
        gate.wait()
        return drain(ledger, lake_root, worker=f"worker-{index}")

    with ThreadPoolExecutor(max_workers=workers) as pool:
        return list(pool.map(one, range(workers)))
