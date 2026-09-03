"""Parameters, the space a sweep walks, the registry, and the context's helpers.

Phase 5 is the phase that walks a parameter space and Phase 7 is the one that
renders it, so the assertions here are mostly about what those two will read:
that a range declared with ``swept()`` is enforced *and* readable back as data,
that the two declarations cannot disagree, and that the registry answers the
same way from every process that asks it.

The context tests cover the one helper with a bug in it if it is written by
hand - ``order_to_target`` netting against the orders already queued. A
strategy that re-evaluates its target every bar and reads only the settled
position doubles its size on the bar after it signals, and the resulting equity
curve looks like a strategy with conviction.
"""

from decimal import Decimal

import pytest
from pydantic import ValidationError

from aerie_trading.engine.backtest import run_backtest
from aerie_trading.engine.broker import ZERO_COSTS, SimBroker
from aerie_trading.engine.history import BarHistory
from aerie_trading.engine.instruments import equity
from aerie_trading.engine.portfolio import Portfolio
from aerie_trading.engine.strategy import (
    Params,
    Strategy,
    StrategyContext,
    SweepRange,
    swept,
)
from aerie_trading.strategies import REGISTRY, SPECS, spec_for
from aerie_trading.strategies.ma_crossover import MaCrossoverParams
from tests.conftest import flat_bars

CLOSES = [10.0, 11.0, 12.0, 13.0, 14.0]


class Tunable(Params):
    window: int = swept(10, low=2, high=20, step=2, description="A window.")
    threshold: Decimal = swept(Decimal("0.5"), low="0.1", high="0.4", step="0.1")
    symbol: str = "ZVZZT"


# -- parameters --------------------------------------------------------------


def test_a_declared_range_is_enforced_on_construction() -> None:
    assert Tunable(window=4).window == 4
    with pytest.raises(ValidationError):
        Tunable(window=100)


def test_a_declared_range_is_also_readable_as_data() -> None:
    # Both halves, because they are what stop the range and the sweep from
    # disagreeing: the same numbers enforce the constraint and describe the
    # grid.
    space = Tunable.sweep_space()

    assert set(space) == {"window", "threshold"}
    assert space["window"] == SweepRange(
        low=Decimal("2"), high=Decimal("20"), step=Decimal("2"), description="A window."
    )
    assert space["window"].values() == tuple(Decimal(value) for value in range(2, 21, 2))
    assert space["window"].count == 10


def test_a_parameter_without_a_range_is_not_swept() -> None:
    # `symbol` is configuration for a run rather than a dimension to search. A
    # sweep that walked it would be searching over which market to trade.
    assert "symbol" not in Tunable.sweep_space()


def test_a_decimal_range_does_not_drift() -> None:
    # 0.1 + 0.1 + 0.1 in binary is 0.30000000000000004, which would be a
    # distinct param_set row from 0.3 - so two sweeps of one declared range
    # would produce different sets of them.
    assert Tunable.sweep_space()["threshold"].values() == (
        Decimal("0.1"),
        Decimal("0.2"),
        Decimal("0.3"),
        Decimal("0.4"),
    )


def test_the_grid_size_is_the_product_of_the_ranges() -> None:
    # Phase 7 shows this as "an estimated run count" before launching a sweep.
    assert Tunable.combinations() == 10 * 4


def test_parameters_are_frozen_and_reject_a_typo() -> None:
    params = Tunable()

    with pytest.raises(ValidationError):
        params.window = 4
    # A misspelled parameter must fail rather than silently use the default: a
    # sweep of a misspelled name is ten thousand identical runs wearing ten
    # thousand different labels.
    # Spelled through `model_validate` because that is how Phase 5 builds a
    # strategy - from a param_set's JSONB blob, which the type checker cannot
    # see a typo in and this model can.
    with pytest.raises(ValidationError):
        Tunable.model_validate({"windwo": 4})


# -- the registry ------------------------------------------------------------


def test_the_registry_holds_both_shipped_strategies() -> None:
    assert set(REGISTRY) == {"buy_and_hold", "ma_crossover"}
    assert tuple(spec.name for spec in SPECS) == ("buy_and_hold", "ma_crossover")


