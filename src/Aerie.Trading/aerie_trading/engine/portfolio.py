"""Legs, cash, marks, and the two kinds of P&L.

docs/plans/trading.md Phase 4: *"``Position`` as a set of legs, cash,
mark-to-market, realized and unrealized P&L. Single-leg equity positions are
the degenerate case, not the model."* That last sentence is the design. A
vertical spread is one decision, one risk and one thing to close, and a
portfolio that models it as two unrelated rows keyed by contract symbol cannot
answer "what is this position worth" without the strategy telling it which two
rows to add up. So a ``Position`` is a set of legs with a key, an equity
position is a position with one leg, and nothing in the accounting below has a
branch for which of the two it is holding.

**The position key.** A fill names the position it belongs to; when it does
not, the key is the instrument's own symbol. That is what makes the equity case
free - two buys of ZVZZT land in one position without anyone naming it - while
leaving Phase 10 a spread that is one position because its legs were submitted
under one key. Keys are opaque strings chosen by the strategy, so the engine
never has to guess whether two legs are related.

**Weighted-average cost, and commissions expensed rather than capitalised.**
When a leg is added to, the average price moves; when it is reduced, the
difference between the exit price and that average is realized. Commissions
come out of cash and out of realized P&L *at the moment they are paid* rather
than being folded into the basis. Both conventions are defensible and the
choice is made here for one reason: a hand check of the fixture (Phase 4's
gate, asserted to the cent) can then read the average price straight off the
trade prices, with no commission arithmetic hidden inside it.

**A short position is a negative quantity, not a separate kind of leg.** The
realized-P&L expression is the same for both because it is signed:
``(exit - average) x closed x multiplier``, where ``closed`` carries the sign
of the position being reduced. A leg that crosses through zero - selling 25
of a 10-share long - is closed and reopened, which is the only case where one
fill does two things and is therefore the only case worth spelling out
explicitly in the code below.
"""

from __future__ import annotations

from collections.abc import Iterator, Mapping
from dataclasses import dataclass, replace
from decimal import Decimal

from aerie_trading.engine.instruments import Instrument
from aerie_trading.engine.money import ZERO, money, to_cents

__all__ = ["Leg", "Portfolio", "PortfolioSnapshot", "Position", "position_key_for"]


def position_key_for(instrument: Instrument) -> str:
    """The key a fill lands under when the strategy did not name one.

    The instrument's own symbol, which makes every single-instrument position
    self-keying. A strategy that wants two legs in one position passes a key of
    its own choosing; nothing here interprets it.
    """
    return instrument.symbol


@dataclass(frozen=True, slots=True)
class Leg:
    """One instrument held within a position, at a signed quantity.

    ``average_price`` is per *unit* - per share, or per contract-unit before
    the multiplier - because that is the number a fill reports and the number
    a person compares against a chart. The multiplier is applied when the leg
    is valued, in one place, rather than being baked into a basis that then
    reads as a strange number.
    """

    instrument: Instrument
    quantity: int
    average_price: Decimal

    @property
    def is_flat(self) -> bool:
        return self.quantity == 0

    def market_value(self, price: Decimal) -> Decimal:
        """What the leg is worth at ``price``, signed by direction."""
        return money(self.quantity) * price * money(self.instrument.multiplier)

    def cost_basis(self) -> Decimal:
        """What the leg cost, signed by direction. Negative for a short's credit."""
        return money(self.quantity) * self.average_price * money(self.instrument.multiplier)

    def unrealized(self, price: Decimal) -> Decimal:
        """Mark-to-market less cost basis. Correct for shorts without a sign flip."""
        return self.market_value(price) - self.cost_basis()


@dataclass(frozen=True, slots=True)
class Position:
    """A set of legs held as one thing.

    Frozen and rebuilt on every fill rather than mutated. Positions are small -
    one leg for an equity, four for the widest spread anyone sensible opens -
    and an immutable one can be handed to a strategy without the engine having
    to trust that the strategy will not edit the portfolio it is reading.
    """

    key: str
    legs: tuple[Leg, ...]

    def __iter__(self) -> Iterator[Leg]:
        return iter(self.legs)

    @property
    def is_flat(self) -> bool:
        """True when every leg is closed. A flat position is dropped by the portfolio."""
        return all(leg.is_flat for leg in self.legs)

    def leg_for(self, instrument: Instrument) -> Leg | None:
        for leg in self.legs:
            if leg.instrument == instrument:
                return leg
        return None

    def quantity_of(self, instrument: Instrument) -> int:
        """The signed size held in ``instrument``, or zero. The common question."""
        leg = self.leg_for(instrument)
        return 0 if leg is None else leg.quantity

    def market_value(self, marks: Mapping[str, Decimal]) -> Decimal:
        return sum(
            (leg.market_value(marks[leg.instrument.symbol]) for leg in self.legs),
            start=ZERO,
        )

    def unrealized(self, marks: Mapping[str, Decimal]) -> Decimal:
        return sum(
            (leg.unrealized(marks[leg.instrument.symbol]) for leg in self.legs),
            start=ZERO,
        )


@dataclass(frozen=True, slots=True)
class PortfolioSnapshot:
    """The portfolio's numbers at one instant, detached from the portfolio.

    What the equity curve is made of, and what a run record stores. Detached
    because the portfolio is mutable by design and a curve holding references
    to it would be a curve where every point reports the final value - a bug
    that looks like a strategy that never traded.
    """

    cash: Decimal
    market_value: Decimal
    realized_pnl: Decimal
    unrealized_pnl: Decimal
    commissions: Decimal

    @property
    def equity(self) -> Decimal:
        """Cash plus what the positions are worth. The number the curve plots."""
        return self.cash + self.market_value


