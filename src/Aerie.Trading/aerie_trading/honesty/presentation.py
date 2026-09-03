"""The guard: a figure that was fitted cannot be served as performance.

docs/plans/trading.md Phase 6: *"The API **refuses** to serve an in-sample-only
figure in a field labeled as performance. The guard is in the serializer, not
in a convention, and not in the UI."*

Three words in that bullet decide the whole design.

**Refuses**, not "omits" and not "annotates". Building a ``Figures`` whose
headline block holds anything at all, for a run with no out-of-sample window,
raises. There is no argument that turns the check off, no flag on the model and
no branch that renders it anyway with a warning attached, because every one of
those is a thing a caller in a hurry sets.

**In the serializer.** Not in the route, which is one of several and grows; not
in a helper the routes are supposed to call, which is a convention; and not in
the UI, which is a second codebase that can be rebuilt without this one. The
model is the only way a number leaves this process, so the model is where the
refusal has to live for the refusal to be structural.

**A field labeled as performance**, which is a smaller set than "every number".
A drawdown, an exposure, a turnover and a trade count are descriptions of what
a run *did*; quoting them from an in-sample window is honest, and a guard that
withheld them would push the UI toward reading the raw metric rows to get them
back - which is the guard being routed around by the person it exists to
protect. ``PERFORMANCE_METRICS`` is the list, and the argument for each
membership is that its sign or size is a claim about how *well* the strategy
did rather than about how it behaved.

**Where a headline number legitimately comes from.** A run whose
``out_of_sample`` window is set: the walk-forward runs Phase 6 enqueues beside
every sweep, whose whole window is test data by construction. An ordinary sweep
run has no out-of-sample window and therefore no headline - which is not a
defect in the run, it is what the run is. The plan says so directly: *"A run
with no out-of-sample window is a valid object that can never be a headline
number."*
"""

from __future__ import annotations

import enum
from collections.abc import Mapping
from datetime import datetime
from decimal import Decimal
from typing import Final

from pydantic import BaseModel, ConfigDict, model_validator

from aerie_trading.engine.metrics import (
    ANNUALIZED_RETURN,
    DEFLATED_SHARPE,
    EXCESS_RETURN,
    EXCESS_RETURN_INDEX,
    FINAL_EQUITY,
    SHARPE,
    SORTINO,
    STRESSED_SHARPE,
    STRESSED_TOTAL_RETURN,
    TOTAL_RETURN,
    WIN_RATE,
)

__all__ = [
    "PERFORMANCE_METRICS",
    "Figures",
    "InSampleFigure",
    "Sample",
    "figures_for",
]

#: The names whose value is a claim about how well a strategy did. Everything
#: else a run records is descriptive and is served regardless of window - see
#: the module docstring on why that set is deliberately not "all of them".
PERFORMANCE_METRICS: Final[frozenset[str]] = frozenset(
    {
        TOTAL_RETURN,
        ANNUALIZED_RETURN,
        FINAL_EQUITY,
        SHARPE,
        SORTINO,
        DEFLATED_SHARPE,
        EXCESS_RETURN,
        EXCESS_RETURN_INDEX,
        STRESSED_TOTAL_RETURN,
        STRESSED_SHARPE,
        WIN_RATE,
    }
)


class Sample(enum.StrEnum):
    """Whether a run's window was fitted on or held out.

    Two values rather than three. A run that is partly both is served as
    ``in_sample``, because a figure computed over a window that includes the
    data its parameters were chosen on is an in-sample figure no matter what
    fraction of it was held out - and a third value would be an invitation to
    treat "mostly out of sample" as a headline.
    """

    IN_SAMPLE = "in_sample"
    OUT_OF_SAMPLE = "out_of_sample"


class InSampleFigure(Exception):
    """Raised when a fitted figure was put in a field labelled as performance.

    **Not a ``ValueError``, and that is the one subtle thing in this file.**
    pydantic catches ``ValueError`` and ``AssertionError`` out of a validator
    and folds them into a ``ValidationError`` alongside every ordinary "this
    field is the wrong type" complaint. That is right for the ordinary
    complaints and wrong for this one: the refusal is the guard the plan asked
    for, and a caller that wrapped a serializer in ``except ValidationError``
    to return a tidy 422 would have turned it off without noticing. Deriving
    from ``Exception`` makes it propagate out of the model unchanged, so
    switching it off has to be done on purpose and by name.
    """

    def __init__(self, names: tuple[str, ...]) -> None:
        super().__init__(
            f"{', '.join(names)} cannot be served as performance:"
            " this run has no out-of-sample window, so the figure describes data"
            " its parameters were chosen on. Serve it under `in_sample` instead,"
            " or sort the leaderboard on a walk-forward run."
        )
        self.names = names


class Figures(BaseModel):
    """One run's numbers, split by what they may be called.

    ``headline`` is what a UI may label "performance" and sort a leaderboard
    on. ``in_sample`` is the same kind of figure over a window that was fitted;
    it is served, because withholding it would hide the divergence between the
    two that Phase 6's gate is about, but it is served under a name that says
    what it is. ``descriptive`` is everything whose honesty does not depend on
    the window.
    """

    model_config = ConfigDict(frozen=True, extra="forbid")

    sample: Sample
    out_of_sample_start: datetime | None = None
    out_of_sample_end: datetime | None = None
    headline: Mapping[str, Decimal] = {}
    in_sample: Mapping[str, Decimal] = {}
    descriptive: Mapping[str, Decimal] = {}

    @model_validator(mode="after")
    def _refuse_a_fitted_headline(self) -> Figures:
        if self.sample is Sample.IN_SAMPLE and self.headline:
            raise InSampleFigure(tuple(sorted(self.headline)))
        if self.sample is Sample.OUT_OF_SAMPLE and self.in_sample:
            # The mirror of the same rule, and it catches the more likely
            # mistake: a caller that filled both buckets from the same mapping
            # would put every figure in front of the reader twice, once under a
            # label that says it was fitted when it was not.
            raise ValueError(
                "an out-of-sample run has no in-sample figures to report;"
                " its whole window was held out"
            )
        if (self.out_of_sample_start is None) != (self.out_of_sample_end is None):
            raise ValueError("an out-of-sample window needs both ends or neither")
        if self.sample is Sample.OUT_OF_SAMPLE and self.out_of_sample_start is None:
            raise ValueError("an out-of-sample run must say which window was held out")
        return self


def figures_for(
    metrics: Mapping[str, Decimal],
    out_of_sample_start: datetime | None,
    out_of_sample_end: datetime | None,
) -> Figures:
    """Sort ``metrics`` into the three buckets, from the run's own window.

    The **only** constructor any serving code should use, and the reason
    ``Figures`` can afford to be strict: nothing here decides whether a run is
    in-sample, it reads it off the two columns the run recorded. A route that
    wanted to present an in-sample run as out-of-sample would have to fabricate
    a window on the row, which is a lie in the database rather than one in a
    response body.
    """
    held_out = out_of_sample_start is not None and out_of_sample_end is not None
    sample = Sample.OUT_OF_SAMPLE if held_out else Sample.IN_SAMPLE

    performance = {name: value for name, value in metrics.items() if name in PERFORMANCE_METRICS}
    descriptive = {
        name: value for name, value in metrics.items() if name not in PERFORMANCE_METRICS
    }
    return Figures(
        sample=sample,
        out_of_sample_start=out_of_sample_start,
        out_of_sample_end=out_of_sample_end,
        headline=performance if held_out else {},
        in_sample={} if held_out else performance,
        descriptive=descriptive,
    )
