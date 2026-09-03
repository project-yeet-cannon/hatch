"""Launching a sweep from the UI, with the handshake intact.

docs/plans/trading.md Phase 5 built the estimate-and-confirm handshake and said
what it is for: *"an estimate that were merely printed would be satisfied by a
launcher that prints it into a log nobody reads"*. Phase 7 puts a button in
front of it, and a button is exactly the caller that would like to skip it - so
the two routes here are the same two commands the CLI has. ``plan`` writes
nothing and returns a number; ``launch`` refuses unless that number comes back.

**The launcher does not build a market data provider, and that is a resource
decision as much as a design one.** ``runs/__main__.py`` constructs one to get
a ``data_source`` id, which drags in ``exchange_calendars`` and therefore
pandas - 142 MB, in a control plane pod with a 512 Mi limit that otherwise
serves four endpoints and a static bundle. Here the row is *looked up* instead.
The consequence is honest and worth having: a sweep cannot be launched against
a source that has never collected anything, which is the same thing as saying a
sweep cannot be launched over a lake that is empty.

**A request is not a ``SweepSpec``, and the difference is the launcher's whole
job.** The spec wants explicit values on every swept axis; a person clicking a
parameter wants "walk the range you declared". So ``LaunchRequest`` carries the
names to walk and the values to walk them over, and turns into a spec the same
way the CLI's flags do - through ``runs/sweep.full_grid``, which reads the
range off the strategy's own model. A launcher that carried its own copy of
those ranges would be a second declaration that can disagree with the one the
model validates against.
"""

from __future__ import annotations

import logging
from collections.abc import Mapping, Sequence
from datetime import UTC, datetime
from decimal import Decimal

from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import text
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.control.panel.views import PlanView
from aerie_trading.providers.base import Interval
from aerie_trading.revision import read_revision
from aerie_trading.runs.costs import CostSpec
from aerie_trading.runs.sweep import (
    SweepPlan,
    SweepSpec,
    enqueue_sweep,
    full_grid,
    plan_sweep,
)
from aerie_trading.settings import Settings
from aerie_trading.strategies import spec_for

__all__ = [
    "AmbiguousSource",
    "LaunchRequest",
    "Launcher",
    "NoSource",
    "UnknownSource",
]

logger = logging.getLogger(__name__)


class NoSource(LookupError):
    """No ``data_source`` row exists, so there is nothing to run over.

    Raised rather than defaulted, because the alternative is a sweep whose
    every run fails with "collect before backtesting" a minute later. This is
    the same refusal, delivered while somebody is still looking at the form.
    """


class UnknownSource(LookupError):
    """A source was named and the Ledger has no row by that name."""


class AmbiguousSource(LookupError):
    """More than one source has collected, and the request named none.

    Only reachable once this installation collects from two providers, which
    is the phase after this one. It is written now because the failure it
    prevents - a sweep silently run against whichever source sorted first - is
    invisible in the result.
    """


class LaunchRequest(BaseModel):
    """What the sweep launcher sends. See the module docstring on why this is
    not a ``SweepSpec``."""

    model_config = ConfigDict(frozen=True, extra="forbid")

    strategy: str = Field(min_length=1, max_length=64)
    #: What to call the batch. Generated from the strategy and the clock when
    #: absent, matching the CLI - a person launching from a form should not
    #: have to name a thing before they know how big it is.
    name: str | None = Field(default=None, max_length=128)
    #: Parameters to walk over the range they declare, at the step they
    #: declare. The ordinary case, and the one that cannot produce a value the
    #: strategy would refuse.
    swept: tuple[str, ...] = ()
    #: Parameters to walk over exactly these values instead. Takes precedence
    #: over ``swept`` for a parameter named in both, which is what lets a
    #: launcher narrow one axis of an otherwise declared grid.
    grid: Mapping[str, tuple[Decimal, ...]] = {}

    #: The universe. Empty means what this installation collects bars for.
    symbols: tuple[str, ...] = ()
    interval: Interval = Interval.ONE_DAY
    window_start: datetime
    window_end: datetime
    starting_cash: Decimal = Field(default=Decimal(100_000), gt=0)
    costs: CostSpec | None = None
    priority: int = Field(default=100, ge=0, le=1000)
    #: Which collected source to run over. Absent means "the only one", which
    #: is the true answer for as long as there is one.
    source: str | None = Field(default=None, max_length=64)


