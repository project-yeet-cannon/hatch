"""Where a price stops being a float, and the arithmetic that follows.

The lake stores ``Float64`` because Parquet and DuckDB do, and that is right
for a column holding a decade of quotes. Cash is a different kind of number:
Phase 4's gate is *"a hand-checkable fixture: a tiny synthetic price series
with known correct P&L, asserted to the cent"*, and a gate asserted to the
cent cannot be met by a running total that accumulates binary rounding error
one fill at a time. So the boundary is drawn here - **prices arrive as floats
and become ``Decimal`` exactly once, at the edge of the engine** - and every
cash movement after that is exact.

Three rules, each of which is a bug that has happened to somebody:

- **``Decimal`` is never constructed from a ``float`` directly.**
  ``Decimal(4.56)`` is ``4.5599999999999996`` and prints as such in a failing
  assertion, which sends the reader looking for a bug in the accounting rather
  than in the conversion. ``money()`` goes through ``repr`` so that the value a
  provider reported as ``4.56`` is the value the engine holds.
- **Quantities are integers.** Fractional shares are a brokerage feature rather
  than a market one, and an engine that allows them silently permits a
  position size that no fill could produce. Phase 8 can revisit it against a
  vendor that actually offers them.
- **Cash is quantized to the cent at each movement, and prices are not.** That
  is what a brokerage statement does: a fill of 137 shares at a slipped price
  of 40.8163... moves a whole number of cents, and rounding the *price*
  instead would make the fill price a different number from the one the
  slippage model computed. Quantizing per movement rather than at the end also
  means the running total is always a number that could appear on a statement,
  so a hand check can stop at any row.
"""

from __future__ import annotations

from decimal import ROUND_HALF_EVEN, Decimal
from typing import Final

__all__ = ["CENT", "ZERO", "money", "to_cents"]

#: The quantum every cash movement is rounded to.
CENT: Final = Decimal("0.01")

#: Bound once because "no cash moved" is compared against constantly and
#: ``Decimal("0")`` at each site is a fresh object for no reason.
ZERO: Final = Decimal("0")


def money(value: Decimal | float | int | str) -> Decimal:
    """A ``Decimal`` holding the value as it was *written*, not as it was stored.

    ``repr`` of a Python float is the shortest string that round-trips to the
    same double, so a provider's ``4.56`` comes back as ``Decimal("4.56")``
    rather than as its binary neighbour. That is a deliberate choice about
    which of the two numbers is the real one: the price the venue published is
    a decimal quantity, and the double is an approximation of it that happened
    on the way through Parquet.

    Not quantized. A price may legitimately carry more than two decimals - a
    slipped price, an index level, an option quoted in tenths of a cent - and
    ``to_cents`` is where a number becomes a cash amount.
    """
    if isinstance(value, Decimal):
        return value
    if isinstance(value, float):
        return Decimal(repr(value))
    return Decimal(value)


def to_cents(value: Decimal) -> Decimal:
    """``value`` rounded to the cent, banker's rounding.

    ``ROUND_HALF_EVEN`` rather than ``ROUND_HALF_UP`` because half-up is
    biased: a strategy that trades ten thousand times through a half-cent
    accumulates a drift in its own favour, which is precisely the kind of
    invisible edge this engine exists to not manufacture. It is also the
    default that ``Numeric`` columns in Postgres round with, so a value
    computed here and a value computed in SQL agree.
    """
    return value.quantize(CENT, rounding=ROUND_HALF_EVEN)
