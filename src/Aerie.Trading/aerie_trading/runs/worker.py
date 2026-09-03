"""What a worker pod does: claim a run, backtest it, record it, repeat.

docs/plans/trading.md Phase 5 asks for *"a worker deployment, horizontally
scalable"*, and horizontal scalability is a property of this loop rather than
of the manifest: nothing here coordinates with another worker, holds a lock, or
knows how many peers it has. Two workers and two hundred behave identically
because the queue is what arbitrates (``runs/queue.py``).

**A worker never migrates.** ``deploy/cluster/trading/app/deployment.yaml``
runs Alembic in an init container on the control plane, with one replica and
``strategy: Recreate``, precisely so that exactly one process does. A worker
that migrated on startup would turn every scale-up into a race, which is the
failure the init container exists to prevent.

**The history cache is what makes a sweep cheap.** A thousand runs of one
strategy over one window read the identical bars, and reading them per run
would be a thousand DuckDB queries over the same Parquet before a single
backtest started. The cache is keyed on everything that defines a history, so a
hit is a hit on *the same data* rather than on the same request - and it is
tiny, because a sweep is contiguous: every run after the first hits.

**SIGTERM finishes the run in flight and then stops.** A worker is evicted by a
node drain, a rollout or a scale-down, and the difference between exiting
immediately and finishing the current run is a run that has to be redone versus
one that is already recorded. The lease covers the case where it does not get
that chance, so this is an optimisation rather than a correctness argument -
which is why it is a flag checked between runs rather than an attempt to
interrupt the engine.
"""

from __future__ import annotations

import logging
import signal
import socket
import threading
import time
from collections import OrderedDict
from dataclasses import dataclass
from datetime import datetime
from types import FrameType

from aerie_trading.engine.backtest import BacktestResult, run_backtest
from aerie_trading.engine.history import BarHistory, load_history
from aerie_trading.engine.metrics import compute_metrics
from aerie_trading.lake.reader import LakeReader
from aerie_trading.providers.base import Interval
from aerie_trading.runs.config import RunnerConfig
from aerie_trading.runs.queue import ClaimedRun, RunQueue
from aerie_trading.strategies import spec_for

__all__ = ["HistoryCache", "Worker", "WorkerReport", "default_worker_name", "execute"]

logger = logging.getLogger(__name__)


def default_worker_name() -> str:
    """This process's name in ``run.leased_by``.

    The hostname, which inside a pod is the pod name - the string that turns
    "which worker had this run" into a ``kubectl logs`` rather than a search.
    """
    return socket.gethostname()


class HistoryCache:
    """The last few loaded histories, keyed by everything that defines one.

    An ``OrderedDict`` used as an LRU rather than ``functools.lru_cache``,
    because the loader needs a ``LakeReader`` that belongs to the worker and a
    decorated function would either close over a global one or take it as part
    of the key - and a ``LakeReader`` is not hashable in a way that means
    anything.

    Small by design (``RunnerConfig.history_cache``). A decade of one symbol's
    minute bars is a large object, and holding several sweeps' worth is how a
    worker with a memory limit gets evicted in the middle of a run it had
    almost finished.
    """

    def __init__(self, reader: LakeReader, maxsize: int = 2) -> None:
        self._reader = reader
        self._maxsize = maxsize
        self._entries: OrderedDict[tuple[str, ...], BarHistory] = OrderedDict()
        self.hits = 0
        self.misses = 0

    def load(
        self,
        symbols: tuple[str, ...],
        interval: Interval,
        start: datetime,
        end: datetime,
    ) -> BarHistory:
        key = (interval.value, start.isoformat(), end.isoformat(), *symbols)
        cached = self._entries.get(key)
        if cached is not None:
            self.hits += 1
            self._entries.move_to_end(key)
            return cached
        self.misses += 1
        history = load_history(self._reader, symbols, interval, start, end)
        self._entries[key] = history
        while len(self._entries) > self._maxsize:
            self._entries.popitem(last=False)
        return history


@dataclass(frozen=True, slots=True)
class WorkerReport:
    """What one worker loop did before it stopped. Read by tests and by the CLI."""

    claimed: int
    succeeded: int
    failed: int
    discarded: int
    idle_polls: int

    @property
    def recorded(self) -> int:
        return self.succeeded + self.failed


