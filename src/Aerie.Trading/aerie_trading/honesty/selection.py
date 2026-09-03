"""What "best of N" is worth, as arithmetic rather than as a caveat.

docs/plans/trading.md Phase 6: *"Selection accounting: a run knows how many
siblings its sweep produced. Report an adjusted figure alongside the raw
Sharpe - a deflated Sharpe or an equivalent multiple-testing correction - so
'best of 10,000' is visibly different from 'best of 3.'"*

**The problem, stated once.** Take a strategy with no edge whatsoever and run
it a thousand times over noise with a thousand different parameter sets. Their
Sharpes scatter around zero, and the best of them is not near zero - it is
near the expected maximum of a thousand draws, which for a five-year daily
backtest is comfortably above 0.5 and looks like a finding. Nothing about the
best run is wrong. It really did post that Sharpe. The mistake is in reading
the maximum of a thousand samples as though it were one sample.

**The correction, and what it deliberately is not.** The figure this computes
is the *haircut* form: the expected best-of-N Sharpe under the null of no skill,
subtracted from the observed one. What remains is how much the result beats
what selection alone would have produced, and it collapses to about zero for
the best of a large sweep over data with no alpha in it - which is Phase 6's
gate, and which is a measurement with a known correct answer because Phase 2's
generator asserts its own zero alpha.

It is **not** the full Bailey/Lopez de Prado deflated Sharpe, and the reason is
that the full form needs the variance of the Sharpes *across* the trials, plus
the skew and kurtosis of the returns. The cross-trial variance is not known to
a worker recording one run - the sibling runs may not have finished, or may
never - and a metric that could only be written after a sweep completed would
be absent on exactly the leaderboard the plan wants it visible on by default.
The null used here (independent trials, iid returns) is the optimistic one on
both counts: correlated parameter sets and fat-tailed returns both make the
expected maximum *larger*, so this haircut understates rather than flatters.
Said plainly: a result that survives this has not been proven; a result that
does not survive it has been disproven cheaply.

**Everything is Decimal**, for the reason ``engine/metrics.py`` gives at
length: the engine stops prices being floats at its boundary and a statistics
layer that put them back would undo it one ratio at a time. The one place that
would otherwise need ``math`` is the inverse normal CDF, which is why one is
implemented here.
"""

from __future__ import annotations

from decimal import Decimal
from typing import Final

__all__ = [
    "expected_max_sharpe",
    "expected_max_standard_normal",
    "haircut_sharpe",
    "inverse_normal_cdf",
]

#: Euler-Mascheroni. It appears in the expected-maximum approximation below
#: because the maximum of n independent draws converges to a Gumbel
#: distribution, whose mean carries this constant.
_GAMMA: Final = Decimal("0.57721566490153286060651209008240243104215933593992")

#: e, to the same precision Decimal's default context carries.
_E: Final = Decimal(1).exp()


def expected_max_standard_normal(trials: int) -> Decimal:
    """The expected largest of ``trials`` independent standard normal draws.

    The standard Gumbel-limit approximation::

        E[max] ~ (1 - gamma) * Z(1 - 1/N) + gamma * Z(1 - 1/(N e))

    accurate to better than a hundredth of a standard deviation for every N
    this is ever handed and exact enough that the third decimal of a Sharpe
    haircut is not where a result's honesty turns.

    One trial is zero, not the expected maximum of one draw. A single draw's
    expectation *is* zero, and returning it says the thing that matters: a run
    that was not selected out of anything gets no haircut at all.
    """
    if trials < 1:
        raise ValueError("a selection is over at least one trial")
    if trials == 1:
        return Decimal(0)
    count = Decimal(trials)
    return (1 - _GAMMA) * inverse_normal_cdf(1 - 1 / count) + _GAMMA * inverse_normal_cdf(
        1 - 1 / (count * _E)
    )


def expected_max_sharpe(trials: int, observations: int, periods_per_year: Decimal) -> Decimal:
    """The annualized Sharpe the best of ``trials`` posts with no skill at all.

    Under the null a per-period Sharpe estimated from ``observations`` returns
    has standard error ``1 / sqrt(n - 1)``; the best of N of them sits
    ``expected_max_standard_normal(N)`` of those errors above zero, and
    annualizing multiplies by the square root of the periods in a year. So the
    whole thing is one expression, and the shape it has is the useful part:
    the bar a result must clear **rises** with the size of the sweep and
    **falls** with the length of the window. A long backtest is worth more than
    a short one by exactly this much.
    """
    if observations < 2:
        raise ValueError("a Sharpe over fewer than two returns has no standard error")
    if periods_per_year <= 0:
        raise ValueError("periods per year must be positive")
    standard_error = (periods_per_year / Decimal(observations - 1)).sqrt()
    return expected_max_standard_normal(trials) * standard_error


