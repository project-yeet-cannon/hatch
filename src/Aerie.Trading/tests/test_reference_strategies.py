"""What the two shipped strategies are expected to do, asserted rather than hoped.

docs/plans/trading.md Phase 4 asks for this before the strategies run:
*"Against a zero-drift random walk, ``ma_crossover`` should lose to
``buy_and_hold`` after costs - it trades, trading costs money, and there is no
signal to pay for it."*

**Running it showed the prediction is right about the mechanism and wrong about
the measurement, and this file asserts the version that is true.** The
head-to-head difference between the two strategies on any one path is dominated
by something much larger than costs: the crossover is long about half the time
and the baseline is long all of it, so the gap between them is mostly the
market's own realized move over that window, which under a driftless walk is a
coin flip with a standard deviation of tens of percent. Measured over twenty
seeds of the default universe, the *gross* difference between them has a mean
of about -7% against a standard error of about 8% - which is to say it is
indistinguishable from zero, exactly as the plan predicts - and the crossover
wins outright on roughly half of the individual paths. A build gate that
asserted "the crossover loses" on one path would be asserting the sign of a
coin flip, and would fail for a reason that has nothing to do with the engine.

So the claim is decomposed into the two parts of it that are load-bearing, and
each is asserted in the form that is actually true:

**There is no gross edge to pay for.** Before costs, the crossover's advantage
over the baseline is statistically zero across seeds. This is the half that
would break if the generator ever acquired structure, and it is the same three-
standard-errors convention ``test_synthetic_zero_alpha.py`` uses.

**Costs are paid, always, in the trader's direction.** On every seed, every
window and every parameter set, running the crossover with costs leaves it
strictly further behind the baseline than running it without them - because it
trades a hundred and sixty times to the baseline's five. This is the half that
breaks if the fill model, the slippage model or the commission model is wrong,
which is what the plan actually wants the gate to catch, and unlike the
head-to-head it has no variance in it at all.

**And the shipped demo is asserted as itself.** Phase 7 seeds the leaderboard
from one configuration, and on that configuration the crossover does finish
behind. That is asserted here so the seeded front page cannot quietly stop
being what the plan says it is - with the caveat, stated in the test, that it
is one path and that ``DEMO_WINDOW`` is what makes it reproducible rather than
what makes it true.
"""

import math
import statistics
from collections.abc import Mapping
from datetime import UTC, datetime
from functools import cache

import pytest

from aerie_trading.engine.backtest import BacktestResult, compare, run_backtest
from aerie_trading.engine.broker import ZERO_COSTS
from aerie_trading.engine.history import BarHistory
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.config import SyntheticConfig
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.strategies import spec_for

#: The window Phase 7's seed job should use, named here so that the assertion
#: about the shipped leaderboard and the leaderboard itself cannot drift apart.
#: Five years of daily bars over the default synthetic universe.
DEMO_WINDOW = (datetime(2021, 1, 1, tzinfo=UTC), datetime(2026, 1, 1, tzinfo=UTC))

#: Seeds for the population claim. Twelve rather than two, because the quantity
#: being measured has a standard deviation of tens of percent and two draws
#: from it say nothing; twelve is enough for three standard errors to be a
#: meaningful bound and few enough that the suite stays quick.
SEEDS = tuple(range(1, 13))


@cache
def history(seed: int | None = None) -> BarHistory:
    config = SyntheticConfig() if seed is None else SyntheticConfig(seed=seed)
    provider = SyntheticMarketDataProvider(config)
    bars = provider.bars(list(provider.config.symbols), Interval.ONE_DAY, *DEMO_WINDOW)
    return BarHistory.from_bars(list(bars))


@cache
def both(seed: int | None, priced: bool) -> Mapping[str, BacktestResult]:
    """Both reference strategies over one seed's bars, with or without costs.

    Cached by its arguments, which is safe for exactly the reason Phase 4
    asserts separately: a run is a pure function of its inputs, so two tests
    asking for the same one must be given the same answer. Without the cache
    this module generates the same five years of bars a dozen times over.
    """
    bars = history(seed)
    return {
        name: run_backtest(
            spec_for(name).build({}),
            bars,
            costs=None if priced else ZERO_COSTS,
            name=name,
        )
        for name in ("buy_and_hold", "ma_crossover")
    }


@pytest.fixture(scope="module")
def default_runs() -> Mapping[str, BacktestResult]:
    return both(None, priced=True)