class Portfolio:
    """Cash, positions, and the marks they are valued at.

    Mutable, and owned by exactly one engine loop. The strategy sees it through
    ``StrategyContext`` and can read every number on it; it cannot move cash,
    because the only way to change a portfolio is a ``Fill``, and the only
    thing that produces a ``Fill`` is a broker.
    """

    __slots__ = ("_commissions", "_marks", "_positions", "_realized", "cash")

    def __init__(self, starting_cash: Decimal | float | int | str) -> None:
        self.cash = to_cents(money(starting_cash))
        self._positions: dict[str, Position] = {}
        # Last price seen per instrument symbol. Seeded by a fill so that a
        # position opened at 10:00 has a value at 10:00 rather than only after
        # the next mark - an unmarked position valued at zero is a silent
        # error that reads as a strategy losing everything it just bought.
        self._marks: dict[str, Decimal] = {}
        self._realized = ZERO
        self._commissions = ZERO

    # -- reading -------------------------------------------------------------

    @property
    def positions(self) -> Mapping[str, Position]:
        """Every open position, keyed. Flat positions are not kept."""
        return self._positions

    @property
    def realized_pnl(self) -> Decimal:
        """Closed P&L, net of the commissions paid to date."""
        return self._realized

    @property
    def commissions(self) -> Decimal:
        """Total commission paid. Already deducted from cash and from realized P&L."""
        return self._commissions

    @property
    def marks(self) -> Mapping[str, Decimal]:
        return self._marks

    def position(self, key: str) -> Position | None:
        return self._positions.get(key)

    def quantity_of(self, instrument: Instrument, key: str | None = None) -> int:
        """Signed size held in ``instrument``, across positions or within one.

        The question a strategy asks before deciding whether it is already in
        the trade, which is why it defaults to summing across every position
        rather than requiring the caller to know which key its own earlier
        order used.
        """
        if key is not None:
            position = self._positions.get(key)
            return 0 if position is None else position.quantity_of(instrument)
        return sum(position.quantity_of(instrument) for position in self._positions.values())

    def market_value(self) -> Decimal:
        return sum(
            (position.market_value(self._marks) for position in self._positions.values()),
            start=ZERO,
        )

    def unrealized_pnl(self) -> Decimal:
        return sum(
            (position.unrealized(self._marks) for position in self._positions.values()),
            start=ZERO,
        )

    @property
    def equity(self) -> Decimal:
        """Cash plus market value - the one number a backtest is judged on."""
        return self.cash + self.market_value()

    def snapshot(self) -> PortfolioSnapshot:
        return PortfolioSnapshot(
            cash=self.cash,
            market_value=to_cents(self.market_value()),
            realized_pnl=to_cents(self._realized),
            unrealized_pnl=to_cents(self.unrealized_pnl()),
            commissions=to_cents(self._commissions),
        )

    # -- writing -------------------------------------------------------------

    def mark(self, symbol: str, price: Decimal) -> None:
        """Record ``symbol``'s current price. Marking is not a trade."""
        self._marks[symbol] = price

    def mark_all(self, prices: Mapping[str, Decimal]) -> None:
        self._marks.update(prices)

    def apply(
        self,
        instrument: Instrument,
        quantity: int,
        price: Decimal,
        commission: Decimal = ZERO,
        key: str | None = None,
    ) -> Decimal:
        """Apply one fill, and return the realized P&L it produced.

        ``quantity`` is signed: positive buys, negative sells. The return value
        is the realized P&L *of this fill*, net of its commission, which is what
        a trade record stores - the running total is on the portfolio.
        """
        if quantity == 0:
            raise ValueError("a fill of zero quantity is not a fill")

        position_key = key if key is not None else position_key_for(instrument)
        position = self._positions.get(position_key, Position(key=position_key, legs=()))
        existing = position.leg_for(instrument)
        before = 0 if existing is None else existing.quantity
        average = existing.average_price if existing is not None else price

        realized = ZERO
        after = before + quantity

        if before != 0 and (before > 0) != (quantity > 0):
            # A reduction, a close, or a flip. `closed` carries the sign of the
            # position being reduced, which is what makes the expression below
            # correct for shorts without a second branch.
            closed = -quantity if abs(quantity) <= abs(before) else before
            realized = (price - average) * money(closed) * money(instrument.multiplier)
            if abs(quantity) > abs(before):
                # Crossed through zero: the remainder opens the other way, at
                # this fill's price, with no memory of the old basis.
                average = price
            elif after == 0:
                average = ZERO
        elif before != 0:
            # Adding to the leg: weighted average over units, before any
            # multiplier, so the number stays comparable to a chart.
            total = money(abs(before)) * average + money(abs(quantity)) * price
            average = total / money(abs(after))

        legs = tuple(leg for leg in position.legs if leg.instrument != instrument)
        if after != 0:
            legs = (*legs, Leg(instrument=instrument, quantity=after, average_price=average))
        # Legs sorted so that two portfolios that reached the same state by
        # different fill orders compare equal, which is what the determinism
        # gate needs of the snapshot it hashes.
        updated = replace(position, legs=tuple(sorted(legs, key=lambda leg: leg.instrument.symbol)))

        if updated.legs:
            self._positions[position_key] = updated
        else:
            self._positions.pop(position_key, None)

        # Cash: what the trade cost, then what the broker charged. Quantized
        # per movement rather than at the end - see engine/money.py.
        notional = money(quantity) * price * money(instrument.multiplier)
        self.cash = to_cents(self.cash - to_cents(notional) - to_cents(commission))
        self._commissions += commission
        net = realized - commission
        self._realized += net
        # The fill price is the most recent trade in this instrument, so it is
        # the best mark until the tick's close arrives. Set rather than
        # defaulted: a stale mark from the previous bar is not better data.
        self._marks[instrument.symbol] = price
        return net
