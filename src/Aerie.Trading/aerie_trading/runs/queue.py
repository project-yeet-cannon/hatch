"""The work queue, which is the ``run`` table with four extra columns.

docs/plans/trading.md Phase 5: *"A Postgres work queue - SELECT ... FOR UPDATE
SKIP LOCKED, retry counts, visibility timeouts. No Redis, no Celery, no new
infrastructure, and queue depth becomes rows the control panel already reads."*

**Why the queue is not a table of its own.** The last clause decides it. A
``job`` table beside ``run`` makes queue depth a join, and it admits two states
that cannot be represented here at all: a job whose run was deleted, and a run
with two jobs. Putting ``status``, ``attempts``, ``available_at`` and the lease
on the row they schedule means the queue is exactly as consistent as the thing
it is scheduling, by construction.

**Claiming: one statement, and the lock does the arbitration.** ``FOR UPDATE
SKIP LOCKED`` inside a CTE is Postgres' own idiom for a queue: each worker
takes a row lock on the first claimable row nobody else holds, skipping past
the ones that are held rather than waiting behind them. Two workers therefore
never see the same row, without a coordinator and without an advisory lock
whose scope somebody has to remember.

**Losing a worker: the lease, and the fence.** A worker that dies holding a run
leaves it ``running`` with a lease that stops being renewed; ``reclaim_expired``
puts it back on the queue, or fails it if its attempts are spent. That much is
an ordinary visibility timeout, and on its own it is not enough - because the
"dead" worker may not be dead. It may be a pod that was frozen for twenty
minutes and is about to wake up and write a result for a run somebody else has
since completed.

So every write is fenced on ``lease_token``: a UUID minted at claim, handed to
the worker, and required to still match for a result to land. A worker whose
lease was reclaimed updates zero rows, discards its result and says so. That is
the whole of the plan's gate - *"killing a worker mid-sweep loses no runs and
duplicates none"* - and the two halves are answered by different mechanisms on
purpose: the lease is what stops work being **lost**, and the fence is what
stops it being **duplicated**. A visibility timeout alone gives you the first
and quietly costs you the second.

Duplicated *execution* remains possible and is the price of the design: two
workers may compute the same backtest, and exactly one may record it. That is
the right side of the trade for a pure function of its inputs, and a backtest
is one.

**Nothing here holds a transaction open across a backtest.** Every method opens
a session, does its statement and commits, on the same argument
``collect/runs.LedgerRunLog`` makes: a run takes seconds to minutes, and a
connection pinned for its duration is one that blocks a CNPG failover's drain.
The lease is what stands in for the lock a long transaction would have been.
"""

from __future__ import annotations

import logging
import uuid
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from typing import Final

from sqlalchemy import text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.db.models import RunMetric, RunStatus, Trade
from aerie_trading.engine.backtest import BacktestResult
from aerie_trading.providers.base import Interval
from aerie_trading.runs.catalog import ensure_instrument
from aerie_trading.runs.costs import CostSpec

__all__ = [
    "LEASE_LOST",
    "ClaimedRun",
    "QueueDepth",
    "RunQueue",
    "SweepProgress",
    "cancel_sweep",
    "read_queue_depth",
    "sweep_progress",
]

logger = logging.getLogger(__name__)

#: What is recorded against a run whose lease expired while a worker held it.
#: A constant because it is asserted in a test and read by an operator, and
#: because the two states it produces - requeued, or failed for good - differ
#: only in the attempt count.
LEASE_LOST: Final = "the worker holding this run stopped renewing its lease"


@dataclass(frozen=True, slots=True)
class ClaimedRun:
    """One run, leased to this worker, with everything needed to execute it.

    Self-contained on purpose: the worker holds no session while it runs a
    backtest, so a lazily-loaded relationship would be an error at the moment
    it was read. Everything a ``run_backtest`` call needs is copied out under
    the claim's transaction and handed over as values.
    """

    id: int
    sweep_id: int | None
    lease_token: uuid.UUID
    attempts: int
    max_attempts: int
    strategy: str
    params: Mapping[str, object]
    symbols: tuple[str, ...]
    interval: Interval
    window_start: datetime
    window_end: datetime
    starting_cash: Decimal
    costs: CostSpec

    @property
    def history_key(self) -> tuple[str, ...]:
        """What two runs must agree on to share a loaded history."""
        return (
            self.interval.value,
            self.window_start.isoformat(),
            self.window_end.isoformat(),
            *self.symbols,
        )