def haircut_sharpe(
    sharpe: Decimal, trials: int, observations: int, periods_per_year: Decimal
) -> Decimal:
    """``sharpe`` less what selection over ``trials`` would have produced anyway.

    Can be negative, and is left negative. A best-of-10,000 that lands below
    the null's own expected maximum has done worse than picking at random would
    have, and clamping that to zero would hide the one result whose adjusted
    figure is genuinely informative.
    """
    return sharpe - expected_max_sharpe(trials, observations, periods_per_year)


# -- the inverse normal CDF --------------------------------------------------
#
# Acklam's rational approximation, with a relative error below 1.15e-9 across
# the whole open interval - which is nine digits more than a Sharpe haircut can
# use. Written out here rather than reached for in `statistics` because
# `statistics.NormalDist.inv_cdf` takes and returns floats, and the one number
# this module produces goes straight into a Decimal metric column; the
# conversion would be a binary round trip in the middle of a file whose whole
# argument is not having one.

_A: Final[tuple[Decimal, ...]] = tuple(
    Decimal(value)
    for value in (
        "-3.969683028665376e+01",
        "2.209460984245205e+02",
        "-2.759285104469687e+02",
        "1.383577518672690e+02",
        "-3.066479806614716e+01",
        "2.506628277459239e+00",
    )
)
_B: Final[tuple[Decimal, ...]] = tuple(
    Decimal(value)
    for value in (
        "-5.447609879822406e+01",
        "1.615858368580409e+02",
        "-1.556989798598866e+02",
        "6.680131188771972e+01",
        "-1.328068155288572e+01",
    )
)
_C: Final[tuple[Decimal, ...]] = tuple(
    Decimal(value)
    for value in (
        "-7.784894002430293e-03",
        "-3.223964580411365e-01",
        "-2.400758277161838e+00",
        "-2.549732539343734e+00",
        "4.374664141464968e+00",
        "2.938163982698783e+00",
    )
)
_D: Final[tuple[Decimal, ...]] = tuple(
    Decimal(value)
    for value in (
        "7.784695709041462e-03",
        "3.224671290700398e-01",
        "2.445134137142996e+00",
        "3.754408661907416e+00",
    )
)

#: Where the central rational switches to the tail one. Acklam's own break.
_LOW: Final = Decimal("0.02425")


def inverse_normal_cdf(probability: Decimal) -> Decimal:
    """The standard normal quantile: the ``z`` with ``P(Z <= z) = probability``.

    Refuses 0 and 1 rather than returning an infinity. Both are reachable only
    by a caller that has already made an arithmetic mistake - a trial count of
    zero, a probability built by subtracting a number from itself - and an
    infinity would propagate into a metric column as something Postgres will
    happily store and a leaderboard will happily sort to the top.
    """
    if not 0 < probability < 1:
        raise ValueError(
            f"a normal quantile needs a probability strictly inside (0, 1); got {probability}"
        )

    if probability < _LOW:
        return _tail((-2 * probability.ln()).sqrt())
    if probability > 1 - _LOW:
        return -_tail((-2 * (1 - probability).ln()).sqrt())

    offset = probability - Decimal("0.5")
    square = offset * offset
    numerator = _horner(_A, square) * offset
    denominator = _horner(_B, square) * square + 1
    return numerator / denominator


def _tail(root: Decimal) -> Decimal:
    """Acklam's lower-tail branch, at ``root = sqrt(-2 ln p)``."""
    return _horner(_C, root) / (_horner(_D, root) * root + 1)


def _horner(coefficients: tuple[Decimal, ...], value: Decimal) -> Decimal:
    """``c0 x^n + ... + cn``, evaluated innermost-first.

    Horner's form rather than the powers written out, because a fifth power of
    a Decimal is five multiplications whose intermediate precision the default
    context has to carry, and because the nested-parenthesis version of these
    two polynomials is where a transcription error hides best.
    """
    total = coefficients[0]
    for coefficient in coefficients[1:]:
        total = total * value + coefficient
    return total
