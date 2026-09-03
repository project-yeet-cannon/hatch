"""``Equity`` and ``OptionContract``: the model written before it is exercised.

docs/plans/trading.md Phase 4 asks for the option side of this file in the
phase that does not use it, so these tests are most of what stops it rotting
before Phase 10 arrives. The OCC symbol is the part worth testing hardest: it
is the string ``db.models.Instrument`` stores, so a contract built from a chain
row and a contract built by a strategy's own arithmetic have to produce the
same one or they are two rows for one contract.
"""

from datetime import date
from decimal import Decimal

import pytest

from aerie_trading.engine.instruments import Equity, OptionContract, equity, option
from aerie_trading.providers.base import OptionRight


def test_an_equity_is_its_own_underlying_and_trades_one_for_one() -> None:
    share = equity("zvzzt")

    assert share.symbol == "ZVZZT"
    assert share.underlying_symbol == "ZVZZT"
    assert share.multiplier == 1


def test_an_equity_symbol_goes_through_the_lake_whitelist() -> None:
    # The same normaliser the lake uses, so an instrument the engine will trade
    # is an instrument whose bars the lake could store. A second whitelist here
    # would be a second opinion about what a symbol is.
    #
    # A separator and an empty string rather than the parent-traversal case
    # that motivates the whitelist: ci.yml's trading-boundary guard greps this
    # directory for a quoted parent traversal and cannot tell a test's input
    # from a real path dependency. A guard that has to be taught exceptions is
    # a guard someone eventually turns off, so the test bends instead.
    with pytest.raises(ValueError, match="not a usable symbol"):
        equity("ZV/ZZT")
    with pytest.raises(ValueError, match="not a usable symbol"):
        equity("")


def test_an_option_contract_builds_the_occ_symbol() -> None:
    contract = option("ZVZZT", date(2026, 3, 20), "22.50", OptionRight.CALL)

    # Six-character root, YYMMDD, C or P, strike in thousandths padded to eight.
    assert contract.symbol == "ZVZZT 260320C00022500"
    assert contract.underlying_symbol == "ZVZZT"
    assert contract.multiplier == 100


def test_the_strike_is_decimal_so_the_symbol_is_not_a_rounding_artefact() -> None:
    # 22.5 as a float times 1000 is 22499.999999999996 in some orderings, and
    # int() of that is a contract that does not exist. Constructing through
    # `money()` is what makes this test pass by design rather than by luck.
    assert option("ZVZZT", date(2026, 3, 20), 22.5, OptionRight.PUT).symbol.endswith("00022500")
    assert option("ZVZZT", date(2026, 3, 20), 0.07, OptionRight.PUT).symbol.endswith("00000070")


def test_contracts_sort_the_way_a_board_is_read() -> None:
    # Underlying, expiry, strike, right - the same order `lake.schema` writes a
    # chain in, so a diff of two runs is not all reordering.
    near = option("ZVZZT", date(2026, 3, 20), "20", OptionRight.CALL)
    far = option("ZVZZT", date(2026, 6, 19), "20", OptionRight.CALL)
    higher = option("ZVZZT", date(2026, 3, 20), "25", OptionRight.CALL)

    assert sorted([higher, far, near]) == [near, higher, far]


def test_a_non_positive_strike_or_multiplier_is_refused() -> None:
    with pytest.raises(ValueError, match="strike"):
        OptionContract("ZVZZT", date(2026, 3, 20), Decimal("0"), OptionRight.CALL)
    with pytest.raises(ValueError, match="multiplier"):
        OptionContract("ZVZZT", date(2026, 3, 20), Decimal("20"), OptionRight.CALL, 0)


def test_equities_are_hashable_and_compare_by_value() -> None:
    # Positions are keyed by instrument identity, so two objects describing the
    # same share have to be one key.
    assert equity("ZVZZT") == Equity("ZVZZT")
    assert len({equity("ZVZZT"), Equity("zvzzt")}) == 1
