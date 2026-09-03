"""Turning "sweep these parameters" into rows on the queue.

docs/plans/trading.md Phase 5: *"A sweep: pick a strategy, pick ranges over its
declared parameters, get the cross product, enqueue N runs, watch them
complete. Sweep size is estimated and confirmed **before** enqueueing."*

**The estimate is not advice, it is a handshake.** ``plan_sweep`` computes the
grid and expands it; ``enqueue_sweep`` refuses to write anything unless the
caller passes back the number the plan reported. A caller that never looked at
the estimate cannot supply it, and a caller whose grid changed between looking
and enqueueing is told so instead of quietly launching the new one. An estimate
that were merely *printed* would be satisfied by a launcher that prints it into
a log nobody reads, which is the version of this bullet that costs an
afternoon of cluster CPU.

**The grid is numeric, because ``swept()`` is.** A swept parameter is declared
with a low, a high and a step (``engine/strategy.py``), so every dimension a
sweep can walk is a number. A parameter with no declared range is configuration
for the run - which symbol, which mode - and sweeping it would be searching over
the question rather than over the answer.

**Invalid corners are pruned at expansion, not at execution.** ``ma_crossover``
rejects ``fast >= slow`` in its own model validator, and its docstring says why:
*"so a sweep's invalid corner is rejected when the param_set is built rather
than after a worker has spent a minute producing a run whose two averages are
the same series in a different order."* This file is the other end of that
sentence. Every combination is validated through the strategy's own ``Params``
model before it becomes a row, so the plan's total is runs that will actually
run, and the difference between the cross product and the total is reported
rather than hidden.

**Every run in a sweep shares one window, one universe and one cost model.**
That is what makes the sweep a comparison: two runs that differ in their data
as well as in their parameters have nothing to say about which parameters are
better. Phase 6's walk-forward slices the window per run and is explicit about
being a different kind of object for exactly this reason.
"""

from __future__ import annotations

import itertools
import logging
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from datetime import datetime
from decimal import Decimal

from pydantic import BaseModel, ConfigDict, Field, ValidationError, field_validator, model_validator
from sqlalchemy import insert
from sqlalchemy.orm import Session

from aerie_trading.db.models import Run, RunKind, RunStatus, Sweep
from aerie_trading.engine.strategy import StrategySpec
from aerie_trading.honesty.config import WalkForwardSpec
from aerie_trading.honesty.windows import folds_over, out_of_sample
from aerie_trading.providers.base import Interval
from aerie_trading.runs.catalog import ensure_param_set, ensure_strategy
from aerie_trading.runs.costs import RETAIL_EQUITY, CostSpec
from aerie_trading.strategies import spec_for

__all__ = [
    "SweepNotConfirmed",
    "SweepPlan",
    "SweepSpec",
    "SweepTooLarge",
    "enqueue_sweep",
    "full_grid",
    "plan_sweep",
]

logger = logging.getLogger(__name__)


class SweepNotConfirmed(ValueError):
    """Raised when the confirmed size does not match the planned one.

    Carries the true total, so a caller that got it wrong can print the right
    number rather than sending the operator back to run the estimate again.
    """

    def __init__(self, planned: int, confirmed: int) -> None:
        super().__init__(
            f"this sweep is {planned} runs and {confirmed} were confirmed."
            " Re-read the estimate and confirm that number, or change the grid."
        )
        self.planned = planned
        self.confirmed = confirmed


class SweepTooLarge(ValueError):
    """Raised when a plan exceeds the configured ceiling on one enqueue."""

    def __init__(self, planned: int, ceiling: int) -> None:
        super().__init__(
            f"this sweep is {planned} runs and the ceiling is {ceiling}."
            " Raise TRADING_RUNS__MAX_SWEEP_RUNS deliberately, or narrow the grid."
        )
        self.planned = planned
        self.ceiling = ceiling


