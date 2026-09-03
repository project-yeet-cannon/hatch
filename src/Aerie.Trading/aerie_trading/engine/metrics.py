"""Every number a run is judged by, computed in exactly one place.

docs/plans/trading.md Phase 5: *"Metrics per run: total and annualized return,
Sharpe, Sortino, max drawdown and its duration, exposure, turnover, win rate,
trade count. All computed in one place; a metric defined twice will diverge."*
The second sentence is the whole reason this is a module rather than a handful
of properties spread between the engine, the worker and the API. There is one
function, it takes a ``BacktestResult``, and it returns the rows that go into
``run_metric``. Phase 6's honesty layer adds names to this file; Phase 7's
leaderboard reads them out of the table. Neither computes one.

**Decimal throughout, including the square roots.** The engine stops prices
being floats at ``engine/money.py`` and a statistics layer that converted them
back would undo it one ratio at a time - which matters most for the metric
whose inputs are nearly equal, since that is where binary error is largest
relative to the answer. ``Decimal`` has ``sqrt`` and a correctly-rounded
fractional power, so nothing here needs ``math``.

**An undefined metric is absent, never zero and never NaN.** Sharpe over a run
whose equity never moved has a zero denominator; a run over a single bar has no
return series at all. Returning 0 for those would put a number on a question
that has no answer, and a leaderboard would then rank it. ``db/models.RunMetric``
writes out the sorting half of the same argument.

**What is deliberately *not* computed here:** anything needing a second run to
interpret. A baseline comparison, a walk-forward figure, a selection-adjusted
Sharpe and a re-score at a higher cost assumption are all Phase 6, and each of
them is a function of several runs rather than of one. Their *names* are below,
because a metric name has to be one string and every process reads this file;
their arithmetic is in ``honesty/scoring.py``. Everything ``compute_metrics``
emits can be computed from a single ``BacktestResult`` and nothing else, which
is what lets a worker write those rows in the transaction that records the run.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from datetime import datetime
from decimal import Decimal
from itertools import pairwise
from typing import Final

from aerie_trading.engine.backtest import BacktestResult, EquityPoint
from aerie_trading.engine.money import ZERO
from aerie_trading.providers.base import REGULAR_SESSION_MINUTES, Interval

__all__ = [
    "ANNUALIZED_RETURN",
    "BASELINE_RETURN",
    "COMMISSION_PAID",
    "COST_SENSITIVITY",
    "DEFLATED_SHARPE",
    "EXCESS_RETURN",
    "EXCESS_RETURN_INDEX",
    "EXPECTED_MAX_SHARPE",
    "EXPOSURE",
    "FINAL_EQUITY",
    "HONESTY_METRIC_NAMES",
    "INDEX_RETURN",
    "MAX_DRAWDOWN",
    "MAX_DRAWDOWN_DAYS",
    "METRIC_NAMES",
    "SELECTION_TRIALS",
    "SHARPE",
    "SLIPPAGE_PAID",
    "SORTINO",
    "STRESSED_SHARPE",
    "STRESSED_TOTAL_RETURN",
    "TOTAL_RETURN",
    "TRADE_COUNT",
    "TURNOVER",
    "VOLATILITY",
    "WALK_FORWARD_FOLDS",
    "WIN_RATE",
    "compute_metrics",
    "periods_per_year",
]

# The names, as constants. A metric is written by the worker and read by the
# leaderboard in a different process, so a string literal in two places is a
# metric that silently stops being displayed the day one of them is retyped.
TOTAL_RETURN: Final = "total_return"
ANNUALIZED_RETURN: Final = "annualized_return"
FINAL_EQUITY: Final = "final_equity"
VOLATILITY: Final = "volatility"
SHARPE: Final = "sharpe"
SORTINO: Final = "sortino"
MAX_DRAWDOWN: Final = "max_drawdown"
MAX_DRAWDOWN_DAYS: Final = "max_drawdown_days"
EXPOSURE: Final = "exposure"
TURNOVER: Final = "turnover"
WIN_RATE: Final = "win_rate"
TRADE_COUNT: Final = "trade_count"
COMMISSION_PAID: Final = "commission_paid"
SLIPPAGE_PAID: Final = "slippage_paid"

# Phase 6's names, which this file predicted it would carry: *"Phase 6's
# honesty layer adds names to this file."* Declared here and **computed in
# `honesty/scoring.py`**, and the split is the point of both docstrings. A
# metric name is read by a worker that writes it and by a leaderboard in
# another process that sorts on it, so it belongs in the one place every
# consumer already imports; the arithmetic behind these needs a second run - a
# baseline, a stressed re-score, the siblings of a sweep - which is precisely
# what this module refuses to know about.
BASELINE_RETURN: Final = "baseline_return"
INDEX_RETURN: Final = "index_return"
EXCESS_RETURN: Final = "excess_return"
EXCESS_RETURN_INDEX: Final = "excess_return_index"
STRESSED_TOTAL_RETURN: Final = "stressed_total_return"
STRESSED_SHARPE: Final = "stressed_sharpe"
COST_SENSITIVITY: Final = "cost_sensitivity"
SELECTION_TRIALS: Final = "selection_trials"
EXPECTED_MAX_SHARPE: Final = "expected_max_sharpe"
DEFLATED_SHARPE: Final = "deflated_sharpe"
WALK_FORWARD_FOLDS: Final = "walk_forward_folds"

#: Every name this module can emit. Not every run has every one - see the
#: module docstring on absence - so this is the vocabulary rather than a
#: promise about any particular row.
METRIC_NAMES: Final[tuple[str, ...]] = (
    TOTAL_RETURN,
    ANNUALIZED_RETURN,
    FINAL_EQUITY,
    VOLATILITY,
    SHARPE,
    SORTINO,
    MAX_DRAWDOWN,
    MAX_DRAWDOWN_DAYS,
    EXPOSURE,
    TURNOVER,
    WIN_RATE,
    TRADE_COUNT,
    COMMISSION_PAID,
    SLIPPAGE_PAID,
)

#: The names Phase 6 adds. Kept as a second tuple rather than folded into
#: ``METRIC_NAMES`` because the two differ in something a caller cares about:
#: everything above can be computed from one ``BacktestResult``, and nothing
#: below can. ``compute_metrics`` emits the first set and never the second.
HONESTY_METRIC_NAMES: Final[tuple[str, ...]] = (
    BASELINE_RETURN,
    INDEX_RETURN,
    EXCESS_RETURN,
    EXCESS_RETURN_INDEX,
    STRESSED_TOTAL_RETURN,
    STRESSED_SHARPE,
    COST_SENSITIVITY,
    SELECTION_TRIALS,
    EXPECTED_MAX_SHARPE,
    DEFLATED_SHARPE,
    WALK_FORWARD_FOLDS,
)

#: Sessions in a year. The conventional 252 rather than a count off the
#: exchange calendar: this is the constant every published Sharpe is
#: annualized with, and a number that differed by a session or two per year
#: would make this silo's figures incomparable with every other one for no gain
#: in accuracy that anybody could use.
SESSIONS_PER_YEAR: Final = Decimal(252)

#: Days in a year, averaged over the leap cycle. Used for the *calendar* span
#: an annualized return is computed over, which is a different question from
#: how many observations a year holds.
DAYS_PER_YEAR: Final = Decimal("365.25")

#: How many decimal places a metric keeps. A statistic is not money and does
#: not want ``money()``'s two places; twenty-eight significant digits of a
#: Sharpe ratio is noise pretending to be precision. Ten is far past anything a
#: comparison depends on and short enough to read in ``psql``.
_PLACES: Final = Decimal("0.0000000001")


def periods_per_year(interval: Interval) -> Decimal:
    """How many bars of ``interval`` a trading year holds.

    One expression for both cases, because ``Interval.minutes`` already answers
    "a session" for the daily bar: 252 sessions times however many bars fit in
    one. A table keyed by interval would be a second place the daily case could
    disagree with itself.
    """
    return SESSIONS_PER_YEAR * Decimal(REGULAR_SESSION_MINUTES) / Decimal(interval.minutes)


def compute_metrics(result: BacktestResult) -> Mapping[str, Decimal]:
    """Every defined metric for ``result``, keyed by name.

    The mapping is what the worker writes to ``run_metric``, one row per entry.
    A name absent from it is a metric this run does not have; see the module
    docstring.
    """
    metrics: dict[str, Decimal] = {
        TRADE_COUNT: Decimal(result.trades),
        COMMISSION_PAID: result.commissions,
        SLIPPAGE_PAID: result.slippage_cost,
    }
    if not result.curve:
        # A run over no bars. It has a trade count of zero and nothing else,
        # and saying so is better than reporting a flat equity curve - which
        # is what a strategy that traded nothing looks like.
        return {name: _round(value) for name, value in metrics.items()}

    metrics[FINAL_EQUITY] = result.final_equity
    metrics[MAX_DRAWDOWN] = result.max_drawdown
    metrics[MAX_DRAWDOWN_DAYS] = _drawdown_days(result.curve, result.starting_cash)

    if result.starting_cash > 0:
        metrics[TOTAL_RETURN] = result.total_return
        annualized = _annualized(result.total_return, _years(result))
        if annualized is not None:
            metrics[ANNUALIZED_RETURN] = annualized

    exposure = _exposure(result.curve)
    if exposure is not None:
        metrics[EXPOSURE] = exposure

    turnover = _turnover(result)
    if turnover is not None:
        metrics[TURNOVER] = turnover

    win_rate = _win_rate(result)
    if win_rate is not None:
        metrics[WIN_RATE] = win_rate

    returns = _returns(result.curve)
    if len(returns) >= 2:
        scale = periods_per_year(Interval(result.interval)).sqrt()
        mean = _mean(returns)
        deviation = _stdev(returns, mean)
        if deviation > 0:
            metrics[VOLATILITY] = deviation * scale
            metrics[SHARPE] = mean / deviation * scale
        downside = _downside_deviation(returns)
        if downside > 0:
            metrics[SORTINO] = mean / downside * scale

    return {name: _round(value) for name, value in metrics.items()}


# -- the pieces -------------------------------------------------------------


def _returns(curve: Sequence[EquityPoint]) -> tuple[Decimal, ...]:
    """Simple returns between consecutive curve points.

    One fewer than there are points, and the first bar is deliberately not a
    return against starting cash: the engine fills at the *next* bar's open, so
    nothing can have happened by the close of the first one, and a leading zero
    would be an observation of a period that had no opportunity to move.

    A point whose equity is zero or negative ends the series rather than
    producing an infinite return. An account that reached zero has no further
    percentage changes to report, and the run's total return already says what
    happened.
    """
    out: list[Decimal] = []
    for previous, point in pairwise(curve):
        if previous.equity <= 0:
            break
        out.append(point.equity / previous.equity - 1)
    return tuple(out)


def _mean(values: Sequence[Decimal]) -> Decimal:
    return sum(values, start=ZERO) / Decimal(len(values))


def _stdev(values: Sequence[Decimal], mean: Decimal) -> Decimal:
    """Sample standard deviation - the ``n - 1`` denominator.

    Sample rather than population because these returns are a sample of what
    the strategy would do, not the whole of it. It matters least where it is
    most often argued about and most where it is not: over the short windows a
    walk-forward test slices in Phase 6, ``n`` and ``n - 1`` differ by enough
    to move a Sharpe in the third decimal, and the two halves of that
    comparison have to be computed the same way.
    """
    variance = sum(((value - mean) ** 2 for value in values), start=ZERO) / Decimal(len(values) - 1)
    return variance.sqrt()


def _downside_deviation(values: Sequence[Decimal]) -> Decimal:
    """Root-mean-square of the negative returns, over the *full* sample.

    The denominator is every observation rather than only the losing ones,
    which is the standard definition and the one that behaves: dividing by the
    count of down periods would reward a strategy for having few of them twice,
    once in the numerator and once in the denominator, and would make a run
    with one bad day look riskier than one with ten mild ones.

    The minimum acceptable return is zero, so "downside" means "lost money"
    rather than "underperformed a rate this plan has not chosen a source for".
    """
    squares = sum((value**2 for value in values if value < 0), start=ZERO)
    return (squares / Decimal(len(values))).sqrt()


def _years(result: BacktestResult) -> Decimal:
    """The run's calendar span in years, from its first bar to its last."""
    span = result.ended_at - result.started_at
    return Decimal(span.total_seconds()) / Decimal(86400) / DAYS_PER_YEAR


