"""Parameters chosen on each train window, one account carried through the tests.

docs/plans/trading.md Phase 6: *"Walk-forward: rolling train/test windows,
parameters chosen on each train window, results stitched from the test windows
only. This is the number the leaderboard sorts on."*

**Why this is one unit of work rather than a coordinated batch.** The obvious
shape is to enqueue each train fold as a sweep, wait for it, pick a winner and
enqueue the test run - which needs a coordinator that knows when a fold has
finished, retries the wait, and survives its own restart. All of that
machinery would exist to arrange backtests that are pure functions of a history
already loaded in memory. So a walk-forward is *one* row on the same queue as
every other run: it claims a lease, loads the history once, and does the whole
evaluation in process. It scales the way everything else here does - by there
being more workers - and it fails the way everything else here does, by its
lease expiring and somebody else picking it up.

**The account is carried, not stitched.** Each fold's test picks up with the
cash the previous fold's test ended with, so the curve this produces is one
continuous equity series and ``compute_metrics`` reads it with no special case
at all - no chaining of per-fold returns, no convention about compounding that
somebody has to be told. The equivalent of the plan's "stitched from the test
windows only" falls out of the arithmetic rather than being performed on it.

**What the fold boundary costs, stated because it is not nothing.** A fold ends
with its positions marked to the last close, and the next fold starts flat with
that value as cash. That is a liquidation and a re-entry charged nothing - no
slippage, no commission - four times in a five-fold run. It flatters a strategy
that turns over at the boundary and is invisible to one that does not. It is
left uncharged rather than modelled because the alternative is inventing a
rebalance the strategy did not ask for and pricing it, and because the
cost-sensitivity re-score (``scoring.py``) is the check that catches
cost-optimism generally, at every fill rather than at four of them.

**Selection is deterministic, including its ties.** Two parameter sets with
identical train objectives are ordered by their rendered parameters, so a
walk-forward run repeated on another worker chooses the same winners and
produces the same fingerprint. A tie broken by iteration order would make the
determinism gate fail intermittently and only on grids with symmetry in them,
which is the worst way for it to fail.
"""

from __future__ import annotations

import logging
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal

from aerie_trading.engine.backtest import BacktestResult, EquityPoint, run_backtest
from aerie_trading.engine.broker import Costs, ExpiredOrder, Fill
from aerie_trading.engine.history import BarHistory
from aerie_trading.engine.metrics import compute_metrics
from aerie_trading.engine.money import money, to_cents
from aerie_trading.engine.strategy import StrategySpec
from aerie_trading.honesty.windows import Fold, OutOfSample, folds_over, out_of_sample

__all__ = ["FoldOutcome", "WalkForwardResult", "WalkForwardTooLarge", "walk_forward"]

logger = logging.getLogger(__name__)


class WalkForwardTooLarge(ValueError):
    """Raised before any backtest runs, when the grid times the folds is absurd.

    Carries both numbers because the useful remedy depends on which one is
    wrong: a thousand-point grid over five folds is narrowed at the grid, and a
    twenty-point grid over five hundred folds is narrowed at the folds.
    """

    def __init__(self, evaluations: int, ceiling: int, trials: int, folds: int) -> None:
        super().__init__(
            f"this walk-forward is {trials} parameter set(s) over {folds} fold(s),"
            f" which is {evaluations} backtests, and the ceiling is {ceiling}."
            " Narrow the grid, use fewer folds, or raise"
            " TRADING_HONESTY__MAX_FOLD_EVALUATIONS deliberately."
        )
        self.evaluations = evaluations
        self.ceiling = ceiling


@dataclass(frozen=True, slots=True)
class FoldOutcome:
    """What one fold chose, and what that choice then did out of sample.

    ``train_objective`` is the winner's score on the train window and
    ``test_return`` is what it earned on the test window. The pair is the whole
    diagnosis a person wants from a walk-forward that disappointed: objectives
    that are high on every train window and returns that are noise on every
    test window is overfitting, and it looks exactly like that in this table.
    """

    fold: Fold
    params: Mapping[str, object]
    train_objective: Decimal
    candidates: int
    starting_cash: Decimal
    ending_equity: Decimal

    @property
    def test_return(self) -> Decimal:
        if self.starting_cash == 0:
            return Decimal(0)
        return self.ending_equity / self.starting_cash - 1


@dataclass(frozen=True, slots=True)
class WalkForwardResult:
    """The evaluation, as the queue and the leaderboard need it.

    ``result`` is a real ``BacktestResult`` over the carried account, so
    everything downstream - ``compute_metrics``, the fingerprint, the blotter -
    treats a walk-forward exactly like any other run. That is the point of
    synthesising one rather than inventing a parallel result type: the honesty
    layer must not need its own metrics implementation, because a metric
    defined twice will diverge.
    """

    result: BacktestResult
    outcomes: tuple[FoldOutcome, ...]
    out_of_sample: OutOfSample
    evaluations: int


