"""The lookahead gate: a strategy cannot see a bar it should not.

docs/plans/trading.md Phase 4: *"a lookahead test fails the build if a strategy
can see a bar it should not."* Lookahead is the failure mode that produces a
backtest which is both beautiful and worthless, and it is invisible in the
result - an equity curve produced by a strategy that peeked looks exactly like
one produced by a strategy that is right.

So it is asserted three ways, because there are three different doors:

1. **The history a strategy is handed** never contains a bar after ``now``, at
   any lookback depth, on any bar of the run.
2. **The prices it is marked and filled at** are the current bar's, not a
   later one - a portfolio marked to tomorrow's close is lookahead wearing the
   accounting's clothes.
3. **An order cannot fill on the bar that motivated it.** This is the same rule
   the fixture test checks from the price side; here it is checked from the
   ordering side, by a strategy that records what it knew when.

The strategy in (1) is deliberately greedy: it asks for far more history than
exists and keeps everything it is given, so a window that ever ran past the
cursor would show up as a timestamp in the future rather than as a subtly
better return.
"""

from datetime import datetime

import pytest

from aerie_trading.engine.backtest import run_backtest
from aerie_trading.engine.broker import ZERO_COSTS
from aerie_trading.engine.history import BarHistory
from aerie_trading.engine.instruments import equity
from aerie_trading.engine.money import money
from aerie_trading.engine.strategy import Params, StrategyContext
from tests.conftest import flat_bars

#: A strictly rising series, so that "a price from the future" is detectable as
#: a number rather than only as a timestamp: any price above the current bar's
#: could only have come from later.
CLOSES = [10.0, 11.0, 12.0, 13.0, 14.0, 15.0, 16.0, 17.0]


class GreedyStrategy:
    """Asks for everything, every bar, and keeps a record of what it was given."""

    def __init__(self) -> None:
        self.params_value = Params()
        self.observations: list[tuple[datetime, datetime, float]] = []
        self.marks: list[tuple[datetime, float]] = []

    @property
    def params(self) -> Params:
        return self.params_value

    def on_bar(self, ctx: StrategyContext) -> None:
        for symbol in ctx.symbols:
            # A lookback far longer than the run, then the unbounded form.
            # Both are the shapes a real strategy uses, and both slice to the
            # cursor or the guarantee is not a guarantee.
            for window in (ctx.bars(symbol, 1_000), ctx.bars(symbol)):
                for bar in window:
                    self.observations.append((ctx.now, bar.timestamp, bar.close))
            price = ctx.price(symbol)
            assert price is not None
            self.marks.append((ctx.now, float(price)))


def test_no_bar_handed_to_a_strategy_is_later_than_the_bar_it_is_on() -> None:
    strategy = GreedyStrategy()
    history = BarHistory.from_bars(flat_bars("ZVZZT", CLOSES))

    run_backtest(strategy, history, costs=ZERO_COSTS, name="greedy")

    assert strategy.observations, "the strategy was never called"
    for now, seen, close in strategy.observations:
        assert seen <= now, f"on {now.isoformat()} the strategy was handed {seen.isoformat()}"
        # The value check, independent of the timestamp check: the series only
        # rises, so a close above the current bar's is a future price whatever
        # timestamp came attached to it.
        assert close <= CLOSES[history.index_of(now)]


def test_the_price_a_strategy_is_quoted_is_the_current_bar_close() -> None:
    strategy = GreedyStrategy()
    history = BarHistory.from_bars(flat_bars("ZVZZT", CLOSES))

    run_backtest(strategy, history, costs=ZERO_COSTS, name="greedy")

    assert [close for _, close in strategy.marks] == CLOSES


class TimestampingStrategy:
    """Buys once, and records the instant it decided to."""

    def __init__(self) -> None:
        self.decided_at: datetime | None = None

    @property
    def params(self) -> Params:
        return Params()

    def on_bar(self, ctx: StrategyContext) -> None:
        if self.decided_at is None:
            self.decided_at = ctx.now
            ctx.order(equity("ZVZZT"), 1, tag="entry")


def test_an_order_cannot_fill_on_the_bar_that_motivated_it() -> None:
    strategy = TimestampingStrategy()
    history = BarHistory.from_bars(flat_bars("ZVZZT", CLOSES))

    result = run_backtest(strategy, history, costs=ZERO_COSTS, name="timestamps")

    assert strategy.decided_at == history.timeline[0]
    assert len(result.fills) == 1
    fill = result.fills[0]
    assert fill.filled_at == history.timeline[1]
    # And at the *later* price, which is the number a same-bar fill would have
    # got wrong in the strategy's favour on a rising series.
    assert fill.price == money(CLOSES[1])


def test_the_history_itself_refuses_to_look_past_a_cursor() -> None:
    # The guarantee at its source. `BarHistory` is what would have to be wrong
    # for the three tests above to be wrong together, so it is asserted
    # directly as well: no accessor at index i returns anything after
    # timeline[i], including the "most recent known" one.
    history = BarHistory.from_bars(flat_bars("ZVZZT", CLOSES))

    for index, instant in enumerate(history.timeline):
        assert history.window("ZVZZT", index) == tuple(history.window("ZVZZT", index, index + 1))
        assert all(bar.timestamp <= instant for bar in history.window("ZVZZT", index))
        latest = history.latest("ZVZZT", index)
        assert latest is not None
        assert latest.timestamp == instant
        assert set(history.opens_at(index)) == {"ZVZZT"}

    with pytest.raises(ValueError, match="lookback"):
        history.window("ZVZZT", 0, 0)
