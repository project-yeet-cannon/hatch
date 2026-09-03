"""The serializer's refusal, exercised at the model rather than at a route.

docs/plans/trading.md Phase 6: *"The API refuses to serve an in-sample-only
figure in a field labeled as performance. The guard is in the serializer, not
in a convention, and not in the UI."* If that is true, it is assertable with no
API in the test - which is what these do. A guard that could only be shown to
work by driving an endpoint would be a guard living in the endpoint.
"""

from datetime import UTC, datetime
from decimal import Decimal

import pytest

from aerie_trading.engine.metrics import (
    MAX_DRAWDOWN,
    SHARPE,
    TOTAL_RETURN,
    TRADE_COUNT,
)
from aerie_trading.honesty.presentation import (
    Figures,
    InSampleFigure,
    Sample,
    figures_for,
)

OOS = (datetime(2024, 1, 1, tzinfo=UTC), datetime(2026, 1, 1, tzinfo=UTC))
METRICS = {
    TOTAL_RETURN: Decimal("0.42"),
    SHARPE: Decimal("1.9"),
    MAX_DRAWDOWN: Decimal("0.17"),
    TRADE_COUNT: Decimal(38),
}


def test_a_run_with_no_held_out_window_has_no_headline() -> None:
    figures = figures_for(METRICS, None, None)

    assert figures.sample is Sample.IN_SAMPLE
    assert figures.headline == {}
    assert figures.in_sample == {TOTAL_RETURN: Decimal("0.42"), SHARPE: Decimal("1.9")}


def test_a_run_with_a_held_out_window_has_one() -> None:
    figures = figures_for(METRICS, *OOS)

    assert figures.sample is Sample.OUT_OF_SAMPLE
    assert figures.headline == {TOTAL_RETURN: Decimal("0.42"), SHARPE: Decimal("1.9")}
    assert figures.in_sample == {}


def test_descriptive_figures_are_served_either_way() -> None:
    # A drawdown and a trade count describe what a run *did*. Withholding them
    # from an in-sample run would push a caller to read the raw metric rows,
    # which is the guard being routed around by the person it protects.
    for window in ((None, None), OOS):
        figures = figures_for(METRICS, *window)
        assert figures.descriptive == {
            MAX_DRAWDOWN: Decimal("0.17"),
            TRADE_COUNT: Decimal(38),
        }


def test_putting_a_fitted_figure_in_the_headline_is_refused() -> None:
    # The refusal, reached directly rather than through the factory - because
    # this is what a route that assembled a Figures by hand would do.
    with pytest.raises(InSampleFigure, match="cannot be served as performance"):
        Figures(sample=Sample.IN_SAMPLE, headline={SHARPE: Decimal("1.9")})


def test_the_refusal_names_the_figure_it_refused() -> None:
    with pytest.raises(InSampleFigure) as refusal:
        Figures(sample=Sample.IN_SAMPLE, headline=dict(METRICS))

    assert refusal.value.names == tuple(sorted(METRICS))


def test_an_out_of_sample_run_cannot_also_report_in_sample_figures() -> None:
    with pytest.raises(ValueError, match="no in-sample figures"):
        Figures(
            sample=Sample.OUT_OF_SAMPLE,
            out_of_sample_start=OOS[0],
            out_of_sample_end=OOS[1],
            in_sample={SHARPE: Decimal("1.9")},
        )


def test_an_out_of_sample_run_must_say_which_window_was_held_out() -> None:
    with pytest.raises(ValueError, match="must say which window"):
        Figures(sample=Sample.OUT_OF_SAMPLE, headline={SHARPE: Decimal("1.9")})


def test_half_a_window_is_refused() -> None:
    with pytest.raises(ValueError, match="both ends or neither"):
        Figures(sample=Sample.IN_SAMPLE, out_of_sample_start=OOS[0])