class SweepSpec(BaseModel):
    """What to sweep, over what data, priced how.

    A pydantic model rather than a dataclass because this is written into
    ``sweep.spec`` as JSONB and read back by a control panel in another
    process: ``model_dump(mode="json")`` and ``model_validate`` are the round
    trip, and validation on the way in is what stops a hand-written launch
    payload from producing ten thousand runs over a window whose end precedes
    its start.
    """

    model_config = ConfigDict(frozen=True, extra="forbid")

    #: What this sweep is called. Not unique - see ``db/models.Sweep`` - because
    #: re-running a named sweep over a wider window is a new batch, not an edit.
    name: str = Field(min_length=1, max_length=128)
    #: A key of ``strategies.REGISTRY``.
    strategy: str = Field(min_length=1, max_length=64)
    #: Parameter name to the values to walk. A parameter absent from the grid
    #: takes its declared default, which is what makes a one-run sweep of a
    #: parameterless strategy expressible as an empty grid.
    grid: Mapping[str, tuple[Decimal, ...]] = Field(default_factory=dict)

    symbols: tuple[str, ...]
    interval: Interval = Interval.ONE_DAY
    window_start: datetime
    window_end: datetime
    starting_cash: Decimal = Field(default=Decimal(100_000), gt=0)
    costs: CostSpec = RETAIL_EQUITY
    #: Lowest first. A demo sweep seeded at first boot should not sit behind an
    #: operator's ten-thousand-run search, and the reverse is also true, which
    #: is why this is on the sweep rather than a constant.
    priority: int = Field(default=100, ge=0, le=1000)
    max_attempts: int = Field(default=3, ge=1, le=10)

    #: How this sweep is evaluated out of sample (Phase 6). ``None`` means it
    #: is not, which is an explicit choice with a visible consequence: every
    #: run in the sweep is then in-sample only, and the API will refuse to
    #: serve any of their figures as performance
    #: (``honesty/presentation.py``). Carried on the spec rather than read
    #: from the environment at scoring time so that a finished result stays
    #: described by the numbers it actually ran with.
    walk_forward: WalkForwardSpec | None = WalkForwardSpec()

    @field_validator("symbols")
    @classmethod
    def _upper_and_unique(cls, value: tuple[str, ...]) -> tuple[str, ...]:
        symbols = tuple(dict.fromkeys(entry.strip().upper() for entry in value))
        if not symbols or any(not symbol for symbol in symbols):
            raise ValueError("a sweep needs at least one non-empty symbol")
        return symbols

    @field_validator("grid")
    @classmethod
    def _no_empty_dimension(
        cls, value: Mapping[str, tuple[Decimal, ...]]
    ) -> Mapping[str, tuple[Decimal, ...]]:
        for name, values in value.items():
            if not values:
                # A dimension with no values makes the cross product empty,
                # which reads as "this grid produced nothing" rather than as
                # the typo it is.
                raise ValueError(f"{name} has no values to sweep")
        return value

    @model_validator(mode="after")
    def _window_is_a_window(self) -> SweepSpec:
        for label, moment in (("window_start", self.window_start), ("window_end", self.window_end)):
            if moment.tzinfo is None:
                raise ValueError(f"{label} must be timezone-aware")
        if self.window_end <= self.window_start:
            raise ValueError("a sweep window must end after it starts")
        return self


@dataclass(frozen=True, slots=True)
class SweepPlan:
    """A sweep, expanded and validated, but not yet written anywhere.

    The object an operator confirms. It holds the parameter sets rather than a
    count, so ``enqueue_sweep`` writes exactly what was estimated - a plan that
    recomputed the expansion at enqueue time could produce a different set from
    the one whose size was confirmed.
    """

    spec: SweepSpec
    strategy: StrategySpec
    param_sets: tuple[Mapping[str, object], ...]
    #: Points in the cross product before validation pruned any.
    combinations: int
    #: How many the strategy's own model refused, and why the first one was
    #: refused. Reported rather than swallowed: a grid that prunes to nothing
    #: and a grid that was empty are different mistakes.
    rejected: int
    rejection: str

    @property
    def total(self) -> int:
        """Parameter sets this plan would run. The number that must be confirmed.

        The *grid*, not the row count. The walk-forward evaluation Phase 6 adds
        beside it is machinery rather than a search, and asking an operator to
        confirm ``20`` for a grid they can see is nineteen points would make
        the handshake a number to be copied rather than one to be read.
        """
        return len(self.param_sets)

    @property
    def rows(self) -> int:
        """Runs this plan would put on the queue, walk-forward included."""
        return self.total + (1 if self.spec.walk_forward is not None else 0)

    def describe(self) -> str:
        """One line an operator reads before confirming."""
        pruned = "" if self.rejected == 0 else f" ({self.rejected} pruned: {self.rejection})"
        forward = (
            ""
            if self.spec.walk_forward is None
            else f" plus a {self.spec.walk_forward.folds}-fold walk-forward"
        )
        return (
            f"{self.spec.name}: {self.total} run(s) of {self.spec.strategy}"
            f" over {len(self.spec.symbols)} symbol(s) at {self.spec.interval.value}"
            f" from {self.spec.window_start.date()} to {self.spec.window_end.date()}"
            f"{pruned}{forward}"
        )


