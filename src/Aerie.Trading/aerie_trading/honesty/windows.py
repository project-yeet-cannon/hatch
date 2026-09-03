"""Rolling train/test folds, and the in-sample/out-of-sample split they imply.

docs/plans/trading.md Phase 6: *"Walk-forward: rolling train/test windows,
parameters chosen on each train window, results stitched from the test windows
only."* This module is the "rolling train/test windows" clause on its own, kept
apart from the evaluation that walks them so that the arithmetic can be
asserted without running a single backtest.

**Rolling, not anchored.** An anchored walk-forward grows its train window
forever, so the last fold chooses parameters on almost the whole history and
the first chooses them on a sliver; the folds are then not comparable with each
other, which is most of what a fold is for. A fixed-length train window that
slides forward gives every fold the same amount of evidence.

**The geometry, and why it is stated as segments.** The window is cut into
``folds + train_multiple`` equal segments. Fold *k* trains on segments
``[k, k + train_multiple)`` and tests on segment ``k + train_multiple``. So the
test windows are contiguous, non-overlapping, and together they cover the tail
of the window exactly once - which is what makes carrying one account through
them (``walkforward.py``) a continuous equity curve rather than a stitching
convention somebody has to be told about.

Expressed in *time* rather than in bars, deliberately. A fold boundary that
depended on the bar count would move when a symbol was added to the universe or
when a session was back-filled, and two walk-forwards over "the same window"
would then not have run over the same folds. Sessions are not evenly spaced and
these segments are, so the folds hold slightly different bar counts; that is
the honest cost of a boundary that is a date.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from datetime import datetime, timedelta

__all__ = ["Fold", "OutOfSample", "folds_over"]


@dataclass(frozen=True, slots=True)
class Fold:
    """One train window, and the test window immediately after it."""

    index: int
    train_start: datetime
    train_end: datetime
    test_start: datetime
    test_end: datetime

    def describe(self) -> str:
        return (
            f"fold {self.index}: train {self.train_start.date()}..{self.train_end.date()},"
            f" test {self.test_start.date()}..{self.test_end.date()}"
        )


@dataclass(frozen=True, slots=True)
class OutOfSample:
    """The span a set of folds is entitled to be quoted over.

    The union of the test windows, which is contiguous by construction. It is a
    type rather than a tuple because it is what lands in ``run.oos_start`` and
    ``run.oos_end``, and those two columns are the entire difference between a
    figure the API will serve as performance and one it refuses to
    (``presentation.py``).
    """

    start: datetime
    end: datetime


def folds_over(
    start: datetime,
    end: datetime,
    folds: int,
    train_multiple: int = 3,
) -> tuple[Fold, ...]:
    """Cut ``[start, end)`` into ``folds`` rolling train/test pairs.

    Raises rather than returning fewer folds than asked for. A caller that
    requested five and silently received two would compare a two-fold number
    with a five-fold one on the same leaderboard, and nothing on the row would
    say which it was looking at.
    """
    if folds < 2:
        raise ValueError("a walk-forward needs at least two folds to be a walk-forward")
    if train_multiple < 1:
        raise ValueError("a train window must hold at least one test window's worth of history")
    if end <= start:
        raise ValueError("a walk-forward window must end after it starts")

    segments = folds + train_multiple
    span = end - start
    # Integer microseconds rather than float seconds: a boundary computed in
    # floating point lands a microsecond either side of itself depending on the
    # window, and the two folds it separates would then disagree about which
    # of them owns a bar sitting exactly on it.
    step = timedelta(microseconds=span // timedelta(microseconds=1) // segments)
    if step <= timedelta(0):
        raise ValueError(
            f"[{start.isoformat()}, {end.isoformat()}) is too short to cut into {segments} segments"
        )

    edges: Sequence[datetime] = [start + step * index for index in range(segments)] + [end]
    return tuple(
        Fold(
            index=index,
            train_start=edges[index],
            train_end=edges[index + train_multiple],
            test_start=edges[index + train_multiple],
            test_end=edges[index + train_multiple + 1],
        )
        for index in range(folds)
    )


def out_of_sample(folds: Sequence[Fold]) -> OutOfSample:
    """The span the given folds tested over. Raises on an empty sequence."""
    if not folds:
        raise ValueError("no folds, so nothing was tested out of sample")
    return OutOfSample(start=folds[0].test_start, end=folds[-1].test_end)
