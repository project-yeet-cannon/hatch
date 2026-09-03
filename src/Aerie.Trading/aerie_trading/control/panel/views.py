"""What the control panel is served, as models rather than as dictionaries.

docs/plans/trading.md Phase 7 ships four screens, and every one of them is a
projection of rows this silo already had. These models are that projection, and
three decisions run through all of them:

**Every result carries its provenance, and it is not optional.** The plan is
explicit: *"a run, a leaderboard row and a strategy's headline number all show
which source they came from, synthetic or Schwab. Not a footnote on a settings
page."* So ``source`` is a required field on ``RunRow`` - the type a run
appears as *everywhere* it appears - rather than something a route remembers to
include. A screen that wanted to hide it would have to be written to drop a
field it was given, which is a decision somebody makes rather than one that
happens.

**The honesty layer is carried whole, not flattened.** A row's numbers arrive
as a ``Figures`` (``honesty/presentation.py``), which is the object that
refuses to put a fitted figure in a field labelled as performance. Copying
``headline["sharpe"]`` into a ``sharpe`` field here would be a second
serializer, without the refusal, sitting in front of the first - which is
precisely the thing that phase built a model to prevent. The UI reads
``figures.headline`` and ``figures.in_sample``, and the difference between them
is the difference the plan wants visible by default.

**snake_case, and not the camelCase of ``/api/trading/version``.** That
endpoint speaks a vocabulary docs/plans/version.md fixes across every surface
in the ecosystem, and it keeps it. Everything here is this service's own
shape, read by one client; renaming it into camelCase would mean renaming
``Figures``' fields too - which would either fork the honesty model or wrap it,
and both are worse than one client calling ``figures.in_sample``.

**Decimals serialize as strings, throughout, and that is deliberate.** JSON has
one numeric type and it is a double. Money and returns are computed here as
``Decimal`` and stored as ``NUMERIC``; rendering them as JSON numbers would
round exactly the figures a person is checking by hand.
"""

from __future__ import annotations

from datetime import datetime
from decimal import Decimal

from pydantic import BaseModel, ConfigDict

from aerie_trading.honesty.presentation import Figures

__all__ = [
    "CurveView",
    "FoldView",
    "LeaderboardView",
    "ParameterView",
    "PlanView",
    "QueueView",
    "RunCounts",
    "RunDetail",
    "RunRow",
    "SourceView",
    "StrategyCard",
    "StrategyDetail",
    "SweepProgressView",
    "SweepRow",
    "TradeView",
]


class View(BaseModel):
    """The base every response model here shares.

    Frozen because a response model is a value, and ``extra="forbid"`` because
    the one mistake these are shaped to catch is a route filling in a field
    that no longer exists under a name that used to.
    """

    model_config = ConfigDict(frozen=True, extra="forbid")


class SourceView(View):
    """Where a result's data came from. See the module docstring on why this
    is required rather than optional."""

    id: int
    name: str
    description: str | None = None


class SweepProgressView(View):
    """How far along a batch is, counted from its runs."""

    total_runs: int
    finished: int
    by_status: dict[str, int]
    complete: bool
    cancelled: bool


class SweepRow(View):
    """One batch, as the strategy detail and the run detail name it."""

    id: int
    name: str
    strategy: str
    #: Written by the seed job (``seed/``) rather than by an operator. Carried
    #: to the UI because "this is the demo that arrived with the install" is
    #: the difference between a leaderboard somebody built and one that came in
    #: the box.
    seeded: bool
    created_at: datetime
    cancelled_at: datetime | None = None
    #: How many parameter sets the grid expanded to - the selection-accounting
    #: divisor, and not the row count. See ``db/models.Sweep``.
    trials: int
    #: The ``SweepSpec`` it was launched with, as it was written down. Read one
    #: row at a time by a person asking what exactly was asked for.
    spec: dict[str, object]
    aerie_revision: str | None = None
    progress: SweepProgressView


class RunRow(View):
    """One run, everywhere a run is listed: leaderboard, strategy, sweep.

    The same type in all three places on purpose. A leaderboard row that
    carried fewer fields than a strategy's "best result" would be two shapes
    for one thing, and the honesty columns would be the ones dropped from the
    smaller one.
    """

    id: int
    kind: str
    status: str
    strategy: str
    #: The exact parameters, from the ``param_set`` row. Null on a
    #: walk-forward, which chooses a different set per fold - see ``folds`` on
    #: the run detail for what it chose.
    params: dict[str, object] | None = None
    param_set_id: int | None = None

    sweep_id: int | None = None
    sweep_name: str | None = None
    #: How many siblings this run's sweep produced. The number Phase 6's
    #: selection haircut is computed against, carried so the UI can say what
    #: the haircut was for.
    trials: int | None = None
    seeded: bool = False

    source: SourceView

    symbols: tuple[str, ...]
    interval: str
    window_start: datetime
    window_end: datetime
    starting_cash: Decimal
    #: The slippage and commission models in words, as the run recorded them.
    costs: dict[str, object]

    enqueued_at: datetime
    started_at: datetime | None = None
    finished_at: datetime | None = None
    duration_ms: int | None = None
    bars: int | None = None
    attempts: int = 0
    #: The build that produced the result. The column an operator bisects with.
    aerie_revision: str | None = None
    data_fingerprint: str | None = None
    result_fingerprint: str | None = None
    error: str | None = None

    #: The numbers, split by what they may be called. See the module docstring.
    figures: Figures