def full_grid(strategy: StrategySpec, names: Sequence[str]) -> Mapping[str, tuple[Decimal, ...]]:
    """The declared range of each named parameter, walked at its declared step.

    The plan's *"pick ranges over its declared parameters"* in its literal
    form. Reading the range off the field rather than taking one as an argument
    is the point ``engine/strategy.swept`` makes at length: a range that lived
    in the launcher would be a second declaration that can disagree with the
    one the model validates against, and the disagreement would surface as a
    sweep that spends an hour producing runs the model then rejects.
    """
    space = strategy.sweep_space()
    unknown = [name for name in names if name not in space]
    if unknown:
        raise LookupError(
            f"{strategy.name} declares no sweep range for {sorted(unknown)};"
            f" it declares {sorted(space)}"
        )
    return {name: space[name].values() for name in names}


def plan_sweep(spec: SweepSpec) -> SweepPlan:
    """Expand ``spec`` into the parameter sets it would run.

    Deterministic: dimensions are walked in sorted key order, so the same spec
    produces the same runs in the same order on every machine. That matters
    more than it looks - a sweep is a comparison, and one whose runs are
    enqueued in an order that depends on dictionary iteration is one whose
    partial results differ between two attempts at the same search.
    """
    strategy = spec_for(spec.strategy)
    model = strategy.params_model

    unknown = [name for name in spec.grid if name not in model.model_fields]
    if unknown:
        raise LookupError(
            f"{spec.strategy} has no parameter {sorted(unknown)};"
            f" it has {sorted(model.model_fields)}"
        )

    names = sorted(spec.grid)
    axes = [spec.grid[name] for name in names]
    # `product` of no axes yields exactly one empty tuple, which is the right
    # answer for a strategy with nothing to sweep: one run, at every default.
    # Special-casing it would be a branch that produces the same thing.
    param_sets: list[Mapping[str, object]] = []
    rejected = 0
    rejection = ""
    for combination in itertools.product(*axes):
        candidate = dict(zip(names, combination, strict=True))
        try:
            validated = model.model_validate(candidate)
        except ValidationError as refusal:
            rejected += 1
            if not rejection:
                rejection = _first_message(refusal)
            continue
        # `mode="json"` because this becomes JSONB and is hashed as text:
        # a Decimal dumped as itself is not JSON, and dumped as a float is a
        # binary round trip through the one value a param_set is keyed on.
        param_sets.append(validated.model_dump(mode="json"))

    return SweepPlan(
        spec=spec,
        strategy=strategy,
        param_sets=tuple(param_sets),
        combinations=rejected + len(param_sets),
        rejected=rejected,
        rejection=rejection,
    )


def _first_message(refusal: ValidationError) -> str:
    errors = refusal.errors()
    return str(errors[0]["msg"]) if errors else "rejected by the strategy's parameter model"


