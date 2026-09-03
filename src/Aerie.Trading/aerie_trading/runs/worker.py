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
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal
from types import FrameType

from aerie_trading.db.models import RunKind
from aerie_trading.engine.backtest import BacktestResult, run_backtest
from aerie_trading.engine.history import BarHistory, load_history
from aerie_trading.engine.metrics import compute_metrics
from aerie_trading.honesty.config import HonestyConfig, WalkForwardSpec
from aerie_trading.honesty.scoring import HonestyInputs, score, stress
from aerie_trading.honesty.walkforward import FoldOutcome, walk_forward
from aerie_trading.lake.reader import LakeReader
from aerie_trading.providers.base import Interval
from aerie_trading.runs.config import RunnerConfig
from aerie_trading.runs.costs import CostSpec
from aerie_trading.runs.queue import ClaimedRun, RunQueue
from aerie_trading.runs.sweep import SweepSpec, plan_sweep
from aerie_trading.strategies import spec_for

__all__ = [
    "Assessor",
    "Execution",
    "HistoryCache",
    "Worker",
    "WorkerReport",
    "default_worker_name",
    "execute",
]

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


@dataclass(frozen=True, slots=True)
class Execution:
    """Everything one claimed run produced, before any of it is written down.

    A value rather than four return values, because Phase 6 added two of them
    and a five-tuple is where the third and fourth get swapped. ``metrics``
    already holds the honesty layer's names folded in beside the engine's, so
    the queue writes one mapping and does not have to know which came from
    where.
    """

    result: BacktestResult
    data_fingerprint: str
    metrics: Mapping[str, Decimal]
    folds: tuple[FoldOutcome, ...] = ()