def _annualized(total_return: Decimal, years: Decimal) -> Decimal | None:
    """The constant annual rate that compounds to ``total_return`` over ``years``.

    ``None`` in the two cases that have no answer rather than a bad one: a run
    whose bars share an instant has no elapsed time to spread a return over,
    and an account that finished at or below zero has no rate that reaches it -
    a total return of -100% would need an infinite negative one, and anything
    worse is not a real number at all.
    """
    if years <= 0:
        return None
    growth = 1 + total_return
    if growth <= 0:
        return None
    return growth ** (1 / years) - 1


def _drawdown_days(curve: Sequence[EquityPoint], starting_cash: Decimal) -> Decimal:
    """The longest stretch, in days, between an equity peak and its recovery.

    Measured from the peak rather than from the trough, which is the reading an
    operator means by "how long was I under water": the money was gone from the
    high-water mark onwards, not from the bottom onwards. A drawdown still open
    at the last bar counts to that bar, because a recovery that has not
    happened cannot be assumed.

    The clock starts at the first bar with the account at its starting cash, so
    a run that never once exceeds where it began reports its whole length.
    """
    peak = starting_cash
    peak_at = curve[0].timestamp
    longest = ZERO
    for point in curve:
        if point.equity >= peak:
            peak = point.equity
            peak_at = point.timestamp
            continue
        longest = max(longest, _days_between(peak_at, point.timestamp))
    return longest


