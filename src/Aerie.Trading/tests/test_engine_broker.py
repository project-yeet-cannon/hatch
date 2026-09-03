"""``SimBroker``, its queue, and the two cost models.

The queue is the interesting part. Phase 4's rule - no same-bar fills on the
signal bar - is enforced by the fact that ``submit`` and ``fill_at`` are
different calls made at different points in the loop, so the tests worth having
are the ones about what the queue does when a fill is *not* straightforward: a
symbol with no bar at this instant, an order left over when the data runs out,
and the identity a fill carries back to the order that caused it.

The cost models are tested against hand-written arithmetic for the same reason
the fixture is: a model checked against a value computed by the model is a
model checked against itself.
"""

from datetime import UTC, date, datetime
from decimal import Decimal

import pytest

from aerie_trading.engine.broker import (
    BpsSlippage,
    Broker,
    Costs,
    NoCommission,
    NoSlippage,
    Order,
    PerTradeCommission,
    PerUnitCommission,
    SimBroker,
)
from aerie_trading.engine.instruments import Instrument, equity, option
from aerie_trading.engine.money import money
from aerie_trading.engine.portfolio import Portfolio
from aerie_trading.providers.base import OptionRight

NOW = datetime(2026, 1, 5, 14, 30, tzinfo=UTC)
LATER = datetime(2026, 1, 6, 14, 30, tzinfo=UTC)
ZVZZT = equity("ZVZZT")
ZWZZT = equity("ZWZZT")


def broker(costs: Costs | None = None, cash: str = "10000.00") -> SimBroker:
    return SimBroker(Portfolio(cash), costs)


def buy(quantity: int = 10, instrument: Instrument = ZVZZT) -> Order:
    return Order(instrument=instrument, quantity=quantity, submitted_at=NOW)


def test_a_sim_broker_satisfies_the_protocol() -> None:
    assert isinstance(broker(), Broker)


def test_submitting_does_not_fill() -> None:
    # The whole no-same-bar-fill rule, from the broker's side: there is no code
    # path from submit() to a cash movement.
    market = broker()

    order = market.submit(buy())

    assert order.id == 1
    assert market.pending == (order,)
    assert market.fills == ()
    assert market.portfolio.cash == Decimal("10000.00")


def test_order_ids_start_at_one_and_increase() -> None:
    # Part of what makes two identical runs byte-identical: an id derived from
    # anything but a counter would differ between runs.
    market = broker()

    assert [market.submit(buy()).id for _ in range(3)] == [1, 2, 3]


def test_a_fill_happens_at_the_open_it_is_given() -> None:
    market = broker(Costs(slippage=NoSlippage(), commission=NoCommission()))
    market.submit(buy(10))

    fills = market.fill_at(LATER, {"ZVZZT": money("102.00")})

    assert len(fills) == 1
    fill = fills[0]
    assert fill.price == Decimal("102.00")
    assert fill.filled_at == LATER
    assert fill.position_key == "ZVZZT"
    assert market.pending == ()
    assert market.portfolio.cash == Decimal("8980.00")


def test_an_order_whose_symbol_did_not_print_stays_queued() -> None:
    # A halted symbol, not a cancelled order. An order that quietly vanished
    # would leave a strategy believing it was in a trade it never entered.
    market = broker()
    market.submit(buy(10, ZWZZT))

    assert market.fill_at(LATER, {"ZVZZT": money("102.00")}) == ()
    assert len(market.pending) == 1

    market.fill_at(LATER, {"ZWZZT": money("50.00")})
    assert market.pending == ()
    assert len(market.fills) == 1


def test_expiring_records_the_orders_rather_than_dropping_them() -> None:
    market = broker()
    market.submit(buy(10))

    expired = market.expire_all("no more bars")

    assert len(expired) == 1
    assert expired[0].reason == "no more bars"
    assert market.pending == ()
    assert market.expired == tuple(expired)


def test_slippage_is_adverse_in_both_directions() -> None:
    model = BpsSlippage(Decimal("10"))

    # 100.00 x 1.001 buying, 100.00 x 0.999 selling.
    assert model.fill_price(buy(1), money("100.00")) == Decimal("100.100")
    assert model.fill_price(buy(-1), money("100.00")) == Decimal("99.900")


def test_negative_slippage_is_refused() -> None:
    # A model that paid the trader is not a cost model, and the mistake is a
    # sign rather than a typo - which is exactly the kind that produces a
    # plausible equity curve.
    with pytest.raises(ValueError, match="pay the trader"):
        BpsSlippage(Decimal("-1"))


def test_per_unit_commission_covers_shares_and_contracts_with_one_model() -> None:
    schedule = PerUnitCommission(Decimal("0.005"), minimum=Decimal("1.00"))

    # 100 shares at half a cent is 0.50, floored to the 1.00 minimum.
    assert schedule.commission(buy(100), money("50.00")) == Decimal("1.00")
    # 1000 shares is 5.00, above the floor.
    assert schedule.commission(buy(1000), money("50.00")) == Decimal("5.00")

    # And the same model priced per contract, which is what Phase 10 needs.
    contracts = option("ZVZZT", date(2026, 3, 20), "20", OptionRight.CALL)
    per_contract = PerUnitCommission(Decimal("0.65"))
    assert per_contract.commission(
        Order(instrument=contracts, quantity=4, submitted_at=NOW), money("6.00")
    ) == Decimal("2.60")


def test_a_commission_cap_applies_after_the_floor() -> None:
    schedule = PerUnitCommission(Decimal("0.01"), minimum=Decimal("1.00"), maximum=Decimal("5.00"))

    assert schedule.commission(buy(10), money("50.00")) == Decimal("1.00")
    assert schedule.commission(buy(10_000), money("50.00")) == Decimal("5.00")


def test_a_flat_commission_ignores_size() -> None:
    schedule = PerTradeCommission(Decimal("0.65"))

    assert schedule.commission(buy(1), money("50.00")) == Decimal("0.65")
    assert schedule.commission(buy(10_000), money("50.00")) == Decimal("0.65")


def test_the_costs_describe_themselves_for_the_run_record() -> None:
    # Phase 5's `run` row stores this. A run that cannot say how it was priced
    # is a run that cannot be reproduced.
    described = Costs(
        slippage=BpsSlippage(Decimal("2")), commission=PerUnitCommission(Decimal("0.005"))
    ).describe()

    assert described == {"slippage": "2 bps", "commission": "0.005/unit, min 0"}


def test_an_order_for_zero_quantity_is_refused() -> None:
    with pytest.raises(ValueError, match="not an order"):
        Order(instrument=ZVZZT, quantity=0, submitted_at=NOW)