class Assessor:
    """The honesty layer, as one object a worker holds for its whole life.

    docs/plans/trading.md Phase 6 wants a baseline, a broad-index baseline and
    a stressed re-score beside *every* result. Computed naively that is three
    extra backtests per run, and two of the three are the same answer for every
    run in a sweep - the baseline over a window does not depend on which
    parameters are being compared against it. So they are memoized on the
    history they were computed over, which turns "three extra backtests per
    run" into "three extra backtests per sweep, plus one stressed re-score per
    run" - and the re-score is the one that genuinely cannot be shared, since
    it is the run itself at different prices.

    Bounded by the same argument as ``HistoryCache``: keyed on the history and
    the money, and small, because a worker that drifts across sweeps must not
    accumulate one entry per window it has ever seen.
    """

    #: The strategy every baseline is. Named rather than passed in: the plan's
    #: baseline is buy-and-hold, and an installation that could configure it to
    #: something else would be one where two leaderboards are not comparable.
    BASELINE_STRATEGY = "buy_and_hold"

    def __init__(
        self,
        cache: HistoryCache,
        config: HonestyConfig | None = None,
        index_symbols: Sequence[str] = (),
    ) -> None:
        self._cache = cache
        self._config = config if config is not None else HonestyConfig()
        self._index_symbols = tuple(dict.fromkeys(symbol.upper() for symbol in index_symbols))
        self._baselines: OrderedDict[tuple[str, ...], BacktestResult] = OrderedDict()

    @property
    def index_symbols(self) -> tuple[str, ...]:
        return self._index_symbols

    @property
    def config(self) -> HonestyConfig:
        return self._config

    def inputs(
        self, claimed: ClaimedRun, result: BacktestResult, history: BarHistory
    ) -> HonestyInputs:
        """The three comparisons and the two counts, for one finished run.

        Every one of them is optional and every failure is swallowed into a
        ``None``. That is deliberate and is the difference between a honesty
        layer and a liability: a lake that has no bars for the broad universe,
        or a baseline that raised, must not turn a run that worked into a run
        that failed. The metric goes absent, which reads on a leaderboard as
        "this run has no such comparison" rather than as a zero.
        """
        return HonestyInputs(
            baseline=self._baseline(claimed, history),
            index=self._index(claimed),
            stressed=self._stressed(claimed, history),
            trials=claimed.trials,
        )

    # -- the comparisons -----------------------------------------------------

    def _baseline(self, claimed: ClaimedRun, history: BarHistory) -> BacktestResult | None:
        """Buy-and-hold over the run's own universe, window, cash and costs."""
        key = (*claimed.history_key, "own", str(claimed.starting_cash))
        return self._memoized(key, history, claimed.starting_cash, claimed.costs)

    def _index(self, claimed: ClaimedRun) -> BacktestResult | None:
        """Buy-and-hold over the broad universe. ``None`` when none is configured."""
        if not self._index_symbols:
            return None
        try:
            history = self._cache.load(
                self._index_symbols, claimed.interval, claimed.window_start, claimed.window_end
            )
        except Exception:
            logger.warning(
                "No broad-index baseline: the lake has no bars for it over this window",
                extra={"RunId": claimed.id, "Symbols": list(self._index_symbols)},
                exc_info=True,
            )
            return None
        key = (
            claimed.interval.value,
            claimed.window_start.isoformat(),
            claimed.window_end.isoformat(),
            "index",
            str(claimed.starting_cash),
            *self._index_symbols,
        )
        return self._memoized(key, history, claimed.starting_cash, claimed.costs)

    def _stressed(self, claimed: ClaimedRun, history: BarHistory) -> BacktestResult | None:
        """The same run at the stressed cost model. Not memoized - it is per run."""
        multiple = self._config.cost_stress_multiple
        if multiple <= 1:
            return None
        try:
            return run_backtest(
                spec_for(claimed.strategy).build(claimed.params),
                history,
                starting_cash=claimed.starting_cash,
                costs=stress(claimed.costs, multiple).build(),
                name=claimed.strategy,
            )
        except Exception:
            logger.warning(
                "No cost-sensitivity figure: the stressed re-score raised",
                extra={"RunId": claimed.id},
                exc_info=True,
            )
            return None

    def _memoized(
        self,
        key: tuple[str, ...],
        history: BarHistory,
        starting_cash: Decimal,
        costs: CostSpec,
    ) -> BacktestResult | None:
        cached = self._baselines.get(key)
        if cached is not None:
            self._baselines.move_to_end(key)
            return cached
        try:
            baseline = run_backtest(
                spec_for(self.BASELINE_STRATEGY).default(),
                history,
                starting_cash=starting_cash,
                costs=costs.build(),
                name=self.BASELINE_STRATEGY,
            )
        except Exception:
            logger.warning("A baseline could not be computed", exc_info=True)
            return None
        self._baselines[key] = baseline
        while len(self._baselines) > 4:
            self._baselines.popitem(last=False)
        return baseline


def execute(
    claimed: ClaimedRun, cache: HistoryCache, assessor: Assessor | None = None
) -> Execution:
    """Run one claimed item of work, whichever kind it is.

    Free of the queue and of the database on purpose: everything it needs was
    copied onto ``ClaimedRun`` under the claim's transaction, and everything it
    produces is a value. That is what lets the loop below hold no session while
    a backtest runs, and it is what lets this be tested against a lake with no
    Postgres anywhere.

    The strategy is resolved from the registry by name rather than carried on
    the row, so a run enqueued by a build that shipped a strategy this one does
    not is a ``LookupError`` naming both - which the caller records as a failed
    run rather than a crashed worker.

    ``assessor`` of ``None`` means "record what the engine can say about this
    run and nothing else". Not a production configuration - every entry point
    passes one - but the honesty layer is three extra backtests, and a test
    measuring the engine's own accounting should not have to pay for them or
    reason about them.
    """
    history = cache.load(
        claimed.symbols, claimed.interval, claimed.window_start, claimed.window_end
    )
    spec = spec_for(claimed.strategy)

    if claimed.kind is RunKind.WALK_FORWARD:
        return _walk_forward(claimed, history, assessor)

    result = run_backtest(
        spec.build(claimed.params),
        history,
        starting_cash=claimed.starting_cash,
        costs=claimed.costs.build(),
        name=claimed.strategy,
    )
    metrics = dict(compute_metrics(result))
    if assessor is not None:
        metrics.update(score(result, assessor.inputs(claimed, result, history)))
    return Execution(result=result, data_fingerprint=history.fingerprint(), metrics=metrics)


