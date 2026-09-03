"""Phase 5's queue gate, against a real Postgres.

*"A 1,000-run sweep completes; killing a worker mid-sweep loses no runs and
duplicates none."*

Every test here skips unless ``TRADING_TEST_DATABASE_URL`` names a scratch
database - see ``tests/conftest.py`` for why that is opt-in rather than
discovered. They are not written against a fake because there is nothing here a
fake could stand in for: ``FOR UPDATE SKIP LOCKED`` deciding which of two
concurrent workers gets a row, a conditional ``UPDATE`` matching zero rows, and
``make_interval`` moving a visibility timeout are all things Postgres does. A
Python reimplementation of them would pass while the SQL was wrong.

**How a dead worker is simulated.** Not by killing a process - a test that
forked a worker and sent it SIGKILL would be measuring the operating system's
scheduler as much as the queue. A worker dies, from the queue's point of view,
exactly when it stops renewing its lease and never writes a result; so a claim
that is then aged past its lease expiry *is* a dead worker, and the interesting
part - what happens when that worker turns out not to be dead and tries to
write - is a thing a real SIGKILL could not test at all.
"""

from __future__ import annotations

import io
import uuid
from collections.abc import Mapping, Sequence
from contextlib import redirect_stdout
from dataclasses import replace
from datetime import UTC, datetime, timedelta
from decimal import Decimal
from pathlib import Path

import pytest
from alembic import command
from alembic.config import Config
from sqlalchemy import text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.db.models import DataSource, RunStatus
from aerie_trading.engine.backtest import run_backtest
from aerie_trading.engine.broker import ZERO_COSTS
from aerie_trading.engine.history import BarHistory
from aerie_trading.engine.metrics import compute_metrics
from aerie_trading.providers.base import Interval
from aerie_trading.runs.queue import (
    LEASE_LOST,
    ClaimedRun,
    RunQueue,
    cancel_sweep,
    read_queue_depth,
    sweep_progress,
)
from aerie_trading.runs.sweep import SweepSpec, enqueue_sweep, plan_sweep
from tests.conftest import ScriptedStrategy, flat_bars

ROOT = Path(__file__).resolve().parent.parent
WINDOW = (datetime(2024, 1, 1, tzinfo=UTC), datetime(2025, 1, 1, tzinfo=UTC))


# -- fixtures ---------------------------------------------------------------


@pytest.fixture
def source_id(ledger: Engine) -> int:
    """A ``data_source`` row for the runs to point at."""
    with Session(ledger) as session, session.begin():
        source = DataSource(name="synthetic", description="test", config={})
        session.add(source)
        session.flush()
        return source.id


def sweep_of(strategy: str = "ma_crossover", **overrides: object) -> SweepSpec:
    values: dict[str, object] = {
        "name": "test",
        "strategy": strategy,
        "grid": {"fast": (Decimal(5), Decimal(10)), "slow": (Decimal(20), Decimal(40))},
        "symbols": ("ZVZZT",),
        "interval": Interval.ONE_DAY,
        "window_start": WINDOW[0],
        "window_end": WINDOW[1],
        # No walk-forward row. Phase 6 adds one to every sweep by default, and
        # these tests are about the queue rather than about it: a run count
        # that silently gained one would make every assertion below off by one
        # for a reason that has nothing to do with SKIP LOCKED. The
        # walk-forward's own trip through this queue is asserted in
        # tests/test_worker.py, which has a lake long enough to fold.
        "walk_forward": None,
    }
    values.update(overrides)
    return SweepSpec.model_validate(values)


def enqueue(ledger: Engine, source_id: int, spec: SweepSpec) -> tuple[int, int]:
    """Enqueue ``spec`` and return ``(sweep_id, run_count)``."""
    plan = plan_sweep(spec)
    with Session(ledger) as session, session.begin():
        sweep_id = enqueue_sweep(session, plan, confirm=plan.total, data_source_id=source_id)
    return sweep_id, plan.total


@pytest.fixture
def queue(ledger: Engine) -> RunQueue:
    return RunQueue(ledger, worker="worker-a", lease_seconds=900)


def age_lease(ledger: Engine, run_id: int) -> None:
    """Make one run's lease look expired. See the module docstring."""
    with ledger.begin() as connection:
        connection.execute(
            text("UPDATE run SET lease_expires_at = now() - interval '1 hour' WHERE id = :id"),
            {"id": run_id},
        )


