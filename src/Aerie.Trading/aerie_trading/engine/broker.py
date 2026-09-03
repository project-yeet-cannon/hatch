"""Orders in, fills out - and the one rule the whole phase is built around.

docs/plans/trading.md Phase 4: *"``SimBroker``: fills at the next bar's open by
default, a configurable slippage model, and a commission model. **No same-bar
fills on the signal bar** - that single shortcut is the most common source of
backtests that cannot be reproduced live."*

That rule is enforced structurally rather than by convention. ``submit()``
puts an order in a queue and returns nothing tradeable; the only thing that
takes orders out of that queue is ``fill_at()``, which the engine calls **at
the start of a tick, before the strategy sees it**. There is no code path from
``on_bar`` to a fill within the same bar, so a strategy cannot take one by
accident and an engine change that introduced one would have to delete the
queue to do it.

**Why the open and not the close.** A signal computed from a bar is computed
from that bar's close, which is knowable only once the bar has ended. The first
price actually available afterwards is the next bar's open. Filling at the
signal bar's close is the same claim as "we traded at a price we learned about
after the fact", and filling at the *signal bar's* open is worse still - it is
a fill placed before the information that motivated it existed.

**Costs are two models rather than one number**, because they scale
differently and Phase 6 wants to re-run a strategy at varying slippage to find
the one that only worked at zero. Slippage moves the *price* and is
proportional to it; commission is charged in *cash* and is per-unit or per
trade. Collapsing them into a single basis-point haircut would make a
per-contract options commission inexpressible, which is a retrofit Phase 10
would pay for.

**The defaults are not zero.** A backtester whose default costs are zero is a
backtester whose default answer is optimistic, and the reference strategies'
whole job is to demonstrate the opposite (``ma_crossover`` losing to
``buy_and_hold`` after costs is a Phase 4 gate). ``ZERO_COSTS`` exists for the
tests that need to isolate the engine's arithmetic from its cost models, and
naming it that way makes every use of it visible.
"""

from __future__ import annotations

import itertools
from collections.abc import Iterator, Mapping, Sequence
from dataclasses import dataclass, field
from datetime import datetime
from decimal import Decimal
from typing import Protocol, runtime_checkable

from aerie_trading.engine.instruments import Instrument
from aerie_trading.engine.money import ZERO, money, to_cents
from aerie_trading.engine.portfolio import Portfolio, position_key_for

__all__ = [
    "RETAIL_EQUITY_COSTS",
    "ZERO_COSTS",
    "BpsSlippage",
    "Broker",
    "CommissionModel",
    "Costs",
    "ExpiredOrder",
    "Fill",
    "NoCommission",
    "NoSlippage",
    "Order",
    "PerTradeCommission",
    "PerUnitCommission",
    "SimBroker",
    "SlippageModel",
]


@dataclass(frozen=True, slots=True)
class Order:
    """A market order, submitted on one bar and filled on the next.

    Market orders only, in this phase and deliberately. A limit order's fill is
    a claim about whether the book traded through a price, which minute bars
    can only guess at - a bar whose low touched the limit says the price was
    printed, not that a resting order of this size would have been hit. The
    guess is defensible and is a decision to make with a measurement in front
    of it (Phase 6's cost sensitivity), not one to smuggle in here.

    ``quantity`` is signed: positive buys, negative sells. ``position_key``
    names the position the fill lands in; ``None`` means the instrument's own
    symbol, which is what makes single-leg equity positions self-keying.
    ``tag`` is the strategy's own label for why it traded, carried through to
    the fill so a trade blotter can say which rule produced each line.
    """

    instrument: Instrument
    quantity: int
    submitted_at: datetime
    position_key: str | None = None
    tag: str = ""
    id: int = 0

    def __post_init__(self) -> None:
        if self.quantity == 0:
            raise ValueError("an order for zero quantity is not an order")

    @property
    def key(self) -> str:
        return (
            self.position_key
            if self.position_key is not None
            else position_key_for(self.instrument)
        )

    @property
    def is_buy(self) -> bool:
        return self.quantity > 0


@dataclass(frozen=True, slots=True)
class Fill:
    """What actually happened: a price, a cost, and the P&L it realized.

    ``reference_price`` is kept beside ``price`` because the difference between
    them *is* the slippage, and a blotter that stored only the filled price
    would make "what did execution cost us" a number that has to be recomputed
    from a model that may since have changed.
    """

    order: Order
    instrument: Instrument
    quantity: int
    price: Decimal
    reference_price: Decimal
    commission: Decimal
    filled_at: datetime
    realized_pnl: Decimal
    position_key: str

    @property
    def slippage_cost(self) -> Decimal:
        """Cash lost to slippage on this fill, always non-negative for an adverse model."""
        return (
            abs(self.price - self.reference_price)
            * money(abs(self.quantity))
            * money(self.instrument.multiplier)
        )

    @property
    def notional(self) -> Decimal:
        return money(self.quantity) * self.price * money(self.instrument.multiplier)


