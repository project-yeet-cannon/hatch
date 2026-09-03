"""Long while the fast average is above the slow one; flat otherwise.

The second of the two shipped reference strategies. docs/plans/trading.md Phase
4 states its job precisely: *"it exists because it is the only one of the two
that can exercise a parameter schema, a sweep launcher and a multi-run
leaderboard. A UI with one unparameterized strategy in it does not demonstrate
the product."*

**It is not a candidate for making money, and this is the strategy the plan
predicts will lose.** Against a zero-drift random walk it should finish *behind*
``buy_and_hold`` after costs, because it trades, trading costs money, and there
is no signal in the data to pay for it. That prediction is asserted in
``tests/test_reference_strategies.py`` and is visible in the seeded leaderboard
from Phase 7 onward: if the crossover is ever shown beating buy-and-hold on
synthetic data, the cost model, the fill model or the engine is wrong, and it
is wrong on the app's own front page.

**Long-only, and flat rather than short on the down-cross.** Two reasons, and
the first is the honest one: a short position's mark-to-market and margin are a
different accounting problem, and this file's job is to be the parameterized
example rather than to be complete. The second is that it halves the number of
round trips, which makes the cost comparison against the baseline a comparison
of one strategy's costs rather than of two.

**Orders are placed on the crossing, not on the state.** A rule written as "if
fast > slow, be long" re-evaluates its target size on every bar and therefore
rebalances every bar as equity drifts - hundreds of tiny trades that are
entirely an artefact of how the rule was written, and each one paying costs.
Tracking the previous side and acting only when it changes is what a person
means by a crossover, and it is the difference between a strategy that trades
about twenty times a year and one that trades every day.

**Warm-up is not padded.** Until a symbol has ``slow`` bars, it has no signal
and no position. Padding the window with whatever history existed - or with the
first price repeated - would let the strategy trade a signal computed over
invented data, which is the shape of a backtest that opens brilliantly and can
never be reproduced.
"""

from __future__ import annotations

from decimal import Decimal
from statistics import fmean
from typing import Final

from pydantic import model_validator

from aerie_trading.engine.instruments import equity
from aerie_trading.engine.money import money
from aerie_trading.engine.strategy import Params, StrategyContext, StrategySpec, swept

__all__ = ["SPEC", "MaCrossover", "MaCrossoverParams"]

NAME: Final = "ma_crossover"

DESCRIPTION: Final = (
    "Goes long a symbol when its fast moving average crosses above its slow one and"
    " flat when it crosses back, equally weighted across the run's universe. Not a"
    " candidate for making money: against data with no drift it is expected to finish"
    " behind buy-and-hold by roughly what it pays in costs, and it ships as the"
    " strategy that exercises parameter sweeps and the leaderboard."
)


class MaCrossoverParams(Params):
    """Two windows, both swept.

    The ranges are what Phase 5's sweep walks, and they are chosen to be a
    parameter space rather than a token one: 49 fast values and 40 slow values
    is 1,960 combinations before the ``fast < slow`` rule prunes them, which is
    the order of magnitude the ask means by "lots of parameters" and enough for
    Phase 6's overfitting gate to have something to measure against.

    The step on ``slow`` is 5 rather than 1 deliberately. A 50-day and a 51-day
    average are the same idea, and a grid that walks both spends most of a
    sweep re-measuring the same hypothesis - which is precisely how a sweep
    manufactures a winner out of nothing.
    """

    fast: int = swept(
        10,
        low=2,
        high=50,
        step=1,
        description="Bars in the fast moving average.",
    )
    slow: int = swept(
        50,
        low=5,
        high=200,
        step=5,
        description="Bars in the slow moving average. Must exceed the fast window.",
    )

    @model_validator(mode="after")
    def _fast_below_slow(self) -> MaCrossoverParams:
        # In the model rather than in `on_bar`, so a sweep's invalid corner is
        # rejected when the param_set is built rather than after a worker has
        # spent a minute producing a run whose two averages are the same
        # series in a different order.
        if self.fast >= self.slow:
            raise ValueError(f"fast ({self.fast}) must be below slow ({self.slow})")
        return self


class MaCrossover:
    """A moving-average crossover, applied independently to each symbol."""

    def __init__(self, params: MaCrossoverParams | None = None) -> None:
        self._params = params if params is not None else MaCrossoverParams()
        # Per symbol, whether the fast average was above the slow one on the
        # previous bar. Absent until the symbol has enough history to have an
        # opinion, which is what stops the first computable bar being read as
        # a crossing from a state that never existed.
        self._above: dict[str, bool] = {}

    @property
    def params(self) -> Params:
        return self._params

    def on_bar(self, ctx: StrategyContext) -> None:
        settings = self._params
        budget = ctx.equity / money(len(ctx.symbols))
        for symbol in ctx.symbols:
            window = ctx.bars(symbol, settings.slow)
            if len(window) < settings.slow:
                continue
            closes = [bar.close for bar in window]
            above = fmean(closes[-settings.fast :]) > fmean(closes)

            previous = self._above.get(symbol)
            self._above[symbol] = above
            if previous is None or previous == above:
                continue

            price = ctx.price(symbol)
            if price is None or price <= 0:
                continue
            target = _shares(budget, price) if above else 0
            ctx.order_to_target(equity(symbol), target, tag="up" if above else "down")


def _shares(budget: Decimal, price: Decimal) -> int:
    return int(budget // price)


SPEC: Final = StrategySpec(
    name=NAME,
    description=DESCRIPTION,
    params_model=MaCrossoverParams,
    build=lambda values: MaCrossover(MaCrossoverParams.model_validate(values)),
)