def statuses(ledger: Engine) -> Mapping[str, int]:
    return {depth.status: depth.runs for depth in read_queue_depth(ledger)}


def fake_result(claimed: ClaimedRun) -> tuple[object, ...]:
    """A completed backtest for ``claimed``, computed with no lake involved.

    Four flat bars and a scripted trade, so the blotter has something in it and
    every asserted number is a round one. What is being tested here is the
    queue, not the engine, and building a lake per test would make these the
    slowest tests in the suite for no assertion gained.
    """
    history = BarHistory.from_bars(flat_bars("ZVZZT", [10, 10, 12, 12]))
    result = run_backtest(ScriptedStrategy({1: 100, 3: -100}), history, costs=ZERO_COSTS)
    return (result, compute_metrics(result), "f" * 64, "a" * 40)


def record(queue: RunQueue, claimed: ClaimedRun) -> bool:
    result, metrics, data_fingerprint, revision = fake_result(claimed)
    return queue.succeed(claimed, result, metrics, data_fingerprint, revision)  # type: ignore[arg-type]


# -- the migration, against a live server -----------------------------------


def test_the_migrations_apply_to_a_real_postgres(ledger_engine: Engine) -> None:
    """Offline DDL that Postgres would reject is DDL this suite would not see.

    ``tests/test_migrations.py`` compares the *rendered* statements against the
    models and never sends them anywhere, so a CHECK constraint calling a
    function that does not exist, or a partial index with a malformed
    predicate, would pass it and fail on the first deploy. This runs the whole
    chain forwards and then all the way back on a scratch schema.
    """
    config = Config(str(ROOT / "alembic.ini"))
    config.set_main_option("script_location", str(ROOT / "aerie_trading" / "migrations"))

    with ledger_engine.begin() as connection:
        connection.execute(text("CREATE SCHEMA IF NOT EXISTS migration_rehearsal"))

    # Built in a scratch schema rather than beside the tables `create_all`
    # made in `public`. The two describe the same schema - that is what
    # tests/test_migrations.py asserts, offline - and what is under test here
    # is only whether the server accepts the DDL at all.
    with ledger_engine.begin() as connection:
        connection.execute(text("SET LOCAL search_path TO migration_rehearsal"))
        for statement in _upgrade_statements(config):
            connection.execute(text(statement))
        # And back down, which is the half nobody rehearses until the day they
        # need it.
        for statement in _downgrade_statements(config):
            connection.execute(text(statement))

    with ledger_engine.begin() as connection:
        connection.execute(text("DROP SCHEMA IF EXISTS migration_rehearsal CASCADE"))


def _upgrade_statements(config: Config) -> Sequence[str]:
    buffer = io.StringIO()
    with redirect_stdout(buffer):
        command.upgrade(config, "head", sql=True)
    return _statements(buffer.getvalue())


def _downgrade_statements(config: Config) -> Sequence[str]:
    buffer = io.StringIO()
    with redirect_stdout(buffer):
        command.downgrade(config, "head:base", sql=True)
    return _statements(buffer.getvalue())


def _statements(rendered: str) -> list[str]:
    out: list[str] = []
    for chunk in rendered.split(";"):
        body = "\n".join(
            line for line in chunk.splitlines() if not line.lstrip().startswith(("--", "{"))
        )
        stripped = body.strip()
        if stripped and not stripped.upper().startswith(("BEGIN", "COMMIT")):
            out.append(stripped)
    return out


# -- claiming ---------------------------------------------------------------


def test_enqueueing_writes_one_run_per_parameter_set(ledger: Engine, source_id: int) -> None:
    sweep_id, total = enqueue(ledger, source_id, sweep_of())

    assert total == 4
    assert statuses(ledger) == {RunStatus.QUEUED.value: 4}
    progress = sweep_progress(ledger, sweep_id)
    assert progress.total_runs == 4
    assert progress.finished == 0
    assert not progress.is_complete


