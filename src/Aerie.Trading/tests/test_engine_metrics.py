"""Phase 5's third gate: *"metrics for a hand-checked run match a hand-computed
answer"*.

Every expected value below is written out as arithmetic in a comment, the way
``tests/test_engine_fixture.py`` writes out the P&L it asserts, and for the same
reason: a fixture whose expected values were produced by the code under test can
be silently rewritten to match a regression. These were computed from four
equity values and two fills, by hand.

Two shapes of test, because the metrics divide cleanly into two kinds. The
curve-only ones - return, volatility, Sharpe, Sortino, drawdown, exposure - are
asserted against a ``BacktestResult`` built by hand, so the numbers going in are
the numbers a person would check. The fill-based ones - turnover, win rate,
trade count - need a run, so they are asserted against the engine driving a
scripted strategy over flat prices, where every fill price is a round number
that appears in the fixture.
"""

from collections.abc import Mapping
from datetime import UTC, datetime, timedelta
from decimal import Decimal

import pytest

from aerie_trading.engine import metrics as m
from aerie_trading.engine.backtest import BacktestResult, EquityPoint, run_backtest
from aerie_trading.engine.broker import ZERO_COSTS
from aerie_trading.engine.history import BarHistory
from aerie_trading.engine.metrics import compute_metrics, periods_per_year
from aerie_trading.providers.base import Interval
from tests.conftest import FIXTURE_START, ScriptedStrategy, flat_bars

# -- the hand-built curve ---------------------------------------------------
#
# Four daily points, starting at 100,000:
#
#     day 0   100,000     fully in cash
#     day 1   110,000     +10%
#     day 2    99,000     -10%
#     day 3   108,900     +10%
#
# so the return series is exactly (+0.1, -0.1, +0.1), which is what makes every
# statistic below checkable without a spreadsheet.

_EQUITIES = (Decimal(100_000), Decimal(110_000), Decimal(99_000), Decimal(108_900))
_MARKET_VALUES = (Decimal(0), Decimal(110_000), Decimal(99_000), Decimal(108_900))


def hand_built_result() -> BacktestResult:
    curve = tuple(
        EquityPoint(
            timestamp=FIXTURE_START + timedelta(days=offset),
            cash=equity - market_value,
            market_value=market_value,
            realized_pnl=Decimal(0),
            unrealized_pnl=Decimal(0),
        )
        for offset, (equity, market_value) in enumerate(zip(_EQUITIES, _MARKET_VALUES, strict=True))
    )
    return BacktestResult(
        strategy="hand_built",
        params={},
        symbols=("ZVZZT",),
        interval=Interval.ONE_DAY.value,
        started_at=curve[0].timestamp,
        ended_at=curve[-1].timestamp,
        bars=len(curve),
        starting_cash=_EQUITIES[0],
        curve=curve,
        fills=(),
        expired=(),
        costs={},
    )


@pytest.fixture(scope="module")
def hand_built() -> Mapping[str, Decimal]:
    return compute_metrics(hand_built_result())


def test_total_return_is_the_last_equity_over_the_first(
    hand_built: Mapping[str, Decimal],
) -> None:
    # 108,900 / 100,000 - 1
    assert hand_built[m.TOTAL_RETURN] == Decimal("0.0890000000")
    assert hand_built[m.FINAL_EQUITY] == Decimal("108900.0000000000")


def test_volatility_and_sharpe_annualize_the_return_series(
    hand_built: Mapping[str, Decimal],
) -> None:
    # mean(+0.1, -0.1, +0.1)                        = 0.0333333333
    # sample stdev, n - 1 = 2                       = 0.1154700538
    # sqrt(252)                                     = 15.8745078664
    # volatility = 0.1154700538 * sqrt(252)         = 1.8330302780
    # sharpe     = 0.0333333333 / 0.1154700538 * sqrt(252)
    assert hand_built[m.VOLATILITY] == Decimal("1.8330302780")
    assert hand_built[m.SHARPE] == Decimal("4.5825756950")


