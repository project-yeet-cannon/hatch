"""The determinism gate: identical inputs produce identical bytes.

docs/plans/trading.md Phase 4: *"the same inputs and seed produce
byte-identical results. Asserted in a test, because a non-reproducible backtest
cannot be debugged and a comparison between two of them means nothing."*

Both halves are asserted here. The first is the obvious one - run it twice, get
the same bytes. The second is the one that makes the first worth having: the
fingerprint has to *change* when an input changes, or "identical" is being
satisfied by a hash that is not looking at anything.

The third test is about a subtler source of non-determinism than randomness:
iteration order. A strategy holding four symbols submits four orders per
rebalance, and if the order they are submitted in depends on a set's hash or a
dictionary's insertion history, the fills land in a different sequence, the
cash is quantized in a different order, and two runs of the same code differ in
the last cent. The engine sorts its universe and its legs for exactly this
reason, and this is where that is checked rather than assumed.
"""

import hashlib
from datetime import UTC, datetime
from decimal import Decimal

from aerie_trading.engine.backtest import run_backtest
from aerie_trading.engine.broker import ZERO_COSTS, BpsSlippage, Costs, NoCommission
from aerie_trading.engine.history import BarHistory
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.config import SyntheticConfig
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.strategies import REGISTRY, spec_for
from tests.conftest import ScriptedStrategy, daily_bars, flat_bars

WINDOW = (datetime(2024, 1, 1, tzinfo=UTC), datetime(2026, 1, 1, tzinfo=UTC))


def synthetic_history(seed: int = 20260902) -> BarHistory:
    provider = SyntheticMarketDataProvider(SyntheticConfig(seed=seed))
    bars = provider.bars(list(provider.config.symbols), Interval.ONE_DAY, *WINDOW)
    return BarHistory.from_bars(list(bars))


def test_two_runs_of_the_same_inputs_are_byte_identical() -> None:
    history = synthetic_history()

    for name, spec in REGISTRY.items():
        # A fresh strategy instance each time, because a strategy carries state
        # across bars (ma_crossover remembers which side it was on) and reusing
        # one would make the second run start where the first ended - which is
        # a different input, honestly reported as a different result.
        first = run_backtest(spec.build({}), history, name=name)
        second = run_backtest(spec.build({}), history, name=name)

        assert first.canonical() == second.canonical()
        assert first.fingerprint() == second.fingerprint()


def test_a_fingerprint_changes_when_any_input_changes() -> None:
    history = synthetic_history()
    spec = spec_for("ma_crossover")
    baseline = run_backtest(spec.build({}), history, name="ma_crossover").fingerprint()

    # A parameter.
    assert run_backtest(spec.build({"fast": 11}), history, name="ma_crossover").fingerprint() != (
        baseline
    )
    # The cost model, at the same parameters over the same data.
    assert (
        run_backtest(
            spec.build({}),
            history,
            costs=Costs(slippage=BpsSlippage(Decimal("25")), commission=NoCommission()),
            name="ma_crossover",
        ).fingerprint()
        != baseline
    )
    # The starting cash, which changes position sizes and therefore every fill.
    assert (
        run_backtest(
            spec.build({}), history, starting_cash=250_000, name="ma_crossover"
        ).fingerprint()
        != baseline
    )
    # The data. A different seed is a different market.
    assert (
        run_backtest(spec.build({}), synthetic_history(seed=7), name="ma_crossover").fingerprint()
        != baseline
    )


def test_the_result_is_stable_across_the_order_bars_arrive_in() -> None:
    # Two histories built from the same bars in different orders. The lake
    # returns rows ordered by timestamp then symbol, but a caller concatenating
    # two reads, or a future partition-parallel loader, would not - and the
    # result must not depend on it.
    bars = [
        *flat_bars("ZVZZT", [10.0, 11.0, 12.0, 13.0]),
        *flat_bars("ZWZZT", [20.0, 19.0, 21.0, 22.0]),
        *flat_bars("ZXZZT", [5.0, 6.0, 5.5, 7.0]),
    ]
    forward = BarHistory.from_bars(bars)
    shuffled = BarHistory.from_bars(list(reversed(bars)))

    spec = spec_for("buy_and_hold")
    assert (
        run_backtest(spec.build({}), forward, name="buy_and_hold").canonical()
        == run_backtest(spec.build({}), shuffled, name="buy_and_hold").canonical()
    )


def test_the_fingerprint_survives_a_round_trip_through_its_own_bytes() -> None:
    # The canonical form is what Phase 5's `run` row will store beside the
    # result, so it has to be bytes a later process can hash to the same value
    # rather than a repr that happens to compare equal in one interpreter.
    result = run_backtest(
        ScriptedStrategy({1: 10, 3: -10}),
        BarHistory.from_bars(daily_bars("ZVZZT", [(10.0, 10.0, 10.0, 10.0)] * 4)),
        costs=ZERO_COSTS,
        name="scripted",
    )
    assert result.fingerprint() == hashlib.sha256(result.canonical()).hexdigest()
    assert len(result.fingerprint()) == 64