class TradeView(View):
    """One fill, as the blotter recorded it."""

    sequence: int
    symbol: str
    filled_at: datetime
    quantity: int
    price: Decimal
    #: What the bar said before slippage. The pair with ``price`` is what makes
    #: a cost model visible in the blotter rather than only in a metric.
    reference_price: Decimal
    commission: Decimal
    realized_pnl: Decimal
    tag: str


class CurveView(View):
    """A run's equity curve, and an honest account of what it is.

    ``recorded`` is false for a run that finished before the curve was stored
    at all (see the 0006 migration, which backfills nothing). The distinction
    matters: a chart drawn from an absent curve and a chart drawn from a curve
    with no points look identical and mean opposite things.
    """

    recorded: bool
    #: ``[timestamp, equity]``, oldest first.
    points: tuple[tuple[datetime, Decimal], ...] = ()
    #: How many points the curve had before sampling.
    points_total: int = 0
    sampled: bool = False


class FoldView(View):
    """One fold of a walk-forward: what it chose, and what the choice did."""

    fold: int
    train_start: datetime
    train_end: datetime
    test_start: datetime
    test_end: datetime
    params: dict[str, object]
    candidates: int
    train_objective: Decimal
    starting_cash: Decimal
    ending_equity: Decimal


class RunDetail(View):
    """*"Trades, the equity curve, the metrics, and the exact parameters and
    revision, so a result can be reproduced."*"""

    run: RunRow
    sweep: SweepRow | None = None
    trades: tuple[TradeView, ...] = ()
    curve: CurveView
    folds: tuple[FoldView, ...] = ()


class ParameterView(View):
    """One parameter of a strategy, as the sweep launcher renders it.

    Read from the strategy's own pydantic model rather than from the
    ``params_schema`` copy in the database: the declared range is what the
    model validates against, and a launcher that offered a range from anywhere
    else would offer values the strategy then refuses.
    """

    name: str
    description: str = ""
    #: The declared default, as text for the reason every number here is text.
    default: str | None = None
    #: Whether this parameter has a declared range, which is what a sweep can
    #: walk. A parameter without one is configuration for a run rather than
    #: something to search over - see ``runs/sweep.py``.
    swept: bool = False
    low: Decimal | None = None
    high: Decimal | None = None
    step: Decimal | None = None
    #: How many values the declared range contributes to a grid. The multiplier
    #: behind the run-count estimate.
    count: int | None = None


class RunCounts(View):
    """Runs by status, for one strategy or for the whole queue."""

    queued: int = 0
    running: int = 0
    succeeded: int = 0
    failed: int = 0
    cancelled: int = 0

    @property
    def total(self) -> int:
        return self.queued + self.running + self.succeeded + self.failed + self.cancelled


class StrategyCard(View):
    """A strategy on the list screen: what it is, what it has done, where it is.

    ``live`` is a field with one honest value today. Nothing in this build
    trades live - the live clock is a later phase - so it reports
    ``backtest_only`` rather than being absent, because a UI that has to infer
    "not live" from a missing field is a UI that will one day infer it from a
    bug.
    """

    name: str
    description: str
    #: Whether this build still ships the strategy. A ``strategy`` row outlives
    #: the code that defined it on purpose (``runs/catalog.py``), so a
    #: leaderboard from a build that dropped one still knows what it was - and
    #: this is the field that says the sweep launcher cannot run it.
    shipped: bool
    parameters: tuple[ParameterView, ...] = ()
    sweeps: int = 0
    runs: RunCounts = RunCounts()
    last_finished_at: datetime | None = None
    #: The best walk-forward result this strategy has, which is the only kind
    #: of result that may be a headline number. Null when it has none - a
    #: strategy whose sweeps have not finished their walk-forward yet, which is
    #: what a cold boot looks like for the first minute.
    best: RunRow | None = None
    live: str = "backtest_only"


class StrategyDetail(View):
    """The strategy screen: the card, its batches, and its recent runs."""

    strategy: StrategyCard
    sweeps: tuple[SweepRow, ...] = ()
    runs: tuple[RunRow, ...] = ()


class LeaderboardView(View):
    """The board, and the question it was asked.

    The filters are echoed back because the board is a claim - *"what did best
    today"* - and a table of numbers with no statement of what was included is
    a claim whose scope the reader has to remember.
    """

    rows: tuple[RunRow, ...] = ()
    sort: str
    sample: str
    since: str
    mode: str
    #: Set when a filter can only ever be empty on this build, so the UI can
    #: say why rather than rendering "no results" over a working query.
    note: str | None = None


class QueueView(View):
    """Queue depth, for the bar above every screen."""

    counts: RunCounts


class PlanView(View):
    """A sweep expanded but not written: the estimate half of the handshake.

    ``total`` is the number that must be confirmed to launch. It is the grid,
    not the row count - ``rows`` is that - because asking somebody to confirm a
    number that includes machinery they did not ask for turns a figure to be
    read into a figure to be copied. See ``runs/sweep.SweepPlan``.
    """

    name: str
    strategy: str
    total: int
    rows: int
    combinations: int
    rejected: int
    rejection: str = ""
    #: The one line the CLI prints, so the UI and the terminal say the same
    #: thing about the same sweep.
    describe: str
    ceiling: int