def test_a_claim_leases_exactly_one_run_and_moves_it_to_running(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    enqueue(ledger, source_id, sweep_of())

    claimed = queue.claim()

    assert claimed is not None
    assert claimed.strategy == "ma_crossover"
    assert claimed.symbols == ("ZVZZT",)
    assert claimed.interval is Interval.ONE_DAY
    assert claimed.attempts == 1
    assert statuses(ledger) == {RunStatus.QUEUED.value: 3, RunStatus.RUNNING.value: 1}


def test_two_workers_never_claim_the_same_run(ledger: Engine, source_id: int) -> None:
    # The whole point of SKIP LOCKED, asserted the only way it can be: drain
    # the queue with two queues alternating and check that every run came out
    # exactly once.
    enqueue(ledger, source_id, sweep_of())
    first = RunQueue(ledger, worker="worker-a")
    second = RunQueue(ledger, worker="worker-b")

    claimed: list[int] = []
    while True:
        taken = [queue.claim() for queue in (first, second)]
        got = [entry.id for entry in taken if entry is not None]
        claimed.extend(got)
        if not got:
            break

    assert sorted(claimed) == sorted(set(claimed))
    assert len(claimed) == 4


def test_claims_come_out_in_priority_order(ledger: Engine, source_id: int, queue: RunQueue) -> None:
    enqueue(ledger, source_id, sweep_of(name="later", priority=200))
    urgent, _ = enqueue(ledger, source_id, sweep_of(name="sooner", priority=1))

    claimed = queue.claim()

    assert claimed is not None
    assert claimed.sweep_id == urgent


def test_an_empty_queue_answers_none_rather_than_blocking(queue: RunQueue) -> None:
    assert queue.claim() is None


# -- recording --------------------------------------------------------------


def test_a_recorded_run_carries_its_blotter_its_metrics_and_its_fingerprints(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    enqueue(ledger, source_id, sweep_of())
    claimed = queue.claim()
    assert claimed is not None

    assert record(queue, claimed) is True

    with ledger.connect() as connection:
        row = (
            connection.execute(
                text(
                    "SELECT status, bars, aerie_revision, data_fingerprint, result_fingerprint,"
                    " duration_ms, error, lease_token FROM run WHERE id = :id"
                ),
                {"id": claimed.id},
            )
            .mappings()
            .one()
        )
        trades = (
            connection.execute(
                text(
                    "SELECT sequence, quantity, price, realized_pnl FROM trade WHERE run_id = :id"
                    " ORDER BY sequence"
                ),
                {"id": claimed.id},
            )
            .mappings()
            .all()
        )
        metrics = (
            connection.execute(
                text("SELECT name, value FROM run_metric WHERE run_id = :id"),
                {"id": claimed.id},
            )
            .mappings()
            .all()
        )

    assert row["status"] == RunStatus.SUCCEEDED.value
    assert row["bars"] == 4
    assert row["data_fingerprint"] == "f" * 64
    assert len(str(row["result_fingerprint"])) == 64
    assert row["error"] is None
    # The lease is released on completion, so a reaper cannot find a finished
    # run to reclaim.
    assert row["lease_token"] is None
    assert row["duration_ms"] is not None

    # Two fills, in engine order, with the second one realizing 100 * (12 - 10).
    assert [entry["sequence"] for entry in trades] == [0, 1]
    assert [entry["quantity"] for entry in trades] == [100, -100]
    assert trades[1]["realized_pnl"] == Decimal(200)

    names = {str(entry["name"]) for entry in metrics}
    assert "total_return" in names
    assert "trade_count" in names


def test_recording_a_run_registers_the_instruments_it_traded(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    # The blotter references `instrument`, which is what makes Phase 10's
    # "every call within 30 days of expiry" a query rather than string
    # surgery on an OCC symbol.
    enqueue(ledger, source_id, sweep_of())
    claimed = queue.claim()
    assert claimed is not None
    record(queue, claimed)

    with ledger.connect() as connection:
        rows = (
            connection.execute(text("SELECT symbol, kind, multiplier FROM instrument"))
            .mappings()
            .all()
        )

    assert [(str(row["symbol"]), str(row["kind"]), row["multiplier"]) for row in rows] == [
        ("ZVZZT", "equity", 1)
    ]


def test_a_second_run_reuses_the_instrument_row(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    enqueue(ledger, source_id, sweep_of())
    for _ in range(2):
        claimed = queue.claim()
        assert claimed is not None
        record(queue, claimed)

    with ledger.connect() as connection:
        count = connection.execute(text("SELECT COUNT(*) FROM instrument")).scalar_one()

    assert count == 1


# -- failing ----------------------------------------------------------------


def test_a_failed_run_goes_back_on_the_queue_until_its_attempts_are_spent(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    enqueue(ledger, source_id, sweep_of(strategy="buy_and_hold", grid={}, max_attempts=2))

    first = queue.claim()
    assert first is not None
    assert queue.fail(first, "ValueError: no") is True
    assert statuses(ledger) == {RunStatus.QUEUED.value: 1}

    second = queue.claim()
    assert second is not None
    assert second.attempts == 2
    assert queue.fail(second, "ValueError: no") is True
    # Attempts spent, so this time it stays failed rather than cycling forever.
    assert statuses(ledger) == {RunStatus.FAILED.value: 1}

    with ledger.connect() as connection:
        error = connection.execute(text("SELECT error FROM run")).scalar_one()
    assert error == "ValueError: no"


def test_a_retried_run_that_then_succeeds_clears_its_error(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    # A non-null error on a succeeded run is a state this queue must not
    # produce, or "which runs went wrong" stops being answerable with a filter.
    enqueue(ledger, source_id, sweep_of(strategy="buy_and_hold", grid={}))
    first = queue.claim()
    assert first is not None
    queue.fail(first, "transient")

    second = queue.claim()
    assert second is not None
    record(queue, second)

    with ledger.connect() as connection:
        row = connection.execute(text("SELECT status, error FROM run")).mappings().one()
    assert row["status"] == RunStatus.SUCCEEDED.value
    assert row["error"] is None


# -- losing a worker: the gate ----------------------------------------------


def test_an_expired_lease_returns_the_run_to_the_queue(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    """Half one of the gate: killing a worker mid-sweep loses no runs."""
    enqueue(ledger, source_id, sweep_of(strategy="buy_and_hold", grid={}))
    lost = queue.claim()
    assert lost is not None
    age_lease(ledger, lost.id)

    reclaimed = queue.reclaim_expired()

    assert reclaimed == [(lost.id, RunStatus.QUEUED.value)]
    assert statuses(ledger) == {RunStatus.QUEUED.value: 1}
    with ledger.connect() as connection:
        error = connection.execute(text("SELECT error FROM run")).scalar_one()
    assert error == LEASE_LOST

    # And it is claimable again, by anybody.
    again = RunQueue(ledger, worker="worker-b").claim()
    assert again is not None
    assert again.id == lost.id
    assert again.attempts == 2


def test_a_reclaimed_run_whose_attempts_are_spent_fails_rather_than_cycling(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    # A worker that dies deterministically on one run - a strategy that
    # segfaults its interpreter, a pod that OOMs on one parameter set - would
    # otherwise be an infinite loop that looks like a sweep making no progress.
    enqueue(ledger, source_id, sweep_of(strategy="buy_and_hold", grid={}, max_attempts=1))
    lost = queue.claim()
    assert lost is not None
    age_lease(ledger, lost.id)

    assert queue.reclaim_expired() == [(lost.id, RunStatus.FAILED.value)]


def test_a_worker_that_lost_its_lease_cannot_write_a_result(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    """Half two of the gate: and duplicates none.

    The case a visibility timeout on its own gets wrong. The "dead" worker was
    only frozen; it wakes up holding a stale lease and tries to write. The
    fence is what makes that write land nowhere - and it has to, because
    another worker has already been handed the run.
    """
    enqueue(ledger, source_id, sweep_of(strategy="buy_and_hold", grid={}))
    frozen = queue.claim()
    assert frozen is not None
    age_lease(ledger, frozen.id)
    queue.reclaim_expired()

    successor = RunQueue(ledger, worker="worker-b").claim()
    assert successor is not None
    assert successor.id == frozen.id
    assert successor.lease_token != frozen.lease_token

    # The successor records it, and then the frozen worker wakes up.
    assert record(queue, successor) is True
    assert record(queue, frozen) is False
    assert queue.fail(frozen, "too late") is False

    with ledger.connect() as connection:
        runs = connection.execute(text("SELECT COUNT(*) FROM run")).scalar_one()
        trades = connection.execute(text("SELECT COUNT(*) FROM trade")).scalar_one()
        status = connection.execute(text("SELECT status FROM run")).scalar_one()

    # One run, one blotter, and it is the successor's. A queue without the
    # fence would have written the trades twice - and the unique index on
    # (run_id, sequence) is the backstop that would have turned that into a
    # constraint violation rather than a doubled P&L.
    assert runs == 1
    assert trades == 2
    assert status == RunStatus.SUCCEEDED.value


def test_a_stale_token_cannot_write_even_for_a_run_that_is_running(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    # The narrower version: the fence is on the token, not on the status. A
    # check of `status = 'running'` alone would let a stale worker overwrite
    # the attempt that replaced it, which is a wrong answer rather than a
    # duplicated one.
    enqueue(ledger, source_id, sweep_of(strategy="buy_and_hold", grid={}))
    claimed = queue.claim()
    assert claimed is not None
    imposter = replace(claimed, lease_token=uuid.uuid4())

    assert record(queue, imposter) is False
    assert statuses(ledger) == {RunStatus.RUNNING.value: 1}


# -- cancellation -----------------------------------------------------------


def test_cancelling_a_sweep_stops_its_queued_runs_and_lets_the_rest_finish(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    sweep_id, _ = enqueue(ledger, source_id, sweep_of())
    in_flight = queue.claim()
    assert in_flight is not None

    cancelled = cancel_sweep(ledger, sweep_id)

    assert cancelled == 3
    assert statuses(ledger) == {RunStatus.CANCELLED.value: 3, RunStatus.RUNNING.value: 1}
    # The run already in flight finishes and is recorded, which is deliberate -
    # see runs/queue.cancel_sweep. `cancelled_at` on the sweep is what makes
    # that legible rather than confusing.
    assert record(queue, in_flight) is True
    progress = sweep_progress(ledger, sweep_id)
    assert progress.cancelled
    assert progress.is_complete
    assert progress.by_status == {
        RunStatus.CANCELLED.value: 3,
        RunStatus.SUCCEEDED.value: 1,
    }


def test_a_cancelled_run_is_not_claimable(ledger: Engine, source_id: int, queue: RunQueue) -> None:
    sweep_id, _ = enqueue(ledger, source_id, sweep_of())
    cancel_sweep(ledger, sweep_id)

    assert queue.claim() is None


def test_cancelling_twice_keeps_the_first_instant(ledger: Engine, source_id: int) -> None:
    sweep_id, _ = enqueue(ledger, source_id, sweep_of())
    cancel_sweep(ledger, sweep_id)
    with ledger.connect() as connection:
        first = connection.execute(text("SELECT cancelled_at FROM sweep")).scalar_one()

    assert cancel_sweep(ledger, sweep_id) == 0
    with ledger.connect() as connection:
        second = connection.execute(text("SELECT cancelled_at FROM sweep")).scalar_one()
    assert first == second


# -- what the control plane reads -------------------------------------------


def test_queue_depth_reports_every_status_that_has_rows(
    ledger: Engine, source_id: int, queue: RunQueue
) -> None:
    enqueue(ledger, source_id, sweep_of())
    claimed = queue.claim()
    assert claimed is not None

    assert statuses(ledger) == {RunStatus.QUEUED.value: 3, RunStatus.RUNNING.value: 1}


def test_progress_for_a_sweep_that_does_not_exist_is_an_error(ledger: Engine) -> None:
    with pytest.raises(LookupError, match="no sweep"):
        sweep_progress(ledger, 404)


def test_param_sets_are_reused_across_sweeps_of_the_same_grid(
    ledger: Engine, source_id: int
) -> None:
    # The same variation, run over a different window, is the same variation.
    # Ten thousand duplicate rows would make a leaderboard group by a hash it
    # should be joining on.
    enqueue(ledger, source_id, sweep_of(name="first"))
    enqueue(
        ledger,
        source_id,
        sweep_of(name="second", window_end=WINDOW[1] + timedelta(days=365)),
    )

    with ledger.connect() as connection:
        param_sets = connection.execute(text("SELECT COUNT(*) FROM param_set")).scalar_one()
        runs = connection.execute(text("SELECT COUNT(*) FROM run")).scalar_one()
        strategies = connection.execute(text("SELECT COUNT(*) FROM strategy")).scalar_one()

    assert param_sets == 4
    assert runs == 8
    assert strategies == 1
