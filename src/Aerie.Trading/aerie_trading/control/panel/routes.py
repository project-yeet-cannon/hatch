"""The control panel's HTTP surface.

Six reads and two writes, and the split between them is the plan's own:
everything a screen renders is a ``GET`` over rows Phases 1-6 already write,
and the only thing this phase adds that *changes* anything is launching a
sweep - which is two routes rather than one, because the estimate-and-confirm
handshake is not a formality (``panel/launch.py``).

**No route catches ``InSampleFigure``, and none may.** Phase 6 put the refusal
in the serializer precisely so that a route cannot decide to serve a fitted
figure as performance, and the exception deliberately does not derive from
``ValueError`` so that a blanket ``except ValidationError`` cannot absorb it.
If one of these routes ever raises it, that is a 500 and a stack trace naming
the run - which is the correct outcome, because the alternative is a number on
a leaderboard that means nothing.

**Refusals are 409, not 400, and the body is the sentence.** An unconfirmed
sweep, a sweep over the ceiling, and an installation with nothing collected are
all "the request was understood and the state says no". Each raises with a
sentence written to be read by the person who clicked the button - the same
sentences the CLI prints - and the route passes it through rather than
replacing it with a status name.

**Which errors become which status is stated once, in ``_refusals``.** A route
that mapped its own exceptions would be a route that quietly returns a
different status from its neighbour for the same failure.
"""

# FastAPI registers a route by decorating a function, and inside a factory
# those functions are never referenced by name afterwards - the framework's
# idiom, which pyright's strict mode reads as dead code. Scoped to this file,
# like control/app.py does for the same reason.
# pyright: reportUnusedFunction=false

from __future__ import annotations

import logging
from typing import Annotated, Literal

from fastapi import APIRouter, HTTPException, Query
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy.engine import Engine

from aerie_trading.control.panel.launch import (
    AmbiguousSource,
    Launcher,
    LaunchRequest,
    NoSource,
    UnknownSource,
)
from aerie_trading.control.panel.reader import (
    DEFAULT_LIMIT,
    DEFAULT_SORT,
    Panel,
    UnknownRun,
    UnknownStrategy,
    UnknownSweep,
)
from aerie_trading.control.panel.views import (
    LeaderboardView,
    PlanView,
    QueueView,
    RunDetail,
    StrategyCard,
    StrategyDetail,
)
from aerie_trading.providers.base import Interval
from aerie_trading.runs.sweep import SweepNotConfirmed, SweepTooLarge
from aerie_trading.settings import Settings

__all__ = ["InstallationView", "LaunchCommand", "SweepLaunched", "create_panel_router"]

logger = logging.getLogger(__name__)


class InstallationView(BaseModel):
    """What this installation is, for a launcher form that should not guess.

    Every field here is a value the sweep launcher would otherwise have to
    hard-code - the universe, the intervals, the ceiling on one enqueue, the
    walk-forward schedule. docs/ethos.md's one rule is that nothing in this
    repository may be true of exactly one installation, and a form with a
    symbol list compiled into it is exactly that.
    """

    model_config = ConfigDict(frozen=True, extra="forbid")

    symbols: tuple[str, ...]
    intervals: tuple[str, ...]
    #: The most runs one enqueue may hold, so the form can say what it is
    #: before somebody hits it rather than after.
    max_sweep_runs: int
    #: How many folds a sweep's walk-forward uses here, or null if this
    #: installation evaluates none - in which case no result it produces can
    #: ever be a headline number, and the UI should say so.
    walk_forward_folds: int | None = None


class LaunchCommand(BaseModel):
    """A launch request, plus the number the estimate reported."""

    model_config = ConfigDict(frozen=True, extra="forbid")

    request: LaunchRequest
    confirm: int = Field(ge=0)


class SweepLaunched(BaseModel):
    """What a successful launch answers with."""

    model_config = ConfigDict(frozen=True, extra="forbid")

    sweep_id: int


