"""The null hypothesis, asserted rather than claimed.

docs/plans/trading.md leans on one property of the synthetic source in two
different places, and neither of them survives if it is only true by intention:

- Phase 2 requires that "generated returns carry no exploitable autocorrelation
  at the sample sizes the plan uses", and says it should be a test rather than
  a claim - *if the generator ever acquires structure, the phase that depends
  on it should break loudly here rather than quietly there.*
- Phase 6's gate is exact rather than impressionistic only because of it. The
  generator has zero alpha by construction, so every result over synthetic data
  is a false positive by definition, and the best of a large sweep over it is
  the strongest false positive the machinery can manufacture. That is a
  measurement with a known correct answer, which the honesty layer could not
  otherwise have had.

So this file is the load-bearing one. The threshold is three standard errors
under the null that the returns are independent - ``3 / sqrt(n)`` - which is
where a lag would have to land to be evidence of structure rather than of
sampling. Everything here is deterministic, so a passing run is a fact about
the generator rather than a lucky draw, and a failing one is a regression
rather than flakiness.
"""

import math
import statistics
from datetime import UTC, datetime

import pytest

from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.config import SyntheticConfig
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider

#: Lags tested. Far enough out to catch a weekly or a fortnightly cycle, which
#: is the shape an accidental structure in a session-indexed walk would take.
LAGS = range(1, 11)


def log_returns(closes: list[float]) -> list[float]:
    return [math.log(closes[i] / closes[i - 1]) for i in range(1, len(closes))]


def autocorrelation(series: list[float], lag: int) -> float:
    mean = statistics.fmean(series)
    variance = sum((value - mean) ** 2 for value in series)
    covariance = sum(
        (series[index] - mean) * (series[index + lag] - mean) for index in range(len(series) - lag)
    )
    return covariance / variance


def daily_closes(provider: SyntheticMarketDataProvider, symbol: str) -> list[float]:
    """Every daily close the calendar can produce, which is the longest sample."""
    calendar = provider.calendar
    bars = provider.bars(
        [symbol],
        Interval.ONE_DAY,
        calendar.first_session.open,
        calendar.last_session.close,
    )
    return [bar.close for bar in bars]


@pytest.mark.parametrize("seed", [20260902, 1, 99])
def test_daily_returns_carry_no_exploitable_autocorrelation(seed: int) -> None:
    # Three seeds rather than one: the property has to be about the generator
    # rather than about the universe the default seed happens to produce.
    provider = SyntheticMarketDataProvider(SyntheticConfig(seed=seed))

    for symbol in provider.config.symbols:
        returns = log_returns(daily_closes(provider, symbol))
        assert len(returns) > 2_000
        threshold = 3.0 / math.sqrt(len(returns))
        for lag in LAGS:
            measured = autocorrelation(returns, lag)
            assert abs(measured) < threshold, (
                f"seed {seed}, {symbol}: lag-{lag} autocorrelation {measured:.4f}"
                f" exceeds {threshold:.4f}. The generator has acquired structure,"
                " and every result measured against it as a null hypothesis is"
                " now suspect - see docs/plans/trading.md Phase 6."
            )


def test_intraday_returns_carry_no_exploitable_autocorrelation() -> None:
    # The intraday walk is a separate process from the daily trunk (see
    # `providers/synthetic/prices.py`), so the property has to be asserted of
    # it separately. This is also the test that would fail if the intraday path
    # were ever bridged onto the session's closing price: a bridge's increments
    # are negatively correlated because their sum is constrained.
    provider = SyntheticMarketDataProvider()
    bars = provider.bars(
        ["ZVZZT"],
        Interval.ONE_MINUTE,
        datetime(2026, 6, 1, tzinfo=UTC),
        datetime(2026, 9, 1, tzinfo=UTC),
    )
    returns = log_returns([bar.close for bar in bars])

    assert len(returns) > 20_000
    threshold = 3.0 / math.sqrt(len(returns))
    for lag in LAGS:
        assert abs(autocorrelation(returns, lag)) < threshold


def test_the_drift_is_zero_by_default() -> None:
    # Not a placeholder. A non-zero drift would give a buy-and-hold an edge to
    # find, and Phase 6 would be measuring its overfitting gate against a
    # moving target.
    config = SyntheticConfig()
    assert config.annual_drift == 0.0
    assert all(entry.annual_drift is None for entry in config.universe)


def test_the_mean_daily_return_is_indistinguishable_from_zero() -> None:
    # The companion to the autocorrelation checks: no trend, as well as no
    # memory. Three standard errors again, on the same null.
    provider = SyntheticMarketDataProvider()
    returns = log_returns(daily_closes(provider, "ZVZZT"))

    standard_error = statistics.stdev(returns) / math.sqrt(len(returns))
    assert abs(statistics.fmean(returns)) < 3.0 * standard_error