@dataclass(frozen=True, slots=True)
class ExpiredOrder:
    """An order that never found a bar to fill against.

    Recorded rather than dropped. It happens for two reasons and only one of
    them is benign: an order submitted on the final bar of the window has no
    next bar (benign, and true of every strategy that trades on its last
    observation), or the instrument stopped printing bars mid-run, which means
    the strategy is holding something the data no longer covers. A run whose
    expiries are all on the last timestamp is fine; one with expiries in the
    middle is a data gap wearing a strategy's clothes.
    """

    order: Order
    reason: str


# -- slippage ---------------------------------------------------------------


@runtime_checkable
class SlippageModel(Protocol):
    """How far the fill price moves against the order.

    Adverse by construction: the sign is applied here rather than by the
    caller, so a model cannot accidentally be written to pay the trader.
    """

    @property
    def description(self) -> str:
        """One line, for the run record. What a person needs to reproduce the run."""
        ...

    def fill_price(self, order: Order, reference_price: Decimal) -> Decimal: ...


@dataclass(frozen=True, slots=True)
class NoSlippage:
    """Fills at the reference price exactly.

    For the fixture test, where the arithmetic being checked is the portfolio's
    rather than the cost model's, and for a deliberate zero-cost run in Phase
    6's sensitivity sweep - the one that finds strategies which only work when
    execution is free.
    """

    @property
    def description(self) -> str:
        return "none"

    def fill_price(self, order: Order, reference_price: Decimal) -> Decimal:
        return reference_price


@dataclass(frozen=True, slots=True)
class BpsSlippage:
    """A fixed fraction of the price, paid in the adverse direction.

    Proportional rather than absolute because the thing being modelled is
    mostly the half-spread, and a spread is roughly proportional to price
    across the liquid names this plan trades. It is a crude model and is
    labelled as one: it does not widen with size, and it does not widen in a
    fast market, which are the two ways execution actually gets expensive.
    Phase 6 varies it rather than trusting it.
    """

    bps: Decimal = Decimal("2")

    def __post_init__(self) -> None:
        object.__setattr__(self, "bps", money(self.bps))
        if self.bps < 0:
            raise ValueError("slippage cannot be negative; it would pay the trader")

    @property
    def description(self) -> str:
        return f"{self.bps} bps"

    def fill_price(self, order: Order, reference_price: Decimal) -> Decimal:
        offset = reference_price * self.bps / Decimal("10000")
        return reference_price + offset if order.is_buy else reference_price - offset


# -- commission -------------------------------------------------------------


@runtime_checkable
class CommissionModel(Protocol):
    """What the broker charges, in cash, for one fill. Never negative."""

    @property
    def description(self) -> str: ...

    def commission(self, order: Order, fill_price: Decimal) -> Decimal: ...


@dataclass(frozen=True, slots=True)
class NoCommission:
    """Charges nothing. The honest default for a US retail equity account in 2026."""

    @property
    def description(self) -> str:
        return "none"

    def commission(self, order: Order, fill_price: Decimal) -> Decimal:
        return ZERO


@dataclass(frozen=True, slots=True)
class PerUnitCommission:
    """A rate per share or per contract, with an optional floor and cap.

    One model for both because a "unit" is a share for an equity and a contract
    for an option - the multiplier is what differs between them, and it is on
    the instrument. That is what keeps ``$0.65 per contract`` expressible
    without a second model arriving in Phase 10.
    """

    per_unit: Decimal = Decimal("0.005")
    minimum: Decimal = ZERO
    maximum: Decimal | None = None

    def __post_init__(self) -> None:
        object.__setattr__(self, "per_unit", money(self.per_unit))
        object.__setattr__(self, "minimum", money(self.minimum))
        if self.maximum is not None:
            object.__setattr__(self, "maximum", money(self.maximum))
        if self.per_unit < 0 or self.minimum < 0:
            raise ValueError("commission cannot be negative")

    @property
    def description(self) -> str:
        cap = "" if self.maximum is None else f", max {self.maximum}"
        return f"{self.per_unit}/unit, min {self.minimum}{cap}"

    def commission(self, order: Order, fill_price: Decimal) -> Decimal:
        charge = money(abs(order.quantity)) * self.per_unit
        charge = max(charge, self.minimum)
        if self.maximum is not None:
            charge = min(charge, self.maximum)
        return to_cents(charge)


@dataclass(frozen=True, slots=True)
class PerTradeCommission:
    """A flat charge per fill, regardless of size."""

    amount: Decimal = Decimal("1.00")

    def __post_init__(self) -> None:
        object.__setattr__(self, "amount", money(self.amount))
        if self.amount < 0:
            raise ValueError("commission cannot be negative")

    @property
    def description(self) -> str:
        return f"{self.amount}/trade"

    def commission(self, order: Order, fill_price: Decimal) -> Decimal:
        return to_cents(self.amount)


@dataclass(frozen=True, slots=True)
class Costs:
    """A slippage model and a commission model, carried together.

    One object because they are always chosen together and a run record has to
    store both to be reproducible - and because a signature taking two
    similarly-shaped protocol arguments is one where they can be passed in the
    wrong order without complaint.
    """

    slippage: SlippageModel = field(default_factory=NoSlippage)
    commission: CommissionModel = field(default_factory=NoCommission)

    def describe(self) -> Mapping[str, str]:
        """What Phase 5's ``run`` row records about how this run was priced."""
        return {
            "slippage": self.slippage.description,
            "commission": self.commission.description,
        }


