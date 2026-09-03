"""What can be held: an equity or an option contract, behind one type.

docs/plans/trading.md Phase 4 asks for this file **in the phase that does not
use half of it**, and says why in the bullet itself: *"written in this phase
even though only ``Equity`` is exercised, because this is the retrofit the plan
exists to avoid."* The same argument put the option columns on
``db.models.Instrument`` at Phase 1. An engine whose instrument is a ticker
string is an engine where adding an expiry means touching every signature that
carries one.

``Instrument`` is a **union rather than a base class**, and that is the load-
bearing choice here. A base class invites an ``isinstance`` ladder that grows a
third branch nobody notices; a union of two frozen dataclasses is exhaustively
matchable, so pyright reports the ladder that forgot a case. The two members
agree on the three attributes every consumer needs - ``symbol``,
``underlying_symbol`` and ``multiplier`` - so code that does not care which it
is holding never has to ask.

**The multiplier is stored, never assumed.** 1 for an equity and 100 for a
standard US option, but an adjusted contract after a split has neither, and a
P&L computed against a hardcoded 100 is wrong in exactly the cases nobody
checks by hand. Same sentence as ``db/models.py``, same reason.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import date
from decimal import Decimal

from aerie_trading.engine.money import money
from aerie_trading.lake.layout import normalise_symbol
from aerie_trading.providers.base import OptionRight

__all__ = ["Equity", "Instrument", "OptionContract", "equity", "option"]


@dataclass(frozen=True, slots=True, order=True)
class Equity:
    """A share, an ETF, or anything else that trades one-for-one.

    Ordered so that a set of instruments has a deterministic iteration order,
    which is what makes an equity curve reproducible when a strategy holds
    several names: dictionary order follows insertion, insertion follows the
    order the strategy submitted in, and a run that sorted its universe
    differently would fill in a different order and accumulate rounding
    differently. Determinism is a Phase 4 gate, so this is not decoration.
    """

    symbol: str

    def __post_init__(self) -> None:
        # The lake's whitelist rather than a second one: a symbol reaching the
        # engine has to be a symbol the lake could store, or a backtest can
        # name a position whose data can never be read back.
        object.__setattr__(self, "symbol", normalise_symbol(self.symbol))

    @property
    def underlying_symbol(self) -> str:
        """Itself. Named so that a caller can group legs by underlying without asking."""
        return self.symbol

    @property
    def multiplier(self) -> int:
        return 1


@dataclass(frozen=True, slots=True, order=True)
class OptionContract:
    """One listed contract: underlying, expiry, strike, right.

    Field order is the sort order, and it is the order a board is read in -
    underlying, then expiry, then right, then strike - which matches the sort
    ``lake.schema.chain_frame`` writes. Two spellings of "the natural order of
    a chain" would be a difference nobody notices until a diff of two runs is
    all reordering.

    The strike is a ``Decimal``. A strike is a decimal quantity by definition
    and a float one is how ``22.5`` becomes ``22.499999999999996`` in the OCC
    symbol this builds - which is a contract that does not exist.
    """

    underlying_symbol: str
    expiry: date
    strike: Decimal
    right: OptionRight
    multiplier: int = 100

    def __post_init__(self) -> None:
        object.__setattr__(self, "underlying_symbol", normalise_symbol(self.underlying_symbol))
        object.__setattr__(self, "strike", money(self.strike))
        if self.strike <= 0:
            raise ValueError(f"{self.underlying_symbol}: strike must be positive")
        if self.multiplier <= 0:
            raise ValueError(f"{self.underlying_symbol}: multiplier must be positive")

    @property
    def symbol(self) -> str:
        """The OCC contract symbol, which is what ``db.models.Instrument`` stores.

        Built rather than carried, so that a contract constructed from a chain
        row and a contract constructed from a strategy's own arithmetic are the
        same object and key the same position. The format is the OCC's:
        a six-character root, ``YYMMDD``, ``C`` or ``P``, then the strike in
        thousandths of a dollar padded to eight digits.
        """
        root = self.underlying_symbol.ljust(6)
        letter = "C" if self.right is OptionRight.CALL else "P"
        thousandths = int(self.strike * 1000)
        return f"{root}{self.expiry:%y%m%d}{letter}{thousandths:08d}"


#: An equity or an option contract. A union rather than a base class - see the
#: module docstring - so that a consumer that must handle both is checked for
#: having handled both.
Instrument = Equity | OptionContract


def equity(symbol: str) -> Equity:
    """``Equity(symbol)``, spelled so a strategy reads as prose."""
    return Equity(symbol)


def option(
    underlying: str,
    expiry: date,
    strike: Decimal | float | str,
    right: OptionRight,
    multiplier: int = 100,
) -> OptionContract:
    """An ``OptionContract``, taking the strike in whatever form the caller has it."""
    return OptionContract(underlying, expiry, money(strike), right, multiplier)
