"""The hand-checked fixture: four bars, two trades, P&L to the cent.

docs/plans/trading.md Phase 4 asks for *"a tiny synthetic price series with
known correct P&L, asserted to the cent. Every later engine change is measured
against it."* The arithmetic is written out below in full, in the comments, so
that the expected numbers can be checked by a person with a calculator and
without reading a line of the engine. That is the property that makes this file
worth more than the sum of its assertions: an engine change that breaks it and
a fixture that was rewritten to match the change would be indistinguishable if
the expected values were computed rather than written down.

The series is deliberately boring - round numbers, one symbol, no gaps - and
the two cost models are exercised separately so that a failure says which of
the three (accounting, slippage, commission) moved.
"""

from decimal import Decimal

from aerie_trading.engine.backtest import run_backtest
from aerie_trading.engine.broker import (
    ZERO_COSTS,
    BpsSlippage,
    Costs,
    NoSlippage,
    PerTradeCommission,
)
from aerie_trading.engine.history import BarHistory
from tests.conftest import ScriptedStrategy, daily_bars

#: open, high, low, close - four sessions of ZVZZT.
FIXTURE = [
    (100.00, 101.00, 99.00, 100.00),
    (102.00, 103.00, 101.00, 102.00),
    (110.00, 111.00, 109.00, 110.00),
    (108.00, 109.00, 107.00, 108.00),
]

STARTING_CASH = Decimal("10000.00")

#: Buy ten shares having seen bar 1; sell them having seen bar 3. Both fill on
#: the *following* bar's open, which is the whole no-same-bar-fill rule and is
#: what makes the fill prices 102.00 and 108.00 rather than 100.00 and 110.00.
SCRIPT = {1: 10, 3: -10}


def history() -> BarHistory:
    return BarHistory.from_bars(daily_bars("ZVZZT", FIXTURE))


def test_the_fixture_pays_out_to_the_cent_with_a_flat_commission() -> None:
    # Costs: no slippage, one dollar a trade. Two trades, so two dollars.
    #
    #   bar 1  no fill. cash 10,000.00, no position.        equity 10,000.00
    #   bar 2  buy 10 @ 102.00 = 1,020.00, plus 1.00 fee
    #          cash 10,000.00 - 1,020.00 - 1.00 = 8,979.00
    #          marked at close 102.00 -> 10 x 102.00 = 1,020.00
    #                                                        equity  9,999.00
    #   bar 3  no fill. marked at 110.00 -> 1,100.00
    #          cash 8,979.00                                 equity 10,079.00
    #   bar 4  sell 10 @ 108.00 = 1,080.00, less 1.00 fee
    #          cash 8,979.00 + 1,080.00 - 1.00 = 10,058.00
    #          flat, so nothing to mark                      equity 10,058.00
    #
    # Realized: (108.00 - 102.00) x 10 = 60.00 gross, less 2.00 of fees = 58.00.
    # Return: 10,058.00 / 10,000.00 - 1 = 0.58%.
    result = run_backtest(
        ScriptedStrategy(SCRIPT),
        history(),
        starting_cash=STARTING_CASH,
        costs=Costs(slippage=NoSlippage(), commission=PerTradeCommission(Decimal("1.00"))),
        name="fixture",
    )

    equity_by_bar = [point.equity for point in result.curve]
    assert equity_by_bar == [
        Decimal("10000.00"),
        Decimal("9999.00"),
        Decimal("10079.00"),
        Decimal("10058.00"),
    ]
    assert result.curve[1].cash == Decimal("8979.00")
    assert result.curve[2].unrealized_pnl == Decimal("80.00")
    assert result.curve[3].realized_pnl == Decimal("58.00")
    assert result.final_equity == Decimal("10058.00")
    assert result.commissions == Decimal("2.00")
    assert result.slippage_cost == Decimal("0")
    assert result.total_return == Decimal("0.0058")
    assert result.trades == 2


def test_the_fixture_pays_out_to_the_cent_with_slippage() -> None:
    # Ten basis points, adverse in both directions, on top of the same dollar.
    #
    #   buy   102.00 x (1 + 0.0010) = 102.102 -> 1,021.02 for ten shares
    #   sell  108.00 x (1 - 0.0010) = 107.892 -> 1,078.92 for ten shares
    #
    #   cash  10,000.00 - 1,021.02 - 1.00 + 1,078.92 - 1.00 = 10,055.90
    #
    # Which decomposes exactly: 60.00 gross at the unslipped prices, less 2.10
    # of slippage (1.02 buying, 1.08 selling) and 2.00 of commission = 55.90.
    result = run_backtest(
        ScriptedStrategy(SCRIPT),
        history(),
        starting_cash=STARTING_CASH,
        costs=Costs(
            slippage=BpsSlippage(Decimal("10")),
            commission=PerTradeCommission(Decimal("1.00")),
        ),
        name="fixture",
    )

    assert [fill.price for fill in result.fills] == [Decimal("102.102"), Decimal("107.892")]
    assert result.curve[1].cash == Decimal("8977.98")
    assert result.final_equity == Decimal("10055.90")
    assert result.slippage_cost == Decimal("2.10")
    assert result.commissions == Decimal("2.00")
    assert result.curve[3].realized_pnl == Decimal("55.90")


def test_costs_are_the_only_difference_between_the_two_runs() -> None:
    # The decomposition asserted as a relationship rather than as two numbers:
    # a free run makes exactly the gross 60.00, and every cent of the gap to
    # the priced runs above is accounted for by the models. If a future change
    # to the engine loses a cent somewhere, this is the assertion that says the
    # cent went missing rather than that a strategy got worse.
    # ZERO_COSTS explicitly, because the engine's default is *not* free - see
    # engine/broker.py. A test that wanted a free run and forgot to say so
    # would silently be measuring the default cost model instead.
    free = run_backtest(
        ScriptedStrategy(SCRIPT),
        history(),
        starting_cash=STARTING_CASH,
        costs=ZERO_COSTS,
        name="fixture",
    )
    priced = run_backtest(
        ScriptedStrategy(SCRIPT),
        history(),
        starting_cash=STARTING_CASH,
        costs=Costs(
            slippage=BpsSlippage(Decimal("10")),
            commission=PerTradeCommission(Decimal("1.00")),
        ),
        name="fixture",
    )

    assert free.final_equity == Decimal("10060.00")
    assert (
        free.final_equity - priced.final_equity
        == priced.slippage_cost + priced.commissions
        == Decimal("4.10")
    )


def test_an_order_on_the_last_bar_expires_rather_than_filling() -> None:
    # The other half of no-same-bar-fills, and the reason `expired` is recorded
    # rather than dropped: a strategy that signals on its final observation has
    # not traded, and a backtest that filled it would be filling at a price
    # that is outside the window it claims to cover.
    result = run_backtest(
        ScriptedStrategy({4: 5}),
        history(),
        starting_cash=STARTING_CASH,
        costs=ZERO_COSTS,
        name="fixture",
    )

    assert result.trades == 0
    assert len(result.expired) == 1
    assert result.expired[0].order.quantity == 5
    assert result.final_equity == STARTING_CASH