def execute(claimed: ClaimedRun, cache: HistoryCache) -> tuple[BacktestResult, str]:
    """Run one claimed backtest. Returns the result and the data fingerprint.

    Free of the queue and of the database on purpose: everything it needs was
    copied onto ``ClaimedRun`` under the claim's transaction, and everything it
    produces is a value. That is what lets the loop below hold no session while
    a backtest runs, and it is what lets this be tested against a lake with no
    Postgres anywhere.

    The strategy is resolved from the registry by name rather than carried on
    the row, so a run enqueued by a build that shipped a strategy this one does
    not is a ``LookupError`` naming both - which the caller records as a failed
    run rather than a crashed worker.
    """
    history = cache.load(
        claimed.symbols, claimed.interval, claimed.window_start, claimed.window_end
    )
    spec = spec_for(claimed.strategy)
    result = run_backtest(
        spec.build(claimed.params),
        history,
        starting_cash=claimed.starting_cash,
        costs=claimed.costs.build(),
        name=claimed.strategy,
    )
    return result, history.fingerprint()


class Worker:
    """The loop. Constructed with everything it needs and told to ``run``."""

    def __init__(
        self,
        queue: RunQueue,
        cache: HistoryCache,
        revision: str,
        config: RunnerConfig | None = None,
    ) -> None:
        self._queue = queue
        self._cache = cache
        self._revision = revision
        self._config = config if config is not None else RunnerConfig()
        self._stopping = threading.Event()

    def stop(self) -> None:
        """Ask the loop to finish the run in flight and then return."""
        self._stopping.set()

    @property
    def is_stopping(self) -> bool:
        return self._stopping.is_set()

    def install_signal_handlers(self) -> None:
        """SIGTERM and SIGINT ask for a graceful stop rather than exiting.

        Separate from ``__init__`` because installing a process-wide handler is
        something an entry point does and a test must not: ``signal.signal``
        outside the main thread raises, and inside one it would leak across
        tests in the same interpreter.
        """

        def handle(number: int, _frame: FrameType | None) -> None:
            logger.info(
                "Stopping after the current run", extra={"Signal": signal.Signals(number).name}
            )
            self.stop()

        signal.signal(signal.SIGTERM, handle)
        signal.signal(signal.SIGINT, handle)

    def run(self, max_runs: int | None = None, max_idle_polls: int | None = None) -> WorkerReport:
        """Claim and execute until told to stop, or until a bound is reached.

        ``max_runs`` and ``max_idle_polls`` both default to unbounded, which is
        what a deployment wants. They exist for the two callers that are not a
        deployment: a test, and an operator draining a queue by hand who wants
        the process to exit when there is nothing left rather than sit polling.
        """
        claimed = succeeded = failed = discarded = idle = 0

        while not self._stopping.is_set():
            if max_runs is not None and claimed >= max_runs:
                break

            # Before claiming, not on a timer, and with no backoff: see
            # RunQueue.reclaim_expired for both halves of that.
            self._queue.reclaim_expired()

            work = self._queue.claim()
            if work is None:
                idle += 1
                if max_idle_polls is not None and idle >= max_idle_polls:
                    break
                # `wait` rather than `sleep`, so a SIGTERM during an idle poll
                # returns immediately instead of after the full interval.
                self._stopping.wait(self._config.poll_seconds)
                continue

            claimed += 1
            outcome = self._perform(work)
            if outcome is True:
                succeeded += 1
            elif outcome is False:
                failed += 1
            else:
                discarded += 1

        return WorkerReport(
            claimed=claimed,
            succeeded=succeeded,
            failed=failed,
            discarded=discarded,
            idle_polls=idle,
        )

    def _perform(self, work: ClaimedRun) -> bool | None:
        """One run. ``True`` recorded, ``False`` failed, ``None`` lease lost.

        Three outcomes rather than two because "this worker lost its lease" is
        not a failure of the run - another worker has it - and counting it as
        one would make a rollout look like a sweep full of errors.
        """
        started = time.monotonic()
        try:
            result, data_fingerprint = execute(work, self._cache)
        except Exception as failure:
            # Bare, and deliberately. Everything a strategy can do wrong -
            # a lookback of zero, a division, a symbol the lake does not have -
            # arrives here, and a worker that died on any of them would take
            # the rest of the sweep with it. The run records what happened and
            # the loop continues; `max_attempts` is what stops a run that fails
            # deterministically from being retried forever.
            message = f"{type(failure).__name__}: {failure}"
            logger.exception("Run failed", extra={"RunId": work.id, "Strategy": work.strategy})
            if self._queue.fail(work, message):
                return False
            return None

        recorded = self._queue.succeed(
            work,
            result,
            compute_metrics(result),
            data_fingerprint,
            self._revision,
        )
        if not recorded:
            return None
        logger.info(
            "Run complete",
            extra={
                "RunId": work.id,
                "Strategy": work.strategy,
                "Bars": result.bars,
                "Trades": result.trades,
                "ElapsedMs": int((time.monotonic() - started) * 1000),
            },
        )
        return True