@dataclass(frozen=True, slots=True)
class QueueDepth:
    """How many runs are in each state. What ``/metrics`` exposes."""

    status: str
    runs: int


@dataclass(frozen=True, slots=True)
class SweepProgress:
    """One sweep's completion, derived from its runs rather than stored."""

    sweep_id: int
    name: str
    total_runs: int
    cancelled: bool
    by_status: Mapping[str, int]

    @property
    def finished(self) -> int:
        return sum(
            count for status, count in self.by_status.items() if RunStatus(status).is_terminal
        )

    @property
    def is_complete(self) -> bool:
        return self.finished >= self.total_runs


# The claim. One statement, and every clause in it is load-bearing:
#
#   - `status = 'queued' AND available_at <= now()` is what the partial index
#     ix_run_claimable covers, so this reads an index over the queued minority
#     rather than a table that grows forever.
#   - ORDER BY matches that index's column order exactly. A different order
#     here would be a sort of the whole candidate set on every claim.
#   - FOR UPDATE SKIP LOCKED is the arbitration. Without SKIP LOCKED, ten
#     workers serialise behind one row lock and the queue runs at one worker's
#     speed while looking busy.
#   - LIMIT 1: a worker takes one run at a time. Batching would return leases
#     on runs that then sit idle behind a slow one in the same batch, which is
#     the classic way a work queue develops a tail.
_CLAIM_SQL = text(
    """
    WITH picked AS (
        SELECT id FROM run
        WHERE status = 'queued' AND available_at <= now()
        ORDER BY priority, available_at, id
        FOR UPDATE SKIP LOCKED
        LIMIT 1
    )
    UPDATE run
       SET status = 'running',
           attempts = run.attempts + 1,
           lease_token = :token,
           lease_expires_at = now() + make_interval(secs => :lease_seconds),
           leased_by = :worker,
           -- COALESCE, so a reclaimed run keeps the instant it was first
           -- picked up. "How long has this been in flight" is a question
           -- about the run, not about its latest attempt.
           started_at = COALESCE(run.started_at, now())
      FROM picked
     WHERE run.id = picked.id
 RETURNING run.id, run.sweep_id, run.attempts, run.max_attempts,
           run.param_set_id, run.symbols, run.interval,
           run.window_start, run.window_end, run.starting_cash, run.costs
    """
)

# The detail a claim needs and the UPDATE cannot return: the strategy's name
# and the parameters, which live one join away. A second statement inside the
# same transaction rather than a join on the UPDATE, because an UPDATE ... FROM
# that joined three tables would be one where the row being locked is harder to
# see than the rows being read.
_CLAIMED_DETAIL_SQL = text(
    """
    SELECT s.name AS strategy, p.params AS params
      FROM run r
      JOIN strategy s ON s.id = r.strategy_id
      JOIN param_set p ON p.id = r.param_set_id
     WHERE r.id = :run_id
    """
)

# The reaper. One statement for both outcomes, because "requeue it" and "give
# up on it" differ only in whether the attempts are spent, and two statements
# would leave a window in which a run is in neither.
#
# `error` is set rather than appended to: the useful error on a run that was
# reclaimed three times is the reason it was reclaimed, and a concatenation of
# three identical sentences is not more informative.
_RECLAIM_SQL = text(
    """
    UPDATE run
       SET status = CASE WHEN attempts >= max_attempts THEN 'failed' ELSE 'queued' END,
           finished_at = CASE WHEN attempts >= max_attempts THEN now() ELSE NULL END,
           available_at = now() + make_interval(secs => :backoff_seconds),
           lease_token = NULL,
           lease_expires_at = NULL,
           error = :reason
     WHERE status = 'running' AND lease_expires_at < now()
 RETURNING id, status
    """
)