def create_panel_router(engine: Engine, settings: Settings) -> APIRouter:
    """The panel's routes, over one engine.

    A factory taking the engine rather than a module-level router reaching for
    a global, for the reason ``create_app`` is a factory: a test needs to point
    these at a scratch database, and a router that found its own connection
    could only be tested against whatever the process was configured for.
    """
    panel = Panel(engine)
    launcher = Launcher(engine, settings)
    router = APIRouter(prefix="/api/trading")

    @router.get("/installation")
    async def installation() -> InstallationView:
        return InstallationView(
            symbols=tuple(settings.collection.bar_symbols),
            intervals=tuple(interval.value for interval in Interval),
            max_sweep_runs=settings.runs.max_sweep_runs,
            # Never null on this build - `HonestyConfig.walk_forward` is not
            # optional - and the field is nullable anyway, because a sweep
            # launched with `walk_forward=None` is a legal object
            # (`runs/sweep.SweepSpec`) and the UI has to be able to say that a
            # board holds no headline-eligible rows.
            walk_forward_folds=settings.honesty.walk_forward.folds,
        )

    @router.get("/strategies")
    def strategies() -> list[StrategyCard]:
        """The list screen: *"every strategy, its parameter space, its best
        walk-forward result, its live status."*

        A ``def`` rather than an ``async def``, here and on every route below
        that touches the Ledger. The driver is synchronous, and FastAPI runs a
        plain ``def`` in a worker thread; written ``async``, one slow query
        would block the event loop and therefore every other request on this
        pod - including the readiness probe, which would take the pod out of
        the Service for the duration of a leaderboard scan.
        """
        return list(panel.strategies())

    @router.get("/strategies/{name}")
    def strategy(name: str) -> StrategyDetail:
        with _refusals():
            return panel.strategy(name)

    @router.get("/leaderboard")
    def leaderboard(
        sort: str = DEFAULT_SORT,
        sample: str = "out_of_sample",
        since: str = "inception",
        mode: str = "backtest",
        strategy: str | None = None,
        source: str | None = None,
        limit: Annotated[int, Query(ge=1)] = DEFAULT_LIMIT,
    ) -> LeaderboardView:
        with _refusals():
            return panel.leaderboard(
                sort=sort,
                sample=sample,
                since=since,
                mode=mode,
                strategy=strategy,
                source=source,
                limit=limit,
            )

    @router.get("/runs/{run_id}")
    def run(run_id: int) -> RunDetail:
        with _refusals():
            return panel.run(run_id)

    @router.get("/sweeps/{sweep_id}")
    def sweep(sweep_id: int) -> StrategyDetail:
        with _refusals():
            return panel.sweep(sweep_id)

    @router.get("/queue")
    def queue() -> QueueView:
        return QueueView(counts=panel.queue())

    @router.post("/sweeps/plan")
    def plan(request: LaunchRequest) -> PlanView:
        """The estimate. Writes nothing, and is the only way to learn the
        number ``POST /sweeps`` requires."""
        with _refusals():
            return launcher.plan(request)

    @router.post("/sweeps", status_code=201)
    def launch(command: LaunchCommand) -> SweepLaunched:
        with _refusals():
            return SweepLaunched(sweep_id=launcher.launch(command.request, command.confirm))

    return router


class _refusals:
    """One place where an exception becomes a status code.

    A context manager rather than FastAPI exception handlers, deliberately:
    handlers are registered on the *application*, and this router is meant to
    be mountable on one that has its own. Every mapping here is visible at the
    call site it protects.

    ``LookupError`` is 404 and covers ``spec_for``'s refusal as well as this
    module's own three - a strategy this build no longer ships is exactly as
    absent as one that never existed, from the point of view of a form trying
    to launch it.
    """

    def __enter__(self) -> None:
        return None

    def __exit__(
        self, kind: object, error: BaseException | None, traceback: object
    ) -> Literal[False]:
        if error is None:
            return False
        if isinstance(error, (UnknownStrategy, UnknownRun, UnknownSweep, UnknownSource)):
            raise HTTPException(status_code=404, detail=str(error)) from error
        if isinstance(error, (SweepNotConfirmed, SweepTooLarge, NoSource, AmbiguousSource)):
            # 409: understood, and refused by the state rather than by the
            # shape. The detail is the sentence the CLI would have printed.
            raise HTTPException(status_code=409, detail=str(error)) from error
        if isinstance(error, LookupError):
            raise HTTPException(status_code=404, detail=str(error)) from error
        if isinstance(error, ValueError):
            # A filter this build does not have, or a spec that does not
            # describe a sweep. 400 rather than 422, which FastAPI has already
            # spent on the body not parsing at all.
            raise HTTPException(status_code=400, detail=str(error)) from error
        return False