def _walk_forward(claimed: ClaimedRun, history: BarHistory, assessor: Assessor | None) -> Execution:
    """Phase 6's evaluation of a whole sweep, as one item of work.

    The grid comes off ``sweep.spec``, which the claim carried, and is expanded
    through the same ``plan_sweep`` the launcher used - so the parameter sets
    walked here are exactly the ones enqueued beside this row, pruned corners
    and all. Re-expanding rather than reading the sibling ``param_set`` rows is
    what keeps this a pure function of the spec: the siblings may still be
    queued, may have been cancelled, and are in any case a different question
    from "what was this sweep asked to search".
    """
    if claimed.sweep_spec is None:  # pragma: no cover - the CHECK forbids it
        raise LookupError("a walk-forward run has no sweep to read its grid from")
    spec = SweepSpec.model_validate(dict(claimed.sweep_spec))
    schedule = spec.walk_forward if spec.walk_forward is not None else WalkForwardSpec()
    config = assessor.config if assessor is not None else HonestyConfig()

    evaluation = walk_forward(
        history,
        spec_for(claimed.strategy),
        plan_sweep(spec).param_sets,
        window=(claimed.window_start, claimed.window_end),
        folds=schedule.folds,
        train_multiple=schedule.train_multiple,
        objective=schedule.objective,
        starting_cash=claimed.starting_cash,
        costs=claimed.costs.build(),
        ceiling=config.max_fold_evaluations,
        name=claimed.strategy,
    )

    metrics = dict(compute_metrics(evaluation.result))
    if assessor is not None:
        inputs = assessor.inputs(claimed, evaluation.result, history)
        # The trials count is the grid, and the fold count is what makes this
        # row a walk-forward rather than a backtest that happens to be out of
        # sample. Both are on the row a leaderboard reads.
        metrics.update(
            score(
                evaluation.result,
                HonestyInputs(
                    baseline=inputs.baseline,
                    index=inputs.index,
                    stressed=None,
                    trials=claimed.trials,
                    folds=len(evaluation.outcomes),
                ),
            )
        )
    return Execution(
        result=evaluation.result,
        data_fingerprint=history.fingerprint(),
        metrics=metrics,
        folds=evaluation.outcomes,
    )


class Worker:
    """The loop. Constructed with everything it needs and told to ``run``."""

    def __init__(
        self,
        queue: RunQueue,
        cache: HistoryCache,
        revision: str,
        config: RunnerConfig | None = None,
        assessor: Assessor | None = None,
    ) -> None:
        self._queue = queue
        self._cache = cache
        self._revision = revision
        self._config = config if config is not None else RunnerConfig()
        # Constructed here when the caller did not, rather than left ``None``.
        # The honesty layer is not optional in a deployment - a leaderboard
        # whose baseline column is empty because a composition root forgot an
        # argument is precisely the failure Phase 6 exists to prevent - so the
        # default is "on, with the shipped defaults" and switching it off is a
        # configuration value (``cost_stress_multiple``, ``index_symbols``)
        # rather than an omission.
        self._assessor = assessor if assessor is not None else Assessor(cache)
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
            execution = execute(work, self._cache, self._assessor)
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
            execution.result,
            execution.metrics,
            execution.data_fingerprint,
            self._revision,
            execution.folds,
        )
        if not recorded:
            return None
        logger.info(
            "Run complete",
            extra={
                "RunId": work.id,
                "Kind": work.kind.value,
                "Strategy": work.strategy,
                "Bars": execution.result.bars,
                "Trades": execution.result.trades,
                "Folds": len(execution.folds),
                "ElapsedMs": int((time.monotonic() - started) * 1000),
            },
        )
        return True