_DEPTH_SQL = text("SELECT status, COUNT(*) AS runs FROM run GROUP BY status ORDER BY status")


class RunQueue:
    """Claiming, completing and failing runs, against the Ledger.

    Holds an ``Engine`` and opens a short session per call - see the module
    docstring. Concrete rather than a Protocol with a stub beside it, unlike
    ``db.Database`` and ``collect.RunLog``: the behaviour worth testing here is
    not a failure path a fake can stand in for, it is ``SKIP LOCKED`` and a
    conditional update, and a fake implementing those in Python would be a test
    of the fake. ``tests/test_run_queue.py`` runs against a real Postgres or
    skips.
    """

    def __init__(
        self,
        engine: Engine,
        worker: str,
        lease_seconds: int = 900,
        retry_backoff_seconds: int = 0,
    ) -> None:
        self._engine = engine
        self._worker = worker[:128]
        self._lease_seconds = lease_seconds
        self._retry_backoff_seconds = retry_backoff_seconds

    @property
    def worker(self) -> str:
        return self._worker

    # -- taking work ---------------------------------------------------------

    def claim(self) -> ClaimedRun | None:
        """Lease the next claimable run, or ``None`` if there is nothing to do."""
        token = uuid.uuid4()
        with Session(self._engine) as session, session.begin():
            row = (
                session.execute(
                    _CLAIM_SQL,
                    {
                        "token": token,
                        "lease_seconds": self._lease_seconds,
                        "worker": self._worker,
                    },
                )
                .mappings()
                .first()
            )
            if row is None:
                return None
            detail = session.execute(_CLAIMED_DETAIL_SQL, {"run_id": row["id"]}).mappings().one()
            return ClaimedRun(
                id=int(row["id"]),
                sweep_id=None if row["sweep_id"] is None else int(row["sweep_id"]),
                lease_token=token,
                attempts=int(row["attempts"]),
                max_attempts=int(row["max_attempts"]),
                strategy=str(detail["strategy"]),
                params=dict(detail["params"]),
                symbols=tuple(str(symbol) for symbol in row["symbols"]),
                interval=Interval(str(row["interval"])),
                window_start=row["window_start"],
                window_end=row["window_end"],
                starting_cash=row["starting_cash"],
                costs=CostSpec.from_blob(row["costs"]),
            )

    def reclaim_expired(self, backoff_seconds: int = 0) -> Sequence[tuple[int, str]]:
        """Return every expired lease to the queue, or fail it if spent.

        Called by a worker before each claim rather than by a separate reaper
        process or CronJob. Three reasons, in order of weight: a worker is
        already connected and already about to ask the queue a question, so
        this costs one statement on an indexed set that is usually empty; a
        reaper is a second deployment whose own death is silent; and the moment
        this most needs to run is the moment a worker died, which is precisely
        when its replacement is starting up and calling this.

        **No backoff by default, unlike ``fail``.** A backoff exists so that a
        retry does not immediately hit whatever just broke; a lease expiry says
        nobody is holding the run at all, so there is nothing to wait for and
        delaying is delaying a sweep for the length of a node drain. The
        attempt count is still what stops a run that reliably kills its worker
        from cycling forever.
        """
        with Session(self._engine) as session, session.begin():
            rows = session.execute(
                _RECLAIM_SQL,
                {"backoff_seconds": backoff_seconds, "reason": LEASE_LOST},
            ).all()
        outcome = [(int(row[0]), str(row[1])) for row in rows]
        for run_id, status in outcome:
            logger.warning(
                "Reclaimed a run whose lease expired",
                extra={"RunId": run_id, "Status": status},
            )
        return outcome

    # -- finishing work ------------------------------------------------------

    def succeed(
        self,
        claimed: ClaimedRun,
        result: BacktestResult,
        metrics: Mapping[str, Decimal],
        data_fingerprint: str,
        revision: str,
    ) -> bool:
        """Record a completed run, its blotter and its metrics. Fenced.

        Returns ``False`` and writes nothing when the lease was lost, which is
        not an error: it means another worker was handed this run and either
        has finished it or will. The caller logs and moves on.

        The status update goes **first** in the transaction, so the trades and
        metrics that follow are written only on the path where the fence held.
        Doing it the other way - blotter first, status last - would leave a
        successful transaction impossible to distinguish from a rolled-back one
        without reading the trade table.
        """
        with Session(self._engine) as session, session.begin():
            # `session.connection()` rather than `session.execute`: the same
            # transaction either way, but a Connection returns a CursorResult
            # and therefore a typed `rowcount`, which is the entire answer this
            # statement is being asked for.
            updated = (
                session.connection()
                .execute(
                    text(
                        """
                    UPDATE run
                       SET status = 'succeeded',
                           finished_at = now(),
                           duration_ms = EXTRACT(EPOCH FROM (now() - started_at)) * 1000,
                           bars = :bars,
                           aerie_revision = :revision,
                           data_fingerprint = :data_fingerprint,
                           result_fingerprint = :result_fingerprint,
                           error = NULL,
                           lease_token = NULL,
                           lease_expires_at = NULL
                     WHERE id = :run_id AND lease_token = :token AND status = 'running'
                    """
                    ),
                    {
                        "run_id": claimed.id,
                        "token": claimed.lease_token,
                        "bars": result.bars,
                        "revision": revision,
                        "data_fingerprint": data_fingerprint,
                        "result_fingerprint": result.fingerprint(),
                    },
                )
                .rowcount
            )
            if updated == 0:
                # Rolling back rather than committing an empty transaction is
                # not cosmetic: `ensure_instrument` below would otherwise be
                # reached on a path where the run it is for is not ours.
                session.rollback()
                logger.warning(
                    "Discarded a result whose lease had been reclaimed",
                    extra={"RunId": claimed.id, "Strategy": claimed.strategy},
                )
                return False

            # One lookup per distinct instrument rather than per fill: a sweep
            # over one symbol has one, and a run with four hundred fills would
            # otherwise be four hundred SELECTs against a table with one row in
            # it that matters.
            instrument_ids: dict[str, int] = {}
            for fill in result.fills:
                if fill.instrument.symbol not in instrument_ids:
                    row = ensure_instrument(session, fill.instrument)
                    session.flush()
                    instrument_ids[fill.instrument.symbol] = row.id

            session.add_all(
                Trade(
                    run_id=claimed.id,
                    sequence=sequence,
                    instrument_id=instrument_ids[fill.instrument.symbol],
                    filled_at=fill.filled_at,
                    quantity=fill.quantity,
                    price=fill.price,
                    reference_price=fill.reference_price,
                    commission=fill.commission,
                    realized_pnl=fill.realized_pnl,
                    position_key=fill.position_key,
                    tag=fill.order.tag,
                )
                for sequence, fill in enumerate(result.fills)
            )
            session.add_all(
                RunMetric(run_id=claimed.id, name=name, value=value)
                for name, value in metrics.items()
            )
        return True

    def fail(self, claimed: ClaimedRun, error: str) -> bool:
        """Record a run that raised. Requeues it unless its attempts are spent.

        Fenced like ``succeed``, and for the same reason: a worker that was
        declared dead and then discovered its own failure must not overwrite
        the state of the attempt that replaced it.

        Requeued behind ``retry_backoff_seconds``, unlike a reclaimed lease:
        the failures this retries are a database failing over or a transient
        read, and an instant retry is a second attempt at whatever just did not
        work.

        The error is recorded on the *requeued* row as well as on the failed
        one. A run that is retried and then succeeds clears it (``succeed``
        sets ``error = NULL``), so a non-null error on a succeeded run is a
        state this queue does not produce - and one on a queued run is the
        reason it is waiting, which is the thing an operator wants while a
        sweep is stuck rather than after it has given up.
        """
        with Session(self._engine) as session, session.begin():
            updated = (
                session.connection()
                .execute(
                    text(
                        """
                    UPDATE run
                       SET status = CASE WHEN attempts >= max_attempts
                                         THEN 'failed' ELSE 'queued' END,
                           finished_at = CASE WHEN attempts >= max_attempts
                                              THEN now() ELSE NULL END,
                           available_at = now() + make_interval(secs => :backoff_seconds),
                           lease_token = NULL,
                           lease_expires_at = NULL,
                           error = :error
                     WHERE id = :run_id AND lease_token = :token AND status = 'running'
                    """
                    ),
                    {
                        "run_id": claimed.id,
                        "token": claimed.lease_token,
                        "error": error[:4000],
                        "backoff_seconds": 0,
                    },
                )
                .rowcount
            )
        return updated > 0

    # -- looking at it -------------------------------------------------------

    def depth(self) -> Sequence[QueueDepth]:
        with Session(self._engine) as session:
            rows = session.execute(_DEPTH_SQL).mappings().all()
        return [QueueDepth(status=str(row["status"]), runs=int(row["runs"])) for row in rows]


