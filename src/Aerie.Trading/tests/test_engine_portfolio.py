"""The accounting: legs, weighted-average cost, and the sign that covers shorts.

The fixture test (``tests/test_engine_fixture.py``) checks the numbers a
strategy produces end to end. This file checks the cases that fixture cannot
reach without becoming unreadable: shorts, a leg crossing through zero, two
legs in one position, and the marks that value them.

Every number here is small and round for the same reason it is in the fixture -
an expected value that has to be computed to be checked is an expected value
that will be recomputed with the bug in it.
"""

from datetime import date
from decimal import Decimal

import pytest

from aerie_trading.engine.instruments import equity, option
from aerie_trading.engine.money import money
from aerie_trading.engine.portfolio import Portfolio
from aerie_trading.providers.base import OptionRight

ZVZZT = equity("ZVZZT")
ZWZZT = equity("ZWZZT")


def portfolio(cash: str = "10000.00") -> Portfolio:
    return Portfolio(cash)


def test_a_buy_moves_cash_and_opens_a_single_leg_position() -> None:
    book = portfolio()

    book.apply(ZVZZT, 10, money("100.00"))

    assert book.cash == Decimal("9000.00")
    position = book.position("ZVZZT")
    assert position is not None
    assert len(position.legs) == 1
    assert position.legs[0].quantity == 10
    assert position.legs[0].average_price == Decimal("100.00")
    # Marked at the fill until something newer arrives, so equity is unchanged
    # by the act of trading at a fair price.
    assert book.equity == Decimal("10000.00")


def test_adding_to_a_leg_takes_a_weighted_average() -> None:
    book = portfolio()

    book.apply(ZVZZT, 10, money("100.00"))
    book.apply(ZVZZT, 30, money("110.00"))

    position = book.position("ZVZZT")
    assert position is not None
    # (10 x 100 + 30 x 110) / 40 = 107.50
    assert position.legs[0].average_price == Decimal("107.50")
    assert position.legs[0].quantity == 40


def test_a_partial_close_realizes_against_the_average_and_leaves_it_alone() -> None:
    book = portfolio()
    book.apply(ZVZZT, 40, money("107.50"))

    realized = book.apply(ZVZZT, -10, money("117.50"))

    assert realized == Decimal("100.00")
    assert book.realized_pnl == Decimal("100.00")
    position = book.position("ZVZZT")
    assert position is not None
    assert position.legs[0].quantity == 30
    assert position.legs[0].average_price == Decimal("107.50")


def test_a_short_realizes_with_the_same_expression_as_a_long() -> None:
    # The signed form is the whole reason there is no separate branch for
    # shorts: sell at 100, cover at 90, make 10 a share.
    book = portfolio()

    book.apply(ZVZZT, -10, money("100.00"))
    assert book.cash == Decimal("11000.00")

    realized = book.apply(ZVZZT, 10, money("90.00"))

    assert realized == Decimal("100.00")
    assert book.position("ZVZZT") is None
    assert book.cash == Decimal("10100.00")
    assert book.equity == Decimal("10100.00")


def test_a_leg_that_crosses_through_zero_closes_and_reopens() -> None:
    # Long 10 at 100, sell 25 at 110: ten shares realize 10 each, and the
    # remaining fifteen are a new short whose basis is 110 with no memory of
    # the old one.
    book = portfolio()
    book.apply(ZVZZT, 10, money("100.00"))

    realized = book.apply(ZVZZT, -25, money("110.00"))

    assert realized == Decimal("100.00")
    position = book.position("ZVZZT")
    assert position is not None
    assert position.legs[0].quantity == -15
    assert position.legs[0].average_price == Decimal("110.00")


def test_commission_leaves_cash_and_realized_pnl_together() -> None:
    book = portfolio()

    book.apply(ZVZZT, 10, money("100.00"), commission=money("1.00"))

    assert book.cash == Decimal("8999.00")
    assert book.commissions == Decimal("1.00")
    # Expensed rather than capitalised, so the basis stays readable off the
    # trade price - see the module docstring in engine/portfolio.py.
    assert book.realized_pnl == Decimal("-1.00")
    position = book.position("ZVZZT")
    assert position is not None
    assert position.legs[0].average_price == Decimal("100.00")


def test_two_legs_under_one_key_are_one_position() -> None:
    # The multi-leg case the single-leg case is a degenerate form of. A vertical
    # spread: long the 20 call, short the 25, submitted under one key.
    book = portfolio("100000.00")
    long_call = option("ZVZZT", date(2026, 3, 20), "20", OptionRight.CALL)
    short_call = option("ZVZZT", date(2026, 3, 20), "25", OptionRight.CALL)

    book.apply(long_call, 1, money("6.00"), key="spread")
    book.apply(short_call, -1, money("2.00"), key="spread")

    position = book.position("spread")
    assert position is not None
    assert len(position.legs) == 2
    # 100 x (6.00 - 2.00) paid for the spread.
    assert book.cash == Decimal("99600.00")

    # Marked at a wider spread, the position is worth 100 x (8.00 - 3.00).
    book.mark_all({long_call.symbol: money("8.00"), short_call.symbol: money("3.00")})
    assert position.market_value(book.marks) == Decimal("500.00")
    assert position.unrealized(book.marks) == Decimal("100.00")


def test_holdings_are_summed_across_positions_unless_a_key_is_named() -> None:
    book = portfolio()
    book.apply(ZVZZT, 10, money("100.00"), key="core")
    book.apply(ZVZZT, 5, money("100.00"), key="tactical")

    assert book.quantity_of(ZVZZT) == 15
    assert book.quantity_of(ZVZZT, "core") == 10
    assert book.quantity_of(ZWZZT) == 0


def test_marking_moves_equity_without_moving_cash() -> None:
    book = portfolio()
    book.apply(ZVZZT, 10, money("100.00"))

    book.mark("ZVZZT", money("120.00"))

    assert book.cash == Decimal("9000.00")
    assert book.unrealized_pnl() == Decimal("200.00")
    assert book.equity == Decimal("10200.00")
    assert book.realized_pnl == Decimal("0")


def test_a_snapshot_is_detached_from_the_portfolio_that_made_it() -> None:
    # The equity curve holds these. One that held a reference would report the
    # final value at every point, which reads as a strategy that never traded.
    book = portfolio()
    book.apply(ZVZZT, 10, money("100.00"))
    before = book.snapshot()

    book.mark("ZVZZT", money("150.00"))

    assert before.equity == Decimal("10000.00")
    assert book.snapshot().equity == Decimal("10500.00")


def test_a_fill_of_zero_is_refused() -> None:
    with pytest.raises(ValueError, match="not a fill"):
        portfolio().apply(ZVZZT, 0, money("100.00"))
