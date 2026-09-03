"""The multiple-testing correction, against numbers with known answers.

Two kinds of assertion here, and the second is the one that matters. The first
checks the arithmetic against published values - a normal quantile is a table
lookup and the expected maximum of N draws is a known series. The second checks
the *shape*: the haircut has to grow with the size of the sweep and shrink with
the length of the backtest, because those two behaviours are the entire content
of the correction. An implementation that got the constants right and the
monotonicity wrong would pass a table check and be useless.
"""

import statistics
from decimal import Decimal

import pytest

from aerie_trading.honesty.selection import (
    expected_max_sharpe,
    expected_max_standard_normal,
    haircut_sharpe,
    inverse_normal_cdf,
)

#: Daily bars, so 252 observations a year. The scale a Sharpe is annualized on.
DAILY = Decimal(252)


@pytest.mark.parametrize("probability", [0.001, 0.01, 0.1, 0.25, 0.5, 0.75, 0.9, 0.99, 0.999])
def test_the_quantile_matches_the_standard_normal(probability: float) -> None:
    # Acklam's approximation claims a relative error below 1.15e-9; a tenth of
    # a millionth is far looser than that and far tighter than any use here.
    expected = statistics.NormalDist().inv_cdf(probability)
    assert float(inverse_normal_cdf(Decimal(str(probability)))) == pytest.approx(expected, abs=1e-7)


def test_the_quantile_refuses_the_two_probabilities_that_have_no_answer() -> None:
    for impossible in (Decimal(0), Decimal(1)):
        with pytest.raises(ValueError, match="strictly inside"):
            inverse_normal_cdf(impossible)


def test_one_trial_earns_no_haircut() -> None:
    # A run that was not selected out of anything is not a best-of-anything.
    assert expected_max_standard_normal(1) == 0
    assert expected_max_sharpe(1, 1000, DAILY) == 0


@pytest.mark.parametrize(
    ("trials", "expected"),
    # Textbook values for the expected maximum of n iid standard normals.
    [(10, 1.54), (100, 2.51), (1_000, 3.24), (10_000, 3.85)],
)
def test_the_expected_maximum_matches_the_known_series(trials: int, expected: float) -> None:
    assert float(expected_max_standard_normal(trials)) == pytest.approx(expected, abs=0.05)


def test_the_bar_rises_with_the_size_of_the_sweep() -> None:
    bars = [float(expected_max_sharpe(trials, 1_260, DAILY)) for trials in (1, 10, 1_000, 10_000)]

    assert bars == sorted(bars)
    assert bars[0] == 0


def test_the_bar_falls_with_the_length_of_the_backtest() -> None:
    # The same sweep judged over five years faces a lower bar than over one,
    # which is the correction saying that a longer record is worth more.
    one_year = expected_max_sharpe(1_000, 252, DAILY)
    five_years = expected_max_sharpe(1_000, 1_260, DAILY)

    assert five_years < one_year


def test_the_best_of_a_large_sweep_over_noise_deflates_to_about_nothing() -> None:
    # The gate's arithmetic in isolation: a strategy with no edge, swept ten
    # thousand ways over five years of daily bars, posts about this Sharpe by
    # selection alone. Subtracting it leaves nothing, which is the correct
    # answer and the one the leaderboard has to reach.
    trials, observations = 10_000, 1_260
    luckiest = expected_max_sharpe(trials, observations, DAILY)

    assert float(luckiest) == pytest.approx(1.72, abs=0.05)
    assert haircut_sharpe(luckiest, trials, observations, DAILY) == 0


def test_a_haircut_can_go_negative_and_is_left_there() -> None:
    # Below what picking at random would have produced. Clamping it to zero
    # would hide the one adjusted figure that is genuinely informative.
    assert haircut_sharpe(Decimal("0.1"), 10_000, 1_260, DAILY) < 0