def test_every_registered_spec_builds_something_that_is_a_strategy() -> None:
    for spec in SPECS:
        built = spec.default()
        assert isinstance(built, Strategy)
        assert isinstance(built.params, spec.params_model)


def test_the_two_shipped_strategies_bracket_the_parameter_cases() -> None:
    # One empty space and one real grid, which is what makes every consumer -
    # the sweep launcher, the leaderboard, the UI's parameter panel - exercised
    # against both from the day it is written.
    assert spec_for("buy_and_hold").sweep_space() == {}
    assert set(spec_for("ma_crossover").sweep_space()) == {"fast", "slow"}


def test_an_unknown_strategy_names_what_the_build_does_ship() -> None:
    with pytest.raises(LookupError, match="buy_and_hold"):
        spec_for("mean_reversion")


def test_the_crossover_refuses_a_fast_window_at_or_above_its_slow_one() -> None:
    # In the model, so a sweep's invalid corner is rejected when the param_set
    # is built rather than after a worker has produced a run whose two averages
    # are the same series.
    assert MaCrossoverParams(fast=5, slow=20).fast == 5
    with pytest.raises(ValidationError, match="must be below"):
        MaCrossoverParams(fast=20, slow=20)


# -- the context -------------------------------------------------------------


def context(index: int = 0, cash: str = "10000.00") -> tuple[StrategyContext, SimBroker]:
    history = BarHistory.from_bars(flat_bars("ZVZZT", CLOSES))
    portfolio = Portfolio(cash)
    broker = SimBroker(portfolio, ZERO_COSTS)
    return (
        StrategyContext(history.timeline[index], index, history, portfolio, broker),
        broker,
    )


def test_the_context_reports_where_it_is_in_the_run() -> None:
    first, _ = context(0)
    last, _ = context(len(CLOSES) - 1)

    assert first.is_first_bar
    assert not first.is_last_bar
    assert first.bar_number == 1
    assert last.is_last_bar
    assert last.bar_number == len(CLOSES)
    assert first.symbols == ("ZVZZT",)


def test_a_holding_counts_orders_that_are_queued_but_not_yet_filled() -> None:
    ctx, _ = context()

    ctx.order(equity("ZVZZT"), 10)

    # Zero settled, ten committed. Reading the settled position alone is the
    # bug this method exists to prevent.
    assert ctx.portfolio.quantity_of(equity("ZVZZT")) == 0
    assert ctx.quantity_of(equity("ZVZZT")) == 10


def test_ordering_to_a_target_does_not_double_up_across_bars() -> None:
    ctx, broker = context()

    ctx.order_to_target(equity("ZVZZT"), 10)
    ctx.order_to_target(equity("ZVZZT"), 10)

    assert len(broker.pending) == 1
    assert broker.pending[0].quantity == 10


def test_ordering_to_the_target_already_held_is_not_an_order() -> None:
    ctx, broker = context()
    ctx.order_to_target(equity("ZVZZT"), 10)

    assert ctx.order_to_target(equity("ZVZZT"), 10) is None
    assert ctx.order(equity("ZVZZT"), 0) is None
    assert len(broker.pending) == 1


def test_chains_are_refused_rather_than_faked() -> None:
    # Declared in Phase 4 because the two-clock design names it; implemented in
    # Phase 10, which is the phase that also brings the pricing guardrail.
    ctx, _ = context()

    with pytest.raises(NotImplementedError, match="Phase 10"):
        ctx.chain("ZVZZT")


def test_a_strategy_reads_a_portfolio_that_is_already_marked() -> None:
    # Step 2 before step 3 in the loop. A strategy reading a portfolio marked
    # to yesterday's close would size its orders off a stale price.
    seen: list[Decimal] = []

    class Recorder:
        @property
        def params(self) -> Params:
            return Params()

        def on_bar(self, ctx: StrategyContext) -> None:
            seen.append(ctx.equity)

    history = BarHistory.from_bars(flat_bars("ZVZZT", CLOSES))
    run_backtest(Recorder(), history, starting_cash="10000.00", costs=ZERO_COSTS)

    assert seen == [Decimal("10000.00")] * len(CLOSES)
