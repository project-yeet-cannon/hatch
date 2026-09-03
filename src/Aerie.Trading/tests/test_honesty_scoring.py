"""The metrics that sit beside a result, over bars built by hand.

No lake and no database: a baseline, a stressed re-score and a haircut are
arithmetic over ``BacktestResult`` objects, and a fixture whose expected answer
has to be derived from the same generator it is checking is not a fixture.

The one thing worth stating about the fixtures below: the "strategy" being
scored is ``buy_and_hold`` in most of them, which makes it *identical* to its
own baseline. That is deliberate. An excess return of exactly zero is the one
value that proves the baseline was computed over the same window with the same
money rather than over something adjacent, and it is a value a bug cannot
produce by luck.
"""

from decimal import Decimal

from aerie_trading.engine.backtest import run_backtest
from aerie_trading.engine.history import BarHistory
from aerie_trading.engine.metrics import (
    BASELINE_RETURN,
    COST_SENSITIVITY,
    DEFLATED_SHARPE,
    EXCESS_RETURN,
    EXCESS_RETURN_INDEX,
    EXPECTED_MAX_SHARPE,
    INDEX_RETURN,
    SELECTION_TRIALS,
    STRESSED_TOTAL_RETURN,
    TOTAL_RETURN,
    WALK_FORWARD_FOLDS,
    compute_metrics,
)
from aerie_trading.honesty.scoring import HonestyInputs, score, stress
from aerie_trading.runs.costs import CostSpec
from aerie_trading.strategies import spec_for
from tests.conftest import flat_bars

RISING = [100.0 + step for step in range(40)]
FLAT = [50.0] * 40

#: No slippage and no commission. Used where the *point* of the fixture is a
#: number that should come out exactly - an unchanged account, a zero excess -
#: because the default cost model charges two basis points on the entry and
#: turns every such number into "almost".
FREE = CostSpec(slippage_bps=Decimal(0))


def result_over(closes: list[float], costs: CostSpec | None = None, symbol: str = "ZVZZT"):
    history = BarHistory.from_bars(flat_bars(symbol, closes))
    return run_backtest(
        spec_for("buy_and_hold").default(),
        history,
        starting_cash=Decimal(100_000),
        costs=(costs or CostSpec()).build(),
        name="buy_and_hold",
    )


# -- the cost model ----------------------------------------------------------


def test_stressing_a_cost_model_scales_every_rate() -> None:
    stressed = stress(
        CostSpec(
            slippage_bps=Decimal(2),
            commission_per_unit=Decimal("0.005"),
            commission_minimum=Decimal(1),
            commission_maximum=Decimal(10),
        ),
        Decimal(5),
    )

    assert stressed.slippage_bps == Decimal(10)
    assert stressed.commission_per_unit == Decimal("0.025")
    assert stressed.commission_minimum == Decimal(5)
    # The cap scales too. A cap that stayed put while the rate rose would
    # become the dominant term and understate the stress.
    assert stressed.commission_maximum == Decimal(50)


def test_stressing_a_model_with_no_cap_leaves_it_uncapped() -> None:
    assert stress(CostSpec(), Decimal(5)).commission_maximum is None


# -- the comparisons ---------------------------------------------------------


def test_a_run_measured_against_itself_has_no_excess() -> None:
    result = result_over(RISING)

    metrics = score(result, HonestyInputs(baseline=result, index=result))

    assert metrics[BASELINE_RETURN] == compute_metrics(result)[TOTAL_RETURN]
    assert metrics[EXCESS_RETURN] == 0
    assert metrics[EXCESS_RETURN_INDEX] == 0


def test_a_run_that_beat_its_baseline_says_so_in_one_number() -> None:
    strategy = result_over(RISING, FREE)
    baseline = result_over(FLAT, FREE)

    metrics = score(strategy, HonestyInputs(baseline=baseline))

    # Bought at 50 and never moved: exactly no return, and the strategy's whole
    # gain is therefore exactly its excess.
    assert metrics[BASELINE_RETURN] == 0
    assert metrics[EXCESS_RETURN] == compute_metrics(strategy)[TOTAL_RETURN]
    assert metrics[EXCESS_RETURN] > 0


def test_a_missing_baseline_is_absent_rather_than_zero() -> None:
    # A lake with no bars for the broad universe must not turn into an index
    # return of zero, which would sort in the middle of a leaderboard as
    # though it were a measurement.
    metrics = score(result_over(RISING), HonestyInputs())

    assert BASELINE_RETURN not in metrics
    assert INDEX_RETURN not in metrics
    assert EXCESS_RETURN not in metrics


# -- cost sensitivity --------------------------------------------------------


def test_higher_costs_are_reported_as_the_difference_they_made() -> None:
    free = result_over(RISING, FREE)
    expensive = result_over(RISING, CostSpec(slippage_bps=Decimal(500)))

    metrics = score(free, HonestyInputs(stressed=expensive))
    unstressed = compute_metrics(free)[TOTAL_RETURN]

    # `score` returns the honesty layer's names and not the engine's, so the
    # unstressed figure is read off the result rather than out of the mapping.
    # That is the split `engine/metrics.py` describes, visible here.
    assert TOTAL_RETURN not in metrics
    assert metrics[STRESSED_TOTAL_RETURN] < unstressed
    assert metrics[COST_SENSITIVITY] > 0
    assert metrics[COST_SENSITIVITY] == unstressed - metrics[STRESSED_TOTAL_RETURN]


def test_no_stressed_run_means_no_cost_sensitivity() -> None:
    metrics = score(result_over(RISING), HonestyInputs())

    assert COST_SENSITIVITY not in metrics
    assert STRESSED_TOTAL_RETURN not in metrics


# -- selection accounting ----------------------------------------------------


def test_every_run_records_how_many_siblings_it_had() -> None:
    assert score(result_over(RISING), HonestyInputs(trials=57))[SELECTION_TRIALS] == 57
    # A run launched on its own is one trial, not zero: it was not selected out
    # of anything, and the haircut for a single draw is zero rather than
    # undefined.
    assert score(result_over(RISING), HonestyInputs())[SELECTION_TRIALS] == 1


def test_the_bar_a_result_must_clear_rises_with_the_sweep_that_produced_it() -> None:
    result = result_over(RISING)

    lonely = score(result, HonestyInputs(trials=1))
    crowded = score(result, HonestyInputs(trials=10_000))

    assert lonely[EXPECTED_MAX_SHARPE] == 0
    assert crowded[EXPECTED_MAX_SHARPE] > lonely[EXPECTED_MAX_SHARPE]
    # The same run, deflated by more because more was searched to find it.
    assert crowded[DEFLATED_SHARPE] < lonely[DEFLATED_SHARPE]


def test_a_run_with_no_sharpe_gets_no_deflated_one() -> None:
    # A flat account has no variance, so it has no Sharpe and therefore nothing
    # to deflate. Reporting a haircut against a missing figure would invent one.
    # Priced free, because two basis points on the entry is itself a move.
    metrics = score(result_over(FLAT, FREE), HonestyInputs(trials=100))

    assert DEFLATED_SHARPE not in metrics
    assert EXPECTED_MAX_SHARPE not in metrics


def test_a_walk_forward_records_how_many_folds_produced_it() -> None:
    metrics = score(result_over(RISING), HonestyInputs(folds=5))

    assert metrics[WALK_FORWARD_FOLDS] == 5
    # And an ordinary run does not claim to be one.
    assert WALK_FORWARD_FOLDS not in score(result_over(RISING), HonestyInputs())
