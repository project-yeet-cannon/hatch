"""The numbers that go beside a result so it cannot be read on its own.

docs/plans/trading.md Phase 6, three bullets in one module because they are
three answers to one question - *what would this result have to beat to mean
anything*:

**Baselines.** *"Computed over the identical window and shown next to every
result: buy-and-hold on the underlying, and buy-and-hold on a broad index. A
strategy that loses to buying the index has told you something, and it should
not take arithmetic to notice."* Computed here, per run, rather than joined to
a sibling baseline run at read time. The join would be cheaper and is the wrong
trade: a baseline that lives on another row is one a failed sibling can remove
from a leaderboard, and *"it should not take arithmetic to notice"* is a claim
about the row, not about the query. The extra cost is one buy-and-hold backtest
over a history the worker has already loaded, memoized per history.

**Transaction-cost sensitivity.** *"Every result re-scored at a higher cost
assumption. A strategy that only works at zero slippage is identified as such
automatically."* The re-score is the same strategy over the same bars with the
cost model's rates multiplied, which is the one variable a cost model has that
is worth varying; ``runs/costs.py`` says the same thing from the other end, and
is why the model is a value on the run rather than a global.

**Selection accounting.** *"A run knows how many siblings its sweep produced."*
The count arrives on the claim (``sweep.trials``) rather than being queried,
because a worker that had to count its siblings would be reading a table its
peers are still writing to. ``selection.py`` holds the arithmetic and the
argument for which correction this is.

**Everything here is optional and absent when it has no answer**, matching
``engine/metrics.py``: a run with no Sharpe gets no deflated Sharpe, and a run
whose baseline could not be computed reports no excess return rather than an
excess over zero. Absence sorts last; a fabricated zero sorts in the middle of
a leaderboard.
"""

from __future__ import annotations

import logging
from collections.abc import Mapping
from dataclasses import dataclass
from decimal import Decimal

from aerie_trading.engine.backtest import BacktestResult
from aerie_trading.engine.metrics import (
    BASELINE_RETURN,
    COST_SENSITIVITY,
    DEFLATED_SHARPE,
    EXCESS_RETURN,
    EXCESS_RETURN_INDEX,
    EXPECTED_MAX_SHARPE,
    INDEX_RETURN,
    SELECTION_TRIALS,
    SHARPE,
    STRESSED_SHARPE,
    STRESSED_TOTAL_RETURN,
    TOTAL_RETURN,
    WALK_FORWARD_FOLDS,
    compute_metrics,
    periods_per_year,
)
from aerie_trading.honesty.selection import expected_max_sharpe
from aerie_trading.providers.base import Interval
from aerie_trading.runs.costs import CostSpec

__all__ = ["HonestyInputs", "score", "stress"]

logger = logging.getLogger(__name__)


def stress(costs: CostSpec, multiple: Decimal) -> CostSpec:
    """``costs`` with every rate multiplied by ``multiple``.

    All four fields, not just slippage. A commission schedule is as much of a
    cost assumption as a spread is, and a stress test that moved only the one
    an operator was already thinking about would confirm what they expected
    rather than test it. ``commission_maximum`` scales too, because a cap that
    stayed put while the per-unit rate rose would quietly turn into the
    dominant term and understate the stress.
    """
    return costs.model_copy(
        update={
            "slippage_bps": costs.slippage_bps * multiple,
            "commission_per_unit": costs.commission_per_unit * multiple,
            "commission_minimum": costs.commission_minimum * multiple,
            "commission_maximum": (
                None if costs.commission_maximum is None else costs.commission_maximum * multiple
            ),
        }
    )


@dataclass(frozen=True, slots=True)
class HonestyInputs:
    """Everything the honesty metrics need that is not the result itself.

    A dataclass rather than eight parameters because the worker assembles it
    from four different places - the claim, the cache, the config and a second
    backtest - and a positional call with three optional ``BacktestResult``
    arguments in a row is one where two of them get swapped exactly once.
    """

    #: Buy-and-hold over the run's own universe, same window, same cash, same
    #: costs. ``None`` when it could not be computed, which is not an error:
    #: the run itself is still a result.
    baseline: BacktestResult | None = None
    #: Buy-and-hold over the broad universe. See ``HonestyConfig.index_symbols``
    #: for what "broad" resolves to when nothing names an index.
    index: BacktestResult | None = None
    #: The same run at the stressed cost model.
    stressed: BacktestResult | None = None
    #: How many parameter sets the run's sweep expanded to. 1 for a run that
    #: was not selected out of anything, which earns no haircut at all.
    trials: int = 1
    #: How many walk-forward folds produced this result, if it is one.
    folds: int | None = None


def score(result: BacktestResult, inputs: HonestyInputs) -> Mapping[str, Decimal]:
    """The Phase 6 metrics for one run. Keys absent where there is no answer.

    Returned separately from ``compute_metrics`` rather than merged into it, so
    that the one function that may be called with nothing but a result stays
    callable that way - which is what lets the engine's own tests measure a
    backtest with no honesty layer anywhere near them.
    """
    metrics: dict[str, Decimal] = {SELECTION_TRIALS: Decimal(max(inputs.trials, 1))}
    if inputs.folds is not None:
        metrics[WALK_FORWARD_FOLDS] = Decimal(inputs.folds)

    own = compute_metrics(result)
    total = own.get(TOTAL_RETURN)

    baseline = _total_return(inputs.baseline)
    if baseline is not None:
        metrics[BASELINE_RETURN] = baseline
        if total is not None:
            metrics[EXCESS_RETURN] = total - baseline

    index = _total_return(inputs.index)
    if index is not None:
        metrics[INDEX_RETURN] = index
        if total is not None:
            metrics[EXCESS_RETURN_INDEX] = total - index

    if inputs.stressed is not None:
        stressed = compute_metrics(inputs.stressed)
        stressed_total = stressed.get(TOTAL_RETURN)
        if stressed_total is not None:
            metrics[STRESSED_TOTAL_RETURN] = stressed_total
            if total is not None:
                # Positive means costs hurt, which is the direction every
                # sane result points. A negative one is a strategy that made
                # money *because* it was charged more, which happens - a cost
                # model can suppress a losing trade - and is worth seeing
                # rather than clamping.
                metrics[COST_SENSITIVITY] = total - stressed_total
        stressed_sharpe = stressed.get(SHARPE)
        if stressed_sharpe is not None:
            metrics[STRESSED_SHARPE] = stressed_sharpe

    sharpe = own.get(SHARPE)
    observations = len(result.curve) - 1
    if sharpe is not None and observations >= 2:
        scale = periods_per_year(Interval(result.interval))
        expected = expected_max_sharpe(max(inputs.trials, 1), observations, scale)
        metrics[EXPECTED_MAX_SHARPE] = expected
        metrics[DEFLATED_SHARPE] = sharpe - expected

    return metrics


def _total_return(result: BacktestResult | None) -> Decimal | None:
    """A comparison run's total return, or ``None`` if it has none.

    Reached through ``compute_metrics`` rather than through the property,
    because the property raises on a run that started with no cash and this is
    a *comparison* - a baseline that cannot be computed must not take the run
    it was going to sit beside down with it.
    """
    if result is None:
        return None
    return compute_metrics(result).get(TOTAL_RETURN)