#: Nothing charged. Used by the fixture test to isolate the portfolio's
#: arithmetic, and by Phase 6's sensitivity sweep as the free-execution end of
#: the range. Named rather than spelled inline so that every place a backtest
#: is run without costs is greppable.
ZERO_COSTS = Costs()

#: The default. A US retail equity account charges no commission and pays the
#: spread, so the whole cost is slippage - two basis points each way, which is
#: about a penny on a fifty-dollar stock and is conservative for a liquid name
#: and optimistic for an illiquid one. It is a guess, it is labelled a guess,
#: and Phase 6 exists to find the strategies that only survive a smaller one.
RETAIL_EQUITY_COSTS = Costs(slippage=BpsSlippage(Decimal("2")), commission=NoCommission())


# -- the broker -------------------------------------------------------------


@runtime_checkable
class Broker(Protocol):
    """Where orders go.

    ``SimBroker`` is the first implementation; Phase 9's live broker is the
    second and routes to Schwab. The protocol carries no method for *reading*
    positions, because a strategy reads them from the portfolio - which in a
    live run is reconciled against the broker rather than being a second
    opinion about it.
    """

    def submit(self, order: Order) -> Order:
        """Accept an order and return it with its identity assigned."""
        ...

    @property
    def pending(self) -> Sequence[Order]: ...


class SimBroker:
    """Fills queued orders against the next bar's open.

    Holds the portfolio it fills into, because a fill is not a message - it is
    a cash movement and a position change that must happen exactly once. Two
    objects that both thought they were applying the same fill is the failure
    this ownership prevents.
    """

    __slots__ = ("_costs", "_expired", "_fills", "_ids", "_pending", "_portfolio")

    def __init__(self, portfolio: Portfolio, costs: Costs | None = None) -> None:
        self._portfolio = portfolio
        self._costs = costs if costs is not None else RETAIL_EQUITY_COSTS
        self._pending: list[Order] = []
        self._fills: list[Fill] = []
        self._expired: list[ExpiredOrder] = []
        # Monotonic within the run, from 1. Order ids are what a trade record
        # joins on in Phase 5, and starting at 1 rather than at a random value
        # is part of what makes two identical runs byte-identical.
        self._ids = itertools.count(1)

    @property
    def portfolio(self) -> Portfolio:
        return self._portfolio

    @property
    def costs(self) -> Costs:
        return self._costs

    @property
    def pending(self) -> Sequence[Order]:
        return tuple(self._pending)

    @property
    def fills(self) -> Sequence[Fill]:
        return tuple(self._fills)

    @property
    def expired(self) -> Sequence[ExpiredOrder]:
        return tuple(self._expired)

    def submit(self, order: Order) -> Order:
        """Queue ``order`` for the next bar. It does not fill now, and cannot.

        See the module docstring: this is the whole no-same-bar-fill rule, and
        it is one line because the rule is structural rather than checked.
        """
        identified = Order(
            instrument=order.instrument,
            quantity=order.quantity,
            submitted_at=order.submitted_at,
            position_key=order.position_key,
            tag=order.tag,
            id=next(self._ids),
        )
        self._pending.append(identified)
        return identified

    def fill_at(self, moment: datetime, opens: Mapping[str, Decimal]) -> Sequence[Fill]:
        """Fill every pending order that has an open price at ``moment``.

        Called by the engine at the top of a tick, before the strategy runs.
        An order whose instrument has no bar at this instant **stays queued**
        rather than being cancelled: a symbol that is halted for a session is
        the case, and an order that quietly vanished would leave a strategy
        believing it was in a trade it never entered. ``expire_all`` is how the
        queue is emptied at the end of a run, with a reason recorded.
        """
        if not self._pending:
            return ()

        filled: list[Fill] = []
        still_pending: list[Order] = []
        for order in self._pending:
            reference = opens.get(order.instrument.symbol)
            if reference is None:
                still_pending.append(order)
                continue
            price = self._costs.slippage.fill_price(order, reference)
            commission = self._costs.commission.commission(order, price)
            realized = self._portfolio.apply(
                instrument=order.instrument,
                quantity=order.quantity,
                price=price,
                commission=commission,
                key=order.key,
            )
            filled.append(
                Fill(
                    order=order,
                    instrument=order.instrument,
                    quantity=order.quantity,
                    price=price,
                    reference_price=reference,
                    commission=commission,
                    filled_at=moment,
                    realized_pnl=realized,
                    position_key=order.key,
                )
            )
        self._pending = still_pending
        self._fills.extend(filled)
        return tuple(filled)

    def expire_all(self, reason: str) -> Sequence[ExpiredOrder]:
        """Drop every pending order, recording why. Called once, at the end of a run."""
        expired = tuple(ExpiredOrder(order=order, reason=reason) for order in self._pending)
        self._pending = []
        self._expired.extend(expired)
        return expired

    def __iter__(self) -> Iterator[Fill]:
        return iter(self._fills)