def walk_forward(
    history: BarHistory,
    strategy: StrategySpec,
    param_sets: Sequence[Mapping[str, object]],
    *,
    window: tuple[datetime, datetime],
    folds: int,
    train_multiple: int = 3,
    objective: str = "sharpe",
    starting_cash: Decimal | float | int | str = 100_000,
    costs: Costs | None = None,
    ceiling: int = 20_000,
    name: str = "",
) -> WalkForwardResult:
    """Walk ``param_sets`` forward over ``history``. Returns one carried account.

    ``history`` holds the bars; ``window`` is the span the folds are cut out
    of, and the two are deliberately separate arguments. The schedule has to be
    a function of the *declared* window rather than of the bars that turned up,
    because the run row records its out-of-sample span at enqueue time and a
    worker that re-derived a slightly different one from the lake would produce
    a result whose stated held-out window is not the one it held out. A lake
    that is short at one end therefore fails the fold it cannot fill, with the
    dates in the error, instead of quietly running a different experiment.

    The folds are cut out of the history in memory (``BarHistory.between``)
    rather than re-read from the lake per fold.
    """
    if not param_sets:
        raise ValueError("a walk-forward needs at least one parameter set to choose between")

    schedule = folds_over(window[0], window[1], folds, train_multiple)
    evaluations = len(param_sets) * len(schedule)
    if evaluations > ceiling:
        raise WalkForwardTooLarge(evaluations, ceiling, len(param_sets), len(schedule))

    cash = to_cents(money(starting_cash))
    carried = cash
    outcomes: list[FoldOutcome] = []
    curve: list[EquityPoint] = []
    fills: list[Fill] = []
    expired: list[ExpiredOrder] = []
    bars = 0

    for fold in schedule:
        chosen, score, candidates = _choose(
            history, strategy, param_sets, fold, objective, cash, costs
        )
        test = run_backtest(
            strategy.build(chosen),
            history.between(fold.test_start, fold.test_end),
            starting_cash=carried,
            costs=costs,
            name=name or strategy.name,
        )
        outcomes.append(
            FoldOutcome(
                fold=fold,
                params=chosen,
                train_objective=score,
                candidates=candidates,
                starting_cash=carried,
                ending_equity=test.final_equity,
            )
        )
        carried = test.final_equity
        curve.extend(test.curve)
        fills.extend(test.fills)
        expired.extend(test.expired)
        bars += test.bars
        logger.debug(
            "Walk-forward fold complete",
            extra={
                "Fold": fold.index,
                "Candidates": candidates,
                "TrainObjective": str(score),
                "Equity": str(carried),
            },
        )

    span = out_of_sample(schedule)
    stitched = BacktestResult(
        strategy=name or strategy.name,
        # Not one parameter set, because a walk-forward does not have one - it
        # has the sequence it chose, and that sequence is what a second run of
        # the same evaluation must reproduce for the fingerprint to mean
        # anything. Rendered as sorted pairs so the value does not depend on
        # dictionary order.
        params={
            f"fold{outcome.fold.index}": tuple(sorted(outcome.params.items()))
            for outcome in outcomes
        },
        symbols=history.symbols,
        interval=history.interval.value,
        started_at=curve[0].timestamp,
        ended_at=curve[-1].timestamp,
        bars=bars,
        starting_cash=cash,
        curve=tuple(curve),
        fills=tuple(fills),
        expired=tuple(expired),
        costs={} if costs is None else costs.describe(),
    )
    return WalkForwardResult(
        result=stitched,
        outcomes=tuple(outcomes),
        out_of_sample=span,
        evaluations=evaluations,
    )


def _choose(
    history: BarHistory,
    strategy: StrategySpec,
    param_sets: Sequence[Mapping[str, object]],
    fold: Fold,
    objective: str,
    starting_cash: Decimal,
    costs: Costs | None,
) -> tuple[Mapping[str, object], Decimal, int]:
    """The best parameter set on ``fold``'s train window, by ``objective``.

    A candidate that raises, or that produces no value for the objective, is
    skipped rather than scored as zero. Both are ordinary: a 200-bar average
    over a train window shorter than 200 bars has nothing to say, and a run
    whose equity never moved has no Sharpe. Scoring them as zero would let a
    strategy that did nothing win a fold in which everything else lost money,
    which is a real way for a walk-forward to look better than the thing it is
    measuring.
    """
    train = history.between(fold.train_start, fold.train_end)
    best: tuple[Decimal, str, Mapping[str, object]] | None = None
    scored = 0

    for params in param_sets:
        try:
            result = run_backtest(
                strategy.build(params), train, starting_cash=starting_cash, costs=costs
            )
        except Exception:
            logger.debug(
                "Walk-forward candidate raised on its train window",
                extra={"Fold": fold.index, "Strategy": strategy.name},
                exc_info=True,
            )
            continue
        score = compute_metrics(result).get(objective)
        if score is None:
            continue
        scored += 1
        # The rendered parameters are the tie-break. See the module docstring:
        # a tie broken by iteration order fails the determinism gate only on
        # grids with symmetry in them.
        key = repr(sorted(params.items()))
        if best is None or (score, key) > (best[0], best[1]):
            best = (score, key, params)

    if best is None:
        raise LookupError(
            f"no parameter set produced a {objective!r} on {fold.describe()};"
            " the train window is probably shorter than the strategy's warm-up"
        )
    return best[2], best[0], scored
