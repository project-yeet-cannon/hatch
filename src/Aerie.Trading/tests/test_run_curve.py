"""The equity curve on its way into a row, and the bound that keeps it small.

The claims worth asserting are the ones a chart would otherwise make silently:
that a curve small enough to store exactly *is* stored exactly, that a large
one keeps both of its ends, that the reduction is deterministic, and that no
value passes through a float on the way to JSONB.
"""

from datetime import UTC, datetime, timedelta
from decimal import Decimal

import pytest

from aerie_trading.engine.backtest import EquityPoint
from aerie_trading.runs.curve import CURVE_POINTS, sample_curve

START = datetime(2026, 1, 5, 21, 0, tzinfo=UTC)


def curve(count: int, first: Decimal = Decimal("100000.01")) -> list[EquityPoint]:
    """``count`` points, one a day, each a cent above the last.

    A cent rather than a round number on purpose: the value is what tells a
    float round trip apart from an exact one.
    """
    return [
        EquityPoint(
            timestamp=START + timedelta(days=offset),
            cash=first + Decimal(offset) / 100,
            market_value=Decimal(0),
            realized_pnl=Decimal(0),
            unrealized_pnl=Decimal(0),
        )
        for offset in range(count)
    ]


def test_a_curve_under_the_cap_is_stored_exactly() -> None:
    # The common case, and the reason the cap is where it is: a five-year
    # daily run is about 1,250 points, so the demo's own curves are not
    # approximations of themselves.
    sampled = sample_curve(curve(1_250))

    assert sampled.sampled is False
    assert sampled.points_total == 1_250
    assert len(sampled.points) == 1_250


def test_a_long_curve_is_reduced_and_says_so() -> None:
    # Five years of one-minute bars is the case this bound exists for: without
    # it, one run's curve is half a million rows in a database whose whole
    # design argument is that the large data lives in the Lake.
    sampled = sample_curve(curve(500_000))

    assert sampled.sampled is True
    assert sampled.points_total == 500_000
    assert len(sampled.points) <= CURVE_POINTS


def test_both_ends_survive_the_reduction() -> None:
    # A chart whose last point is not the run's last point disagrees with the
    # final_equity metric printed beside it, which is the one way this
    # reduction could produce a visible lie rather than a lower resolution.
    points = curve(100_000)

    sampled = sample_curve(points)

    assert sampled.points[0] == [points[0].timestamp.isoformat(), str(points[0].equity)]
    assert sampled.points[-1] == [points[-1].timestamp.isoformat(), str(points[-1].equity)]


def test_the_reduction_is_deterministic() -> None:
    # The same argument result_fingerprint makes: two identical runs that
    # produced different curves would be a difference nobody could explain
    # from the inputs.
    points = curve(37_000)

    assert sample_curve(points).points == sample_curve(points).points


def test_values_are_text_rather_than_json_numbers() -> None:
    # JSON has one numeric type and it is a double. An equity of 100000.01
    # written as a number comes back as a float, and the run detail would
    # quietly disagree with the NUMERIC stored beside it.
    sampled = sample_curve(curve(3))

    assert all(isinstance(value, str) for point in sampled.points for value in point)
    assert sampled.points[0][1] == "100000.01"


def test_an_empty_curve_is_an_empty_curve() -> None:
    # A run that produced no bars is a run with nothing to plot, and that is a
    # row rather than an absent one: "the curve is empty" and "the curve was
    # never recorded" are different things on the run detail.
    sampled = sample_curve([])

    assert sampled.points == []
    assert sampled.points_total == 0
    assert sampled.sampled is False


def test_a_cap_with_no_room_for_two_ends_is_refused() -> None:
    with pytest.raises(ValueError, match="two ends"):
        sample_curve(curve(10), cap=1)
