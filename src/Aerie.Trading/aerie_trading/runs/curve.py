"""The equity curve, on its way from a result into a row.

docs/plans/trading.md Phase 7 wants an equity curve on the run detail. The
engine produces one point per bar and a run is unbounded in bars, so the
question this module answers is not *how* to store a curve but *how much* of
one to store.

**A cap, applied at write time, rather than a decimation at read time.** The
alternative - store every point, sample when the chart asks - keeps the exact
series, and the cost of it is a table whose size is set by whichever interval
somebody sweeps at: five years of one-minute bars is about half a million
points *per run*, and a thousand-run sweep of those is half a billion rows in a
database whose whole design argument (``db/__init__.py``) is that the large
data lives in the Lake instead. Sampling on the way in makes a run's curve a
bounded object, which is what lets the run detail be a single-row read.

**What that costs, stated plainly:** a spike between two kept points is not
kept. That is why ``RunCurve.sampled`` and ``points_total`` exist - a chart
drawn from a sampled curve can say so - and why the cap is high enough that
every daily-bar run this silo has produced falls under it and is exact. A
deep-dive at full resolution is Grafana's job, which is the plan's own
division of labour.

**Deterministic, and asserted to be.** Two runs of the same result produce the
same sampled curve, because the indices are computed from the length rather
than accumulated by stepping. That matters for the same reason
``result_fingerprint`` does: a curve that differed between two identical runs
would be a difference nobody could explain from the inputs.
"""

from __future__ import annotations

from collections.abc import Sequence
from typing import Final

from aerie_trading.engine.backtest import EquityPoint

__all__ = ["CURVE_POINTS", "SampledCurve", "sample_curve"]

#: The most points one stored curve may hold.
#:
#: Two thousand, chosen against both ends of the question rather than as a
#: round number. Above it: a 1,250-bar daily run over five years - the demo,
#: and every run the plan's own gates measure - is stored exactly, so the
#: common case is not an approximation. Below it: a chart of two thousand
#: points is already more than a 1,200-pixel-wide plot can resolve, so the
#: first point sampling discards is one the screen was going to drop anyway.
CURVE_POINTS: Final = 2_000


class SampledCurve:
    """A curve reduced to at most ``cap`` points, and the record of the reduction.

    Not a dataclass, and not frozen for the sake of it: this is what
    ``RunQueue.succeed`` writes and what a test asserts against, so it carries
    exactly the three things ``run_curve`` stores and nothing that would have
    to be kept in step with them.
    """

    __slots__ = ("points", "points_total", "sampled")

    def __init__(self, points: list[list[str]], points_total: int, sampled: bool) -> None:
        self.points = points
        self.points_total = points_total
        self.sampled = sampled

    def __repr__(self) -> str:  # pragma: no cover - diagnostics
        return (
            f"SampledCurve(points={len(self.points)},"
            f" points_total={self.points_total}, sampled={self.sampled})"
        )


def sample_curve(curve: Sequence[EquityPoint], cap: int = CURVE_POINTS) -> SampledCurve:
    """``curve`` as at most ``cap`` ``[timestamp, equity]`` pairs, oldest first.

    Both ends are always kept. A curve is read as "what did this run start at
    and end at, and what happened in between", and a sampling scheme that could
    drop the last point would be one where the chart's final value disagrees
    with the ``final_equity`` metric sitting next to it.

    Timestamps are ISO 8601 with an offset - the engine's instants are
    timezone-aware, so this is a lossless rendering - and equity is the
    ``Decimal`` as text. See ``db/models.RunCurve`` on why neither is a JSON
    number.
    """
    if cap < 2:
        raise ValueError("a curve needs room for at least its two ends")

    total = len(curve)
    if total <= cap:
        return SampledCurve([_point(entry) for entry in curve], total, sampled=False)

    # Indices spread evenly across the closed interval [0, total - 1], computed
    # from the position rather than by adding a step, so rounding cannot drift
    # and the last index is exactly the last point. `dict.fromkeys` rather than
    # a set: two positions can round to the same index, and the order has to
    # survive the de-duplication.
    last = total - 1
    indices = dict.fromkeys(round(step * last / (cap - 1)) for step in range(cap))
    return SampledCurve([_point(curve[index]) for index in indices], total, sampled=True)


def _point(entry: EquityPoint) -> list[str]:
    """One curve point, as the two strings that are stored.

    A list rather than a tuple because this is JSON on the way to JSONB, and a
    tuple would be rendered as a list anyway - by a serializer, silently, at a
    point further from the schema than here.
    """
    return [entry.timestamp.isoformat(), str(entry.equity)]