def test_both_reference_strategies_run_over_the_full_demo_window(
    default_runs: Mapping[str, BacktestResult],
) -> None:
    for name, result in default_runs.items():
        assert result.strategy == name
        assert result.bars > 1_200
        assert result.started_at >= DEMO_WINDOW[0]
        assert result.ended_at < DEMO_WINDOW[1]
        assert result.curve[0].equity == result.starting_cash
        # Every symbol in the universe was traded, not just the first one -
        # both strategies are written against the run's universe rather than
        # against a configured symbol, and a regression to single-symbol would
        # show up here rather than as a quietly smaller number.
        assert {fill.instrument.symbol for fill in result.fills} == set(result.symbols)


def test_the_crossover_trades_far_more_than_the_baseline(
    default_runs: Mapping[str, BacktestResult],
) -> None:
    baseline = default_runs["buy_and_hold"]
    crossover = default_runs["ma_crossover"]

    # The baseline buys once per symbol and never sells; the crossover round-
    # trips. The ratio is the whole reason the two cost different amounts, and
    # asserting it here means a strategy change that quietly stopped trading
    # cannot make the cost assertions below pass by trading nothing.
    assert baseline.trades == len(baseline.symbols)
    assert crossover.trades > 20 * baseline.trades
    assert crossover.slippage_cost > 20 * baseline.slippage_cost


@pytest.mark.parametrize("seed", SEEDS)
def test_costs_always_push_the_crossover_further_behind_the_baseline(seed: int) -> None:
    # The gate the plan actually wants, in the form that has no variance in it.
    # Whatever the market did on this path, pricing the run can only move the
    # crossover down relative to the baseline, because it pays the costs many
    # more times. A cost model with an inverted sign, a fill model that filled
    # on the signal bar, or a portfolio that credited commissions would all
    # show up here, on every seed at once.
    free = both(seed, priced=False)
    priced = both(seed, priced=True)

    gross_edge = free["ma_crossover"].total_return - free["buy_and_hold"].total_return
    net_edge = priced["ma_crossover"].total_return - priced["buy_and_hold"].total_return

    assert net_edge < gross_edge, f"seed {seed}: costs did not cost the trader anything"
    # And by a margin that is the crossover's costs rather than a rounding
    # artefact: about half a percent of the account over five years.
    assert gross_edge - net_edge > 0.001


def test_the_crossover_has_no_gross_edge_over_the_baseline_to_pay_costs_with() -> None:
    # The null hypothesis the generator exists to provide, measured on the
    # thing that would exploit it. Three standard errors, the same convention
    # as tests/test_synthetic_zero_alpha.py. A failure here means the walk has
    # acquired structure the crossover can find - which would make every
    # Phase 6 result measured against this generator suspect.
    edges = [
        float(runs["ma_crossover"].total_return - runs["buy_and_hold"].total_return)
        for runs in (both(seed, priced=False) for seed in SEEDS)
    ]

    mean = statistics.fmean(edges)
    standard_error = statistics.stdev(edges) / math.sqrt(len(edges))
    assert abs(mean) < 3.0 * standard_error, (
        f"the crossover's gross edge over buy-and-hold averages {mean:.2%} across"
        f" {len(edges)} seeds, more than three standard errors ({standard_error:.2%})"
        " from zero. The synthetic generator is no longer a null hypothesis - see"
        " docs/plans/trading.md Phase 6."
    )


def test_the_seeded_demo_shows_the_crossover_behind_the_baseline(
    default_runs: Mapping[str, BacktestResult],
) -> None:
    # The plan's front-page claim, asserted on the one configuration Phase 7
    # seeds. It is a statement about this path rather than about crossovers in
    # general - see this module's docstring - and it is worth asserting anyway,
    # because the shipped leaderboard is the first thing an operator reads and
    # the plan says what it should say.
    leaderboard = compare(list(default_runs.values()))

    assert [result.strategy for result in leaderboard] == ["buy_and_hold", "ma_crossover"]
    assert default_runs["ma_crossover"].total_return < default_runs["buy_and_hold"].total_return


def test_neither_strategy_claims_to_be_a_candidate() -> None:
    # The plan: "the plan says so in the strategy's own description field so
    # that the UI says so too." Asserted rather than trusted, because the
    # description is the only place the disclaimer exists and a rewrite that
    # dropped it would be invisible until it was on a page.
    for name in ("buy_and_hold", "ma_crossover"):
        assert "Not a candidate for making money" in spec_for(name).description
