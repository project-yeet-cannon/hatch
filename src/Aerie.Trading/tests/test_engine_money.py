"""Where a price stops being a float.

Three rules from ``engine/money.py``, each of which is a bug that happens
otherwise: a ``Decimal`` is never built from a ``float`` directly, cash is
quantized to the cent, and the rounding is banker's rather than half-up.

The last one looks like a preference and is not. Half-up is biased upward, so a
strategy that trades ten thousand times through a half-cent accumulates a drift
in its own favour - which is exactly the kind of invisible edge this engine
exists not to manufacture.
"""

from decimal import Decimal

from aerie_trading.engine.money import CENT, money, to_cents


def test_a_float_becomes_the_number_it_was_written_as() -> None:
    # Decimal(4.56) is 4.5599999999999996 and prints that way in a failing
    # assertion, which sends the reader looking for a bug in the accounting
    # rather than in the conversion.
    assert money(4.56) == Decimal("4.56")
    assert money(0.1) + money(0.2) == Decimal("0.3")
    # The contrast, spelled through `from_float` because ruff refuses the
    # literal form outright - which is the same rule as this module's, one
    # layer out.
    assert Decimal.from_float(0.1) + Decimal.from_float(0.2) != Decimal("0.3")


def test_the_other_forms_pass_through_unchanged() -> None:
    assert money("22.50") == Decimal("22.50")
    assert money(100) == Decimal("100")
    assert money(Decimal("1.005")) == Decimal("1.005")


def test_a_price_is_not_quantized_but_a_cash_amount_is() -> None:
    # A slipped price legitimately carries more than two decimals; the cash it
    # moves does not.
    assert money(102.0) * Decimal("1.001") == Decimal("102.102")
    assert to_cents(Decimal("102.102")) == Decimal("102.10")
    assert to_cents(Decimal("102.102")).as_tuple().exponent == CENT.as_tuple().exponent


def test_rounding_is_banker_s_rather_than_half_up() -> None:
    assert to_cents(Decimal("1.005")) == Decimal("1.00")
    assert to_cents(Decimal("1.015")) == Decimal("1.02")
    # Which is unbiased across a long run of half-cents, unlike half-up.
    halves = [Decimal(f"{whole}.{cents:02d}5") for whole in range(10) for cents in range(100)]
    assert sum(to_cents(value) - value for value in halves) == Decimal("0")