def test_sortino_divides_by_the_downside_only(hand_built: Mapping[str, Decimal]) -> None:
    # One negative return of -0.1, and the denominator is the *full* sample:
    # sqrt(0.01 / 3) = 0.0577350269, which is smaller than the two-sided
    # deviation, so Sortino exceeds Sharpe here - as it must for a series whose
    # losses are fewer than its gains.
    assert hand_built[m.SORTINO] == Decimal("9.1651513899")
    assert hand_built[m.SORTINO] > hand_built[m.SHARPE]


def test_max_drawdown_and_its_duration_run_from_the_peak(
    hand_built: Mapping[str, Decimal],
) -> None:
    # Peak 110,000 on day 1, trough 99,000 on day 2: (110000 - 99000) / 110000.
    assert hand_built[m.MAX_DRAWDOWN] == Decimal("0.1000000000")
    # Under water from day 1 to day 3, and never recovered to 110,000 - so the
    # duration runs to the last bar rather than stopping at the trough.
    assert hand_built[m.MAX_DRAWDOWN_DAYS] == Decimal("2.0000000000")


def test_exposure_is_position_value_over_account_value(
    hand_built: Mapping[str, Decimal],
) -> None:
    # (0 + 110000 + 99000 + 108900) / (100000 + 110000 + 99000 + 108900)
    #   = 317900 / 417900
    assert hand_built[m.EXPOSURE] == Decimal("0.7607083034")


def test_a_run_with_no_fills_reports_no_turnover_and_no_win_rate(
    hand_built: Mapping[str, Decimal],
) -> None:
    assert hand_built[m.TRADE_COUNT] == 0
    # Turnover is defined (it is zero) and win rate is not: nothing was traded,
    # so there is a traded notional to report and no completed trade to have
    # won or lost. The asymmetry is the point - see the module docstring in
    # engine/metrics.py on absence.
    assert hand_built[m.TURNOVER] == 0
    assert m.WIN_RATE not in hand_built


def test_annualizing_compounds_over_the_calendar_span() -> None:
    # +21% over exactly two years is 10% a year, because 1.1 * 1.1 = 1.21.
    # Two points 730.5 days apart, which is 2 * 365.25.
    start = datetime(2024, 1, 1, tzinfo=UTC)
    curve = (
        EquityPoint(start, Decimal(100_000), Decimal(0), Decimal(0), Decimal(0)),
        EquityPoint(
            start + timedelta(days=730, hours=12),
            Decimal(121_000),
            Decimal(0),
            Decimal(0),
            Decimal(0),
        ),
    )
    result = BacktestResult(
        strategy="hand_built",
        params={},
        symbols=("ZVZZT",),
        interval=Interval.ONE_DAY.value,
        started_at=curve[0].timestamp,
        ended_at=curve[-1].timestamp,
        bars=2,
        starting_cash=Decimal(100_000),
        curve=curve,
        fills=(),
        expired=(),
        costs={},
    )

    computed = compute_metrics(result)
    assert computed[m.TOTAL_RETURN] == Decimal("0.2100000000")
    assert computed[m.ANNUALIZED_RETURN] == Decimal("0.1000000000")


# -- the fill-based metrics, driven through the engine ----------------------


@pytest.fixture(scope="module")
def scripted() -> Mapping[str, Decimal]:
    """Buy 200 at 10, sell 200 at 15, over five flat daily bars.

    Flat bars so the fill price and the marked price are the same number, and
    ``ZERO_COSTS`` so that turnover is traded notional rather than traded
    notional plus a slippage nobody is asserting here. The engine fills at the
    *next* bar's open, so the order placed on bar 1 fills on bar 2.
    """
    history = BarHistory.from_bars(flat_bars("ZVZZT", [10, 10, 15, 15, 15]))
    result = run_backtest(
        ScriptedStrategy({1: 200, 3: -200}), history, starting_cash=100_000, costs=ZERO_COSTS
    )
    return compute_metrics(result)