def enqueue_sweep(
    session: Session,
    plan: SweepPlan,
    confirm: int,
    data_source_id: int,
    revision: str | None = None,
    ceiling: int = 50_000,
) -> int:
    """Write ``plan`` to the queue. Returns the new sweep's id.

    Refuses unless ``confirm`` is the number ``plan.total`` reported - see the
    module docstring on why that is a handshake rather than a formality - and
    unless the plan is within ``ceiling``.

    Does not commit; the caller owns the transaction, matching every other
    write helper in this package. A sweep whose ``sweep`` row committed and
    whose runs did not would be a batch with a denominator and no work.
    """
    if plan.total == 0:
        raise ValueError(
            f"{plan.spec.name} expands to no runs"
            f" ({plan.combinations} combination(s), all pruned: {plan.rejection})"
        )
    if confirm != plan.total:
        raise SweepNotConfirmed(plan.total, confirm)
    if plan.total > ceiling:
        raise SweepTooLarge(plan.total, ceiling)

    strategy = ensure_strategy(session, plan.strategy)
    session.flush()
    sweep = Sweep(
        name=plan.spec.name,
        strategy_id=strategy.id,
        spec=plan.spec.model_dump(mode="json"),
        total_runs=plan.rows,
        trials=plan.total,
        aerie_revision=revision,
    )
    session.add(sweep)
    session.flush()

    # Every row added, then one flush, then the ids. Reading `.id` inside the
    # comprehension would read it off a pending object - which is None until
    # something flushes, and the something that eventually did would be the
    # insert below, by which time every id had already been collected as None.
    param_sets = [ensure_param_set(session, strategy, params) for params in plan.param_sets]
    session.flush()
    param_set_ids = [row.id for row in param_sets]

    spec = plan.spec
    costs = spec.costs.to_blob()
    # One executemany rather than ten thousand ORM objects. A sweep is the one
    # place in this silo that writes rows in bulk, and the ORM's unit of work
    # would spend the whole insert building identity-mapped instances nothing
    # then reads.
    common: dict[str, object] = {
        "sweep_id": sweep.id,
        "strategy_id": strategy.id,
        "data_source_id": data_source_id,
        "symbols": list(spec.symbols),
        "interval": spec.interval.value,
        "window_start": spec.window_start,
        "window_end": spec.window_end,
        "starting_cash": spec.starting_cash,
        "costs": costs,
        "status": RunStatus.QUEUED.value,
        "max_attempts": spec.max_attempts,
    }
    rows: list[dict[str, object]] = [
        {
            **common,
            "kind": RunKind.BACKTEST.value,
            "param_set_id": param_set_id,
            "priority": spec.priority,
        }
        for param_set_id in param_set_ids
    ]
    if spec.walk_forward is not None:
        rows.append(_walk_forward_row(common, spec))

    session.execute(insert(Run), rows)

    logger.info(
        "Sweep enqueued",
        extra={
            "Sweep": spec.name,
            "SweepId": sweep.id,
            "Strategy": spec.strategy,
            "Trials": plan.total,
            "Runs": plan.rows,
            "Pruned": plan.rejected,
        },
    )
    return sweep.id


def _walk_forward_row(common: Mapping[str, object], spec: SweepSpec) -> dict[str, object]:
    """The one extra row that evaluates this sweep out of sample.

    Enqueued **behind the grid it evaluates** - priority is nudged one step
    later - which is not a detail. A walk-forward is the most expensive item in
    the batch by an order of magnitude, and a worker that claimed it first
    would hold the only leaderboard-eligible row for as long as the whole grid
    would otherwise have taken to finish. Behind the grid, a cold boot shows
    the in-sample results filling in and then the honest number arriving, which
    is also the order in which they are worth reading.

    ``oos_start`` and ``oos_end`` are computed here rather than by the worker,
    from the same ``folds_over`` the worker will walk. They are on the row from
    the moment it is enqueued because they are a *promise about what this run
    is*, and a run that only became leaderboard-eligible once it succeeded
    would be one whose eligibility could not be queried while it was queued.
    The worker recomputes the schedule against the bars it actually loaded, so
    a lake that turns out to be short at one end narrows the folds rather than
    the promise being silently wrong - see ``runs/worker.py``.
    """
    assert spec.walk_forward is not None
    schedule = folds_over(
        spec.window_start,
        spec.window_end,
        spec.walk_forward.folds,
        spec.walk_forward.train_multiple,
    )
    span = out_of_sample(schedule)
    return {
        **common,
        "kind": RunKind.WALK_FORWARD.value,
        "param_set_id": None,
        "priority": min(spec.priority + 1, 1000),
        "oos_start": span.start,
        "oos_end": span.end,
    }