def _days_between(start: datetime, end: datetime) -> Decimal:
    return Decimal((end - start).total_seconds()) / Decimal(86400)


def _exposure(curve: Sequence[EquityPoint]) -> Decimal | None:
    """Gross position value over the run, divided by account value over it.

    A value-weighted average rather than a mean of per-bar ratios, which is the
    same number when equity is steady and the better-behaved one when it is
    not: a single bar where the account approached zero would otherwise
    dominate the average with a ratio in the thousands.

    1 is fully invested, 0 is never in the market, and above 1 is leverage -
    which the engine cannot currently produce and which this therefore reports
    rather than clamps, since the day it appears is a day something is wrong.
    """
    total_equity = sum((point.equity for point in curve), start=ZERO)
    if total_equity <= 0:
        return None
    return sum((abs(point.market_value) for point in curve), start=ZERO) / total_equity


def _turnover(result: BacktestResult) -> Decimal | None:
    """Traded notional over the run, divided by the account's average value.

    "How many times the account was turned over", and deliberately **not**
    annualized: annualizing it would fold the run's length into a number whose
    whole use is comparing two strategies over the *same* window, and the
    window is on the run row for anyone who wants the rate.

    Both sides of a round trip count, so a strategy that buys and sells its
    whole account once reports 2.
    """
    average_equity = _mean([point.equity for point in result.curve])
    if average_equity <= 0:
        return None
    traded = sum((abs(fill.notional) for fill in result.fills), start=ZERO)
    return traded / average_equity


def _win_rate(result: BacktestResult) -> Decimal | None:
    """The share of closing fills that closed at a profit.

    A round trip is scored at the fill that ends it, because that is the fill
    the engine attributes realized P&L to. An opening fill realizes nothing and
    is therefore neither a win nor a loss - counting it as a loss would make
    every strategy's win rate at most one half by construction.

    ``None`` when nothing was closed: a run that is still holding everything it
    bought has no completed trades to have won or lost, which is exactly
    ``buy_and_hold``.
    """
    closing = [fill for fill in result.fills if fill.realized_pnl != 0]
    if not closing:
        return None
    wins = sum(1 for fill in closing if fill.realized_pnl > 0)
    return Decimal(wins) / Decimal(len(closing))


def _round(value: Decimal) -> Decimal:
    """Ten decimal places, banker's rounding - the file's one lossy step.

    Applied once, on the way out, rather than between steps: rounding an
    intermediate would put the error inside the arithmetic instead of at the
    boundary where it is a display decision.
    """
    return value.quantize(_PLACES)