def read_queue_depth(engine: Engine) -> Sequence[QueueDepth]:
    """Runs by status, for the ``/metrics`` collector.

    A free function rather than a method because the control plane has no
    worker identity and no lease to hold - it only ever reads. Handing it a
    ``RunQueue`` would mean inventing a worker name for a process that never
    claims anything, and a name in ``leased_by`` that never appears is a
    confusing thing to leave lying in a table.
    """
    with engine.connect() as connection:
        rows = connection.execute(_DEPTH_SQL).mappings().all()
    return [QueueDepth(status=str(row["status"]), runs=int(row["runs"])) for row in rows]


def cancel_sweep(engine: Engine, sweep_id: int) -> int:
    """Stop a sweep. Returns how many queued runs were cancelled.

    **Runs already in flight are allowed to finish, and that is deliberate.**
    Killing them would mean either a second channel a worker polls mid-backtest
    - which the engine loop has no callback for and should not grow one for
    this - or leaving rows ``running`` with nobody holding them, waiting on the
    lease to expire before anything reports the truth. What in-flight work
    costs is bounded by the number of workers, which is small and known;
    what stopping it would cost is a hole in the run loop.

    ``cancelled_at`` on the sweep is what records the intent, so a run that
    completes after the cancellation is legible as exactly that rather than as
    a cancellation that did not take.
    """
    with Session(engine) as session, session.begin():
        connection = session.connection()
        connection.execute(
            text("UPDATE sweep SET cancelled_at = COALESCE(cancelled_at, now()) WHERE id = :id"),
            {"id": sweep_id},
        )
        cancelled = connection.execute(
            text(
                """
                UPDATE run
                   SET status = 'cancelled', finished_at = now()
                 WHERE sweep_id = :id AND status = 'queued'
                """
            ),
            {"id": sweep_id},
        ).rowcount
    return cancelled


def sweep_progress(engine: Engine, sweep_id: int) -> SweepProgress:
    """One sweep's counts, derived from its runs. Raises if there is no such sweep."""
    with engine.connect() as connection:
        header = (
            connection.execute(
                text("SELECT name, total_runs, cancelled_at FROM sweep WHERE id = :id"),
                {"id": sweep_id},
            )
            .mappings()
            .first()
        )
        if header is None:
            raise LookupError(f"no sweep with id {sweep_id}")
        rows = (
            connection.execute(
                text(
                    "SELECT status, COUNT(*) AS runs FROM run WHERE sweep_id = :id GROUP BY status"
                ),
                {"id": sweep_id},
            )
            .mappings()
            .all()
        )
    return SweepProgress(
        sweep_id=sweep_id,
        name=str(header["name"]),
        total_runs=int(header["total_runs"]),
        cancelled=header["cancelled_at"] is not None,
        by_status={str(row["status"]): int(row["runs"]) for row in rows},
    )