class Launcher:
    """Plan and enqueue, over one engine and one installation's settings."""

    def __init__(self, engine: Engine, settings: Settings) -> None:
        self._engine = engine
        self._settings = settings

    def plan(self, request: LaunchRequest) -> PlanView:
        """Expand the request and report what it would cost. Writes nothing."""
        return _view(plan_sweep(self.spec(request)), self._settings.runs.max_sweep_runs)

    def launch(self, request: LaunchRequest, confirm: int) -> int:
        """Write the sweep. Returns its id.

        Refuses unless ``confirm`` is the number ``plan`` reported - the check
        is in ``enqueue_sweep``, which is the function that holds the invariant
        and has callers other than this one. Re-checking it here would be a
        second copy of a rule that must not be able to disagree with itself.
        """
        plan = plan_sweep(self.spec(request))
        revision = read_revision().revision
        with Session(self._engine) as session, session.begin():
            source_id = self._source(session, request.source)
            sweep_id = enqueue_sweep(
                session,
                plan,
                confirm=confirm,
                data_source_id=source_id,
                revision=revision,
                ceiling=self._settings.runs.max_sweep_runs,
            )
        logger.info(
            "Sweep launched from the control panel",
            extra={
                "Sweep": plan.spec.name,
                "SweepId": sweep_id,
                "Strategy": plan.spec.strategy,
                "Runs": plan.rows,
            },
        )
        return sweep_id

    def spec(self, request: LaunchRequest) -> SweepSpec:
        """The request as a ``SweepSpec``, resolved against this installation.

        Three things come from the installation rather than from the request,
        and each of them is a value a form should not be asking a person for:
        the universe it collects, the cost model it prices with, and the
        walk-forward schedule its sweeps are evaluated on. The last is the one
        that matters most - see ``honesty/config.py``: a fold count that
        differed between a seeded sweep and an operator's would make the two
        rows on one leaderboard incomparable.
        """
        strategy = spec_for(request.strategy)
        grid: dict[str, tuple[Decimal, ...]] = dict(full_grid(strategy, request.swept))
        grid.update({name: tuple(values) for name, values in request.grid.items()})
        return SweepSpec(
            name=request.name or f"{request.strategy}-{datetime.now(UTC):%Y%m%d-%H%M%S}",
            strategy=request.strategy,
            grid=grid,
            symbols=request.symbols or self._settings.collection.bar_symbols,
            interval=request.interval,
            window_start=request.window_start,
            window_end=request.window_end,
            starting_cash=request.starting_cash,
            costs=request.costs if request.costs is not None else CostSpec(),
            priority=request.priority,
            walk_forward=self._settings.honesty.walk_forward,
        )

    def _source(self, session: Session, name: str | None) -> int:
        """The ``data_source`` row a sweep will read. See the module docstring."""
        if name is not None:
            row = session.execute(
                text("SELECT id FROM data_source WHERE name = :name"),
                {"name": name},
            ).one_or_none()
            if row is None:
                raise UnknownSource(f"no data source named {name!r} has collected anything here")
            return int(row.id)

        rows = session.execute(
            text("SELECT id, name FROM data_source WHERE is_enabled ORDER BY id")
        ).all()
        if not rows:
            raise NoSource(
                "no data source has collected anything yet, so there is no history to run"
                " over. The collectors write this row on their first run."
            )
        if len(rows) > 1:
            raise AmbiguousSource(
                "this installation collects from more than one source"
                f" ({', '.join(str(row.name) for row in rows)});"
                " name the one this sweep should read."
            )
        return int(rows[0].id)


def _view(plan: SweepPlan, ceiling: int) -> PlanView:
    return PlanView(
        name=plan.spec.name,
        strategy=plan.spec.strategy,
        total=plan.total,
        rows=plan.rows,
        combinations=plan.combinations,
        rejected=plan.rejected,
        rejection=plan.rejection,
        describe=plan.describe(),
        ceiling=ceiling,
    )


def bar_symbols(settings: Settings) -> Sequence[str]:
    """What this installation collects. The launcher form's default universe."""
    return settings.collection.bar_symbols
