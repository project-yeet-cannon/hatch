"""Buy the universe on the first bar; hold it to the last.

**The file to read before writing a strategy.** docs/plans/trading.md Phase 4
asks for the smallest honest example of the ``Strategy`` protocol, and the
whole rule below is four lines in ``on_bar``. Everything longer than that is
this docstring explaining decisions a reader would otherwise have to guess at.

**No parameters at all**, which is the point of it existing beside
``ma_crossover``: one strategy in the registry has an empty parameter space and
the other has a grid, so every consumer - the sweep launcher, the leaderboard,
the UI's parameter panel - is exercised against both the degenerate case and
the real one from the day it is written.

**It buys everything in the run's universe, equally weighted**, rather than
taking a symbol as a parameter. That makes it parameter-free in fact and not
just in claim, and it makes it the correct baseline for Phase 6: the baseline
beside a result has to cover the *same* window and the *same* universe as the
result, and a baseline that had to be told which symbol to hold would be one
more thing that can be configured inconsistently.

**It is not a candidate for making money**, which its ``description`` says so
that the UI says it too. Against the synthetic source it is a zero-drift random
walk held for the whole window, and its expected return before costs is zero by
construction.

Two mechanics worth stating, because both are the engine's rules showing
through rather than choices made here:

- The order is submitted on the first bar and **fills at the second bar's
  open**. There is no way to fill on the bar you signalled from, so "buy at the
  first bar" means "decide on the first bar" - which is what a live run would
  also mean.
- Size is computed from the first bar's close because that is the last price
  the strategy can see, and the fill happens at a price it cannot. A gap up
  overnight leaves the account slightly under-invested and a gap down slightly
  over; both are what would have happened.
"""

from __future__ import annotations

from decimal import Decimal
from typing import Final

from aerie_trading.engine.instruments import equity
from aerie_trading.engine.money import money
from aerie_trading.engine.strategy import Params, StrategyContext, StrategySpec

__all__ = ["SPEC", "BuyAndHold", "BuyAndHoldParams"]

NAME: Final = "buy_and_hold"

DESCRIPTION: Final = (
    "Buys every symbol in the run's universe on the first bar, equally weighted,"
    " and holds to the last. Not a candidate for making money: it is the baseline"
    " every other result is measured against, and the smallest complete example of"
    " the Strategy protocol."
)


class BuyAndHoldParams(Params):
    """No tunables. Deliberately - see the module docstring."""


class BuyAndHold:
    """Equal-weight the universe once, then do nothing."""

    def __init__(self, params: BuyAndHoldParams | None = None) -> None:
        self._params = params if params is not None else BuyAndHoldParams()

    @property
    def params(self) -> Params:
        return self._params

    def on_bar(self, ctx: StrategyContext) -> None:
        if not ctx.is_first_bar:
            return
        budget = ctx.equity / money(len(ctx.symbols))
        for symbol in ctx.symbols:
            price = ctx.price(symbol)
            if price is not None and price > 0:
                ctx.order(equity(symbol), _shares(budget, price), tag="entry")


def _shares(budget: Decimal, price: Decimal) -> int:
    """Whole shares affordable at ``price``. Fractional shares are not a market feature."""
    return int(budget // price)


SPEC: Final = StrategySpec(
    name=NAME,
    description=DESCRIPTION,
    params_model=BuyAndHoldParams,
    build=lambda values: BuyAndHold(BuyAndHoldParams.model_validate(values)),
)