def test_trade_count_and_win_rate_score_the_closing_fill(
    scripted: Mapping[str, Decimal],
) -> None:
    assert scripted[m.TRADE_COUNT] == 2
    # Two fills, one round trip. The buy realizes nothing and is neither a win
    # nor a loss; the sell realizes 200 * (15 - 10) = 1,000 and is a win.
    assert scripted[m.WIN_RATE] == 1


def test_turnover_is_traded_notional_over_average_equity(
    scripted: Mapping[str, Decimal],
) -> None:
    # Traded notional  = 200 * 10 + 200 * 15                  =   5,000
    # Equity per bar   = 100000, 100000, 101000, 101000, 101000
    # Average equity   = 503,000 / 5                          = 100,600
    assert scripted[m.TURNOVER] == Decimal("0.0497017893")


def test_exposure_over_the_scripted_run(scripted: Mapping[str, Decimal]) -> None:
    # Position value per bar = 0, 2000, 3000, 0, 0            =   5,000
    # Account value per bar sums to                           = 503,000
    assert scripted[m.EXPOSURE] == Decimal("0.0099403579")


def test_a_free_run_pays_nothing_and_says_so(scripted: Mapping[str, Decimal]) -> None:
    assert scripted[m.COMMISSION_PAID] == 0
    assert scripted[m.SLIPPAGE_PAID] == 0
    assert scripted[m.TOTAL_RETURN] == Decimal("0.0100000000")
    # Equity never fell below a previous peak.
    assert scripted[m.MAX_DRAWDOWN] == 0
    assert scripted[m.MAX_DRAWDOWN_DAYS] == 0


# -- the undefined cases ----------------------------------------------------


def test_a_motionless_account_has_no_sharpe_rather_than_a_zero_one() -> None:
    # A strategy that never traded has a zero standard deviation, and every
    # risk-adjusted ratio over it divides by zero. Reporting 0 would rank it
    # beside a strategy that earned nothing at some risk; reporting nothing
    # says the question does not apply. NUMERIC would happily store NaN and
    # Postgres sorts NaN *above* every number, which is the failure this
    # prevents on a leaderboard.
    history = BarHistory.from_bars(flat_bars("ZVZZT", [10, 10, 10, 10]))
    computed = compute_metrics(run_backtest(ScriptedStrategy({}), history, costs=ZERO_COSTS))

    assert computed[m.TOTAL_RETURN] == 0
    assert m.VOLATILITY not in computed
    assert m.SHARPE not in computed
    assert m.SORTINO not in computed


def test_a_position_still_open_at_the_end_has_no_win_rate() -> None:
    # buy_and_hold's shape: one fill, nothing closed, nothing realized. A win
    # rate of 0 would read as "every trade lost".
    history = BarHistory.from_bars(flat_bars("ZVZZT", [10, 10, 12, 12]))
    computed = compute_metrics(run_backtest(ScriptedStrategy({1: 100}), history, costs=ZERO_COSTS))

    assert computed[m.TRADE_COUNT] == 1
    assert m.WIN_RATE not in computed


def test_every_emitted_name_is_one_the_module_declares() -> None:
    # METRIC_NAMES is what a UI builds a column list from, so a metric emitted
    # under a name absent from it is one that silently never appears.
    assert set(compute_metrics(hand_built_result())) <= set(m.METRIC_NAMES)


@pytest.mark.parametrize(
    ("interval", "expected"),
    [
        (Interval.ONE_DAY, Decimal(252)),
        (Interval.THIRTY_MINUTE, Decimal(252) * 13),
        (Interval.ONE_MINUTE, Decimal(252) * 390),
    ],
)
def test_periods_per_year_counts_bars_in_a_session(interval: Interval, expected: Decimal) -> None:
    # 252 sessions, times however many bars of this size fit in a 390-minute
    # one. The daily case falls out of the same expression because
    # Interval.minutes answers "a session" for it.
    assert periods_per_year(interval) == expected
