"""The Ledger.

Two phases of tables, and the split is the plan's own sequencing rather than an
accident of growth. Phase 1 built the vocabulary a collector needs -
``instrument``, ``data_source``, ``ingest_run`` - and deliberately stopped
there, on the grounds that *a schema written before the engine that fills it is
a schema written from imagination*. Phase 5 adds the engine's half:
``strategy``, ``param_set``, ``sweep``, ``run``, ``trade`` and ``run_metric``,
written after Phase 4 shipped the ``BacktestResult`` they record.

The one shape Phase 1 could not defer, and did not:

**An instrument is an equity or an option contract, from day one.** The plan is
explicit that all three option-strategy families are targets, and retrofitting
option contracts onto a table whose primary key is a ticker is a migration of
every row that ever referenced one. Only ``EQUITY`` is exercised at Phase 1;
the columns an option needs exist and are null for equities.

Two conventions worth stating:

- **Enumerations are text with a CHECK constraint, not a Postgres ENUM type.**
  Adding a value to a native enum is DDL that behaves differently inside a
  transaction depending on the server version, and dropping one is not possible
  at all; a CHECK constraint is an ordinary ALTER that Alembic can write in
  either direction. The Python side stays a ``StrEnum``, so the values are
  still named in exactly one place.
- **Every timestamp is ``timestamptz`` and every value written is UTC.** The
  cluster's nodes run UTC and the market calendar this eventually answers to is
  America/New_York; storing a naive local time is how a backtest silently
  crosses a DST boundary twice.
"""

from __future__ import annotations

import enum
import uuid
from datetime import date, datetime
from decimal import Decimal

from sqlalchemy import (
    BigInteger,
    CheckConstraint,
    Date,
    DateTime,
    ForeignKey,
    Index,
    Integer,
    MetaData,
    Numeric,
    String,
    Text,
    Uuid,
    func,
    text,
)
from sqlalchemy.dialects.postgresql import ARRAY, JSONB
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, relationship

__all__ = [
    "Base",
    "DataSource",
    "IngestRun",
    "IngestStatus",
    "Instrument",
    "InstrumentKind",
    "OptionRight",
    "ParamSet",
    "Run",
    "RunCurve",
    "RunMetric",
    "RunStatus",
    "Strategy",
    "Sweep",
    "Trade",
]


class Base(DeclarativeBase):
    """The declarative base, and what Alembic's autogenerate compares against.

    Every model in the silo inherits from this one so that
    ``aerie_trading/migrations/env.py`` has a single ``metadata`` to diff. A
    second base is a second set of tables Alembic cannot see.

    The naming convention is not cosmetic. Postgres generates a name for any
    constraint declared without one, and Alembic then writes a migration that
    drops it by that generated name - which is derived from the table and
    column, so it is stable right up until a column is renamed. Fixing the
    pattern here means every constraint this schema will ever have is named by
    a rule rather than by a server, and a migration that drops one says so in
    words. It has to be set before the first table is created; retrofitting it
    means renaming every constraint in the database.
    """

    metadata = MetaData(
        naming_convention={
            "ix": "ix_%(table_name)s_%(column_0_N_name)s",
            "uq": "uq_%(table_name)s_%(column_0_N_name)s",
            "ck": "ck_%(table_name)s_%(constraint_name)s",
            "fk": "fk_%(table_name)s_%(column_0_name)s_%(referred_table_name)s",
            "pk": "pk_%(table_name)s",
        }
    )


def _utcnow() -> Mapped[datetime]:
    """A ``created_at`` column defaulted by the *server*, in UTC.

    Server-side rather than Python-side because rows will eventually be written
    by more than one process - a collector, a worker, a migration - and a
    default that depends on which machine wrote the row is a clock-skew bug
    waiting for a busy day.
    """
    return mapped_column(
        DateTime(timezone=True),
        nullable=False,
        server_default=func.now(),
    )


class InstrumentKind(enum.StrEnum):
    EQUITY = "equity"
    OPTION = "option"


class OptionRight(enum.StrEnum):
    CALL = "call"
    PUT = "put"


class IngestStatus(enum.StrEnum):
    RUNNING = "running"
    SUCCEEDED = "succeeded"
    FAILED = "failed"


class Instrument(Base):
    """One tradable thing: an equity, an ETF, or a single option contract.

    ``symbol`` is the canonical identifier and is unique across kinds - a
    ticker for an equity, the OCC contract symbol for an option - which is what
    lets a position leg reference one row regardless of what it is. The
    decomposed option columns exist beside it because "every call within 30
    days of expiry, by delta" is a query, not a string operation on a symbol.
    """

    __tablename__ = "instrument"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    symbol: Mapped[str] = mapped_column(String(32), nullable=False, unique=True)
    kind: Mapped[str] = mapped_column(String(16), nullable=False)
    description: Mapped[str | None] = mapped_column(Text)

    # Null for an equity, required together for an option - asserted by the
    # CHECK below rather than by whichever writer happens to fill them in.
    underlying_symbol: Mapped[str | None] = mapped_column(String(32))
    expiry: Mapped[date | None] = mapped_column(Date)
    strike: Mapped[Decimal | None] = mapped_column(Numeric(12, 4))
    # `option_right`, not `right`: RIGHT is a reserved word in SQL (it opens a
    # RIGHT JOIN), so a column of that name works only while every statement
    # that touches it remembers to quote it. The one that forgets is a
    # hand-written CHECK constraint or a psql session during an incident.
    option_right: Mapped[str | None] = mapped_column(String(8))

    # Every column with a default has it on the *server* as well as in the
    # ORM, here and below. Rows in these tables will eventually be written by a
    # collector, a worker and a migration, and a default that only exists in
    # Python is one that is silently absent from every INSERT that does not go
    # through this class.
    #
    # Contract size. 1 for an equity and 100 for a standard US option, but
    # stored rather than assumed: an adjusted contract after a split or a
    # special dividend has neither, and a P&L computed against a hardcoded 100
    # is wrong in exactly the cases nobody checks by hand.
    multiplier: Mapped[int] = mapped_column(
        Integer, nullable=False, default=1, server_default=text("1")
    )

    is_active: Mapped[bool] = mapped_column(
        nullable=False, default=True, server_default=text("true")
    )
    created_at: Mapped[datetime] = _utcnow()

    __table_args__ = (
        CheckConstraint(
            "kind IN ('equity', 'option')",
            name="kind",
        ),
        CheckConstraint(
            "option_right IS NULL OR option_right IN ('call', 'put')",
            name="right",
        ),
        # The shape rule, in the database rather than in a comment: an option
        # has all four of its defining fields or it is not an option, and an
        # equity has none of them. Without this the first half-populated row
        # is discovered by a chain query that quietly returns nothing.
        CheckConstraint(
            "(kind = 'option') = (underlying_symbol IS NOT NULL"
            " AND expiry IS NOT NULL AND strike IS NOT NULL"
            " AND option_right IS NOT NULL)",
            name="option_fields",
        ),
        CheckConstraint("multiplier > 0", name="multiplier"),
        # The chain lookup, which is the only read pattern this table has that
        # is not by symbol or by id.
        Index("ix_instrument_underlying_expiry", "underlying_symbol", "expiry"),
    )


class DataSource(Base):
    """Where data came from - one row per provider implementation.

    A table rather than a string on every row because ``MarketDataProvider`` is
    an interface (Phase 2) and Schwab is its first implementation, not its only
    possible one. When a second provider arrives, "which of these bars came
    from where" is a join rather than an archaeology exercise, and that
    question is the one that gets asked the moment two providers disagree about
    a price.
    """

    __tablename__ = "data_source"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    name: Mapped[str] = mapped_column(String(64), nullable=False, unique=True)
    description: Mapped[str | None] = mapped_column(Text)
    is_enabled: Mapped[bool] = mapped_column(
        nullable=False, default=True, server_default=text("true")
    )

    # Everything needed to reproduce what this source returns - for the
    # synthetic generator (Phase 2) the seed, the universe and every parameter
    # of the walk; for a vendor the endpoint and the API version, and never
    # the credential. Written by
    # ``aerie_trading.providers.registry.ensure_data_source``.
    #
    # JSONB rather than columns because the shape differs per provider and
    # there is no query that filters on it: this is read one row at a time by
    # a person asking "what produced these numbers", or by the chain guardrail
    # asking one boolean of it (``base.chains_are_priceable_in``). A column per
    # provider parameter would be a migration every time a provider gains one.
    config: Mapped[dict[str, object] | None] = mapped_column(JSONB)

    created_at: Mapped[datetime] = _utcnow()

    runs: Mapped[list[IngestRun]] = relationship(back_populates="data_source")


class IngestRun(Base):
    """One call out to a provider: what was asked, what came back, what it cost.

    **One row per collection run, not per provider call** - widened by Phase 3,
    which is the phase that started writing to this table. A chain snapshot of a
    four-name watchlist is four provider calls; recording each would be a
    thousand rows a month at a granularity nobody asks a question at, and the
    question that *is* asked - "did this collector run, and did it produce
    anything" - is one row. ``aerie_trading/collect/runs.py`` writes them.

    Written by Phase 3's collectors and read by its collection-health
    metrics. The columns that look like over-collection now are the ones the
    plan names as gates later: ``quota_cost`` is what keeps two collectors from
    racing each other into Schwab's rate limit unnoticed, and
    ``aerie_revision`` is what makes "which build wrote this data" answerable
    at all - the same field every log line carries
    (``docs/plans/version.md``).

    Nothing writes to this table at Phase 1. It exists now because the phase
    that starts writing to it is the phase that is racing a wall clock, and a
    migration is a worse thing to be writing that day than a collector.
    """

    __tablename__ = "ingest_run"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    data_source_id: Mapped[int] = mapped_column(
        BigInteger,
        # RESTRICT, not CASCADE: deleting a provider must not silently delete
        # the record of everything it ever collected.
        ForeignKey("data_source.id", ondelete="RESTRICT"),
        nullable=False,
    )

    # What kind of call this was - 'bars', 'chain', 'quotes', 'market_hours'.
    # Deliberately not a CHECK constraint: unlike the enumerations above, this
    # set grows with every provider capability, and a constraint that has to be
    # migrated before a collector can log its own name is friction with no
    # reader.
    kind: Mapped[str] = mapped_column(String(32), nullable=False)

    # The request, as the provider was actually asked - symbols, window,
    # interval. JSONB rather than columns because it differs per `kind`, and
    # the question it answers ("what exactly did we ask for when this came back
    # empty") is asked by a human reading one row, not by a query.
    request: Mapped[dict[str, object] | None] = mapped_column(JSONB)

    status: Mapped[str] = mapped_column(String(16), nullable=False)
    started_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    duration_ms: Mapped[int | None] = mapped_column(Integer)

    rows_written: Mapped[int] = mapped_column(
        Integer, nullable=False, default=0, server_default=text("0")
    )
    # What this call spent against the provider's quota. One shared limiter for
    # the whole silo is a Phase 2 requirement; this is how anyone finds out
    # after the fact where the quota went.
    quota_cost: Mapped[int | None] = mapped_column(Integer)

    # How many things the run expected to find and did not - a session with no
    # bars, a watchlist entry with no board. A count rather than a boolean
    # because "collected, with three sessions missing" and "collected, with one
    # missing" are different mornings, and a column rather than a derivation
    # from `result` below because it is the one thing about a gap that is
    # aggregated: the collection-health gauge is a MAX over this per collector.
    #
    # Nullable, and null means "this run did not look" rather than zero. A run
    # that failed before it could compare what it asked for against what it got
    # has no honest gap count, and recording 0 for it would put a clean number
    # on a run that never checked.
    gap_count: Mapped[int | None] = mapped_column(Integer)

    # What the run produced: the lake partitions it wrote, the gaps it found,
    # and whatever else the collector thought worth keeping. The counterpart to
    # `request` above - that column answers "what did we ask for", this one
    # answers "what came back, and what was missing" - and the pair is what a
    # person reading one row during an incident actually needs.
    #
    # JSONB for the same reason `request` is: the shape differs per collector,
    # and nothing queries into it. The one field that *is* queried was promoted
    # to `gap_count` above rather than left in here as a `jsonb_array_length`
    # in a metrics query.
    result: Mapped[dict[str, object] | None] = mapped_column(JSONB)

    error: Mapped[str | None] = mapped_column(Text)
    aerie_revision: Mapped[str | None] = mapped_column(String(40))

    data_source: Mapped[DataSource] = relationship(back_populates="runs")

    __table_args__ = (
        CheckConstraint(
            "status IN ('running', 'succeeded', 'failed')",
            name="status",
        ),
        CheckConstraint("rows_written >= 0", name="rows_written"),
        CheckConstraint("gap_count IS NULL OR gap_count >= 0", name="gap_count"),
        # "When did this collector last succeed" is the query behind the
        # collection-health metrics Phase 3 gates on, and it is asked per
        # source and per kind.
        Index("ix_ingest_run_source_kind_started", "data_source_id", "kind", "started_at"),
    )


class RunStatus(enum.StrEnum):
    """Where a run is, and - because the ``run`` table *is* the work queue -
    the whole of the queue's state machine.

    ``queued`` -> ``running`` -> one of ``succeeded``, ``failed``,
    ``cancelled``, with one edge back: a ``running`` run whose lease expired
    returns to ``queued`` until its attempts are exhausted. There is no
    ``leased`` distinct from ``running``, because the lease is what makes a run
    running and a row that held one without executing would be a state nothing
    can produce.
    """

    QUEUED = "queued"
    RUNNING = "running"
    SUCCEEDED = "succeeded"
    FAILED = "failed"
    CANCELLED = "cancelled"

    @property
    def is_terminal(self) -> bool:
        return self in (RunStatus.SUCCEEDED, RunStatus.FAILED, RunStatus.CANCELLED)


class RunKind(enum.StrEnum):
    """What sort of work a ``run`` row is, and therefore how a worker executes it.

    Two kinds on one queue rather than two queues, and the argument is the one
    ``runs/queue.py`` already makes about not splitting the ``run`` table: a
    second queue would need its own claim, its own lease, its own reaper and
    its own depth metric, and every one of those is a mechanism that can be
    wrong in a way the first one is not.

    ``BACKTEST``
        one strategy at one parameter set over one window. What Phase 5 built,
        and what every row in a sweep's grid is.
    ``WALK_FORWARD``
        Phase 6's evaluation of a *sweep*: the whole grid, walked over rolling
        folds, parameters chosen on each train window and one account carried
        through the tests. It has no ``param_set_id`` because it does not have
        one parameter set - it has the sequence it chose, which lands in
        ``walk_forward_fold``.
    """

    BACKTEST = "backtest"
    WALK_FORWARD = "walk_forward"


class Strategy(Base):
    """One trading rule this build ships - the Ledger's side of ``StrategySpec``.

    A row per entry in ``aerie_trading.strategies.REGISTRY``, written by
    ``runs/catalog.ensure_strategy`` and refreshed on every arrival, the way
    ``data_source`` is. The ask's *"I would generally expect to have to develop
    and deploy code to introduce a whole new strategy"* is what makes that
    correct rather than lossy: the code is the definition, and this table is a
    cache of it that a ``run`` can point a foreign key at.

    ``params_schema`` is the pydantic JSON schema of the strategy's ``Params``
    model, including the ``sweep`` blocks ``swept()`` writes into each field's
    ``json_schema_extra``. Stored rather than derived at read time because
    Phase 7's launcher renders the parameter space of a strategy this build may
    no longer ship, and a form that cannot be drawn for a historical run is a
    leaderboard row nobody can reproduce.
    """

    __tablename__ = "strategy"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    name: Mapped[str] = mapped_column(String(64), nullable=False, unique=True)
    description: Mapped[str] = mapped_column(Text, nullable=False)
    params_schema: Mapped[dict[str, object]] = mapped_column(JSONB, nullable=False)
    created_at: Mapped[datetime] = _utcnow()

    param_sets: Mapped[list[ParamSet]] = relationship(back_populates="strategy")


class ParamSet(Base):
    """One point in a strategy's parameter space. The ask's *variation*.

    The plan's ontology in one table: *"A new strategy is code and a deploy; a
    variation is data."* A sweep of ten thousand combinations writes ten
    thousand rows here and ten thousand ``run`` rows pointing at them.

    ``params_hash`` is a sha256 over the parameters rendered canonically
    (``runs/catalog.params_hash``), and it carries the unique constraint rather
    than the JSONB itself. Two reasons, and the second is the load-bearing one:
    Postgres cannot build a btree unique index over ``jsonb`` at all, and
    ``jsonb`` equality is equality of the *parsed* document, so ``{"fast": 10}``
    written by a sweep and ``{"fast": 10.0}`` typed by an operator are two rows
    that produce byte-identical runs. Hashing a canonical rendering makes
    "the same parameters" one answer rather than one per speller.
    """

    __tablename__ = "param_set"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    strategy_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("strategy.id", ondelete="RESTRICT"),
        nullable=False,
    )
    params: Mapped[dict[str, object]] = mapped_column(JSONB, nullable=False)
    params_hash: Mapped[str] = mapped_column(String(64), nullable=False)
    created_at: Mapped[datetime] = _utcnow()

    strategy: Mapped[Strategy] = relationship(back_populates="param_sets")

    __table_args__ = (
        # Per strategy, not globally: two strategies can legitimately both have
        # a `window` of 20, and they are not the same variation.
        Index("uq_param_set_strategy_hash", "strategy_id", "params_hash", unique=True),
    )


class Sweep(Base):
    """A batch of runs enqueued together, and the unit cancellation acts on.

    **Not in the plan's list of Phase 5 tables, and added deliberately.** That
    list names ``strategy``, ``param_set``, ``run``, ``trade`` and
    ``run_metric``; building the phase's other bullets needs one more thing
    those five cannot hold. Cancellation is *"cancel this sweep"* rather than
    "cancel these 1,000 rows I hope I listed correctly", progress is a count
    against a denominator that has to exist before the runs finish, and Phase
    6's selection accounting is explicit that *"a run knows how many siblings
    its sweep produced"* - which is a number about the batch and not about any
    run in it. A ``sweep_id`` on ``run`` with no table behind it would be a
    foreign key to a string somebody typed twice.

    **There is no status column.** A sweep is finished when its runs are, and a
    second place recording that is a second place it can be wrong; the one
    fact the runs cannot carry is that somebody asked for it to stop, which is
    ``cancelled_at``. ``runs/queue.sweep_progress`` derives the rest.
    """

    __tablename__ = "sweep"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    # Not unique. The demo sweep is enqueued by name on every fresh install and
    # an operator re-runs a named sweep after collecting more data; both are
    # new batches over new data rather than edits of an old one.
    name: Mapped[str] = mapped_column(String(128), nullable=False)
    strategy_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("strategy.id", ondelete="RESTRICT"),
        nullable=False,
    )
    # Everything the sweep was launched with - the grid, the window, the
    # universe, the cost model. JSONB for the reason `ingest_run.request` is:
    # it is read one row at a time by a person asking "what exactly did we ask
    # for", and nothing queries into it.
    spec: Mapped[dict[str, object]] = mapped_column(JSONB, nullable=False)
    # How many runs were enqueued. The denominator of every progress bar.
    # Written once, at enqueue, and it counts Phase 6's walk-forward row as
    # well as the grid - a progress bar whose denominator omitted a run that
    # is on the queue would sit at 19/19 with work outstanding.
    total_runs: Mapped[int] = mapped_column(Integer, nullable=False)
    # How many parameter sets the grid expanded to. Phase 6's
    # selection-accounting divisor, and deliberately *not* `total_runs`: the
    # walk-forward row is not a trial, and counting it would deflate every
    # sibling Sharpe by the amount one extra draw is worth. Carried on the
    # sweep rather than counted at scoring time because a worker that had to
    # count its siblings would be reading a table its peers are still writing.
    trials: Mapped[int] = mapped_column(
        Integer, nullable=False, default=0, server_default=text("0")
    )
    # Whether the seed job wrote this batch (docs/plans/trading.md Phase 7:
    # *"Seeded rows are marked as seeded; re-running the job reconciles only
    # those. It must never touch a strategy, sweep or run the owner added"*).
    #
    # A flag on the batch rather than a naming convention, because the name is
    # the one thing an operator can reproduce by accident: a person who
    # launches a sweep and calls it `demo-ma-crossover` would otherwise have
    # their work reconciled away by the next deploy. Nothing else in the
    # schema needs it - a run belongs to its sweep, so "is this row seeded"
    # is answered one join away for everything the seed writes.
    seeded: Mapped[bool] = mapped_column(
        nullable=False, default=False, server_default=text("false")
    )
    aerie_revision: Mapped[str | None] = mapped_column(String(40))
    created_at: Mapped[datetime] = _utcnow()
    cancelled_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))

    __table_args__ = (
        CheckConstraint("total_runs >= 0", name="total_runs"),
        CheckConstraint("trials >= 0", name="trials"),
        # The seed's own lookup: "which batch did I write for this spec". A
        # partial index because seeded rows are two of however many an
        # installation accumulates, and the query never asks the question of
        # the others.
        Index(
            "ix_sweep_seeded_name",
            "name",
            postgresql_where=text("seeded"),
        ),
    )


class Run(Base):
    """One backtest: what to do, whether it has been done, and what came of it.

    **This table is the work queue.** The plan asks for *"a Postgres work queue
    - SELECT ... FOR UPDATE SKIP LOCKED, retry counts, visibility timeouts. No
    Redis, no Celery, no new infrastructure, and queue depth becomes rows the
    control panel already reads."* The last clause is what decides the shape: a
    separate ``job`` table beside this one would make queue depth a join and
    would introduce the state every two-table queue eventually gets wrong - a
    job with no run, a run with two jobs. The scheduling columns therefore live
    here, on the row they schedule.

    Three groups of columns, in the order they are filled in:

    *What to run* - the strategy, the parameters, the window, the universe, the
    interval, the starting cash and the cost model. Fixed at enqueue, and
    together they are the entire input to ``run_backtest``, which is what makes
    a run reproducible from its row.

    *Where it is* - ``status``, ``attempts``, ``available_at``, and the lease.
    See ``runs/queue.py`` for how they move.

    *What happened* - the fingerprints, the revision that produced it, and the
    error if it failed. ``result_fingerprint`` is the sha256 Phase 4 built for
    exactly this column: two runs of the same inputs that disagree on it are a
    determinism bug, and they are visible as one because both are rows here.
    """

    __tablename__ = "run"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)

    # -- what to run --------------------------------------------------------
    # `backtest` or `walk_forward`; see RunKind. Defaulted in the database as
    # well as in Python, because the migration that added it had to give every
    # existing row an answer and "the kind everything was before Phase 6" is
    # the only correct one.
    kind: Mapped[str] = mapped_column(
        String(16),
        nullable=False,
        default=RunKind.BACKTEST.value,
        server_default=text("'backtest'"),
    )
    sweep_id: Mapped[int | None] = mapped_column(
        BigInteger,
        ForeignKey("sweep.id", ondelete="RESTRICT"),
    )
    strategy_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("strategy.id", ondelete="RESTRICT"),
        nullable=False,
    )
    # Nullable from Phase 6, and the CHECK below is what keeps that from
    # weakening the ordinary case: a backtest must have one and a walk-forward
    # must not, which is stronger than the NOT NULL it replaced because it also
    # refuses the row that would otherwise be silently meaningless - a
    # walk-forward pretending to be a single parameter set.
    param_set_id: Mapped[int | None] = mapped_column(
        BigInteger,
        ForeignKey("param_set.id", ondelete="RESTRICT"),
    )
    # Which lake rows this reads, in the only durable form there is: the source
    # that wrote them. RESTRICT for the reason `ingest_run` uses it - deleting
    # a provider must not silently delete the record of what was run on it.
    data_source_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("data_source.id", ondelete="RESTRICT"),
        nullable=False,
    )

    # The universe, as an array rather than JSONB. "Which runs covered ZVZZT"
    # is `'ZVZZT' = ANY(symbols)`, which is a question Phase 6's baselines ask
    # of every run they sit beside; the same list in JSONB answers it with a
    # function call that cannot use an index.
    symbols: Mapped[list[str]] = mapped_column(ARRAY(Text), nullable=False)
    interval: Mapped[str] = mapped_column(String(8), nullable=False)
    window_start: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    window_end: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    starting_cash: Mapped[Decimal] = mapped_column(Numeric(18, 2), nullable=False)
    # `Costs.describe()` - the slippage and commission models in words. Stored
    # rather than referenced because Phase 6 re-scores a run at a higher cost
    # assumption, and the two rows differ in nothing else.
    costs: Mapped[dict[str, object]] = mapped_column(JSONB, nullable=False)

    # Which part of [window_start, window_end) this run's parameters were not
    # chosen on. Both null - the ordinary sweep run - means none of it, which
    # is a valid run that can never be a headline number
    # (`honesty/presentation.py` is where that is enforced rather than
    # asserted). A walk-forward sets them to the span its test folds covered.
    oos_start: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    oos_end: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))

    # -- where it is -------------------------------------------------------
    status: Mapped[str] = mapped_column(String(16), nullable=False)
    # Lowest first, like `nice`. Ascending so the claim query's index needs no
    # DESC column: a mixed-direction index is one more thing that has to be
    # spelled identically in the model and in the migration.
    priority: Mapped[int] = mapped_column(
        Integer, nullable=False, default=100, server_default=text("100")
    )
    attempts: Mapped[int] = mapped_column(
        Integer, nullable=False, default=0, server_default=text("0")
    )
    max_attempts: Mapped[int] = mapped_column(
        Integer, nullable=False, default=3, server_default=text("3")
    )
    # Not before this. The visibility timeout's other half: a reclaimed run is
    # pushed out by a backoff rather than being retried instantly against
    # whatever was broken a second ago.
    available_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, server_default=func.now()
    )
    # The fence. A worker may only write a result while this is still the token
    # it was handed, which is what makes a duplicated *execution* - inevitable
    # once leases expire - unable to become a duplicated *row*. A worker name
    # would not do: the same worker can legitimately re-claim a run it lost.
    lease_token: Mapped[uuid.UUID | None] = mapped_column(Uuid)
    lease_expires_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    leased_by: Mapped[str | None] = mapped_column(String(128))

    # -- what happened ------------------------------------------------------
    enqueued_at: Mapped[datetime] = _utcnow()
    started_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    finished_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    duration_ms: Mapped[int | None] = mapped_column(Integer)
    bars: Mapped[int | None] = mapped_column(Integer)
    # The build that produced the result, written by the worker at completion
    # rather than by the launcher at enqueue: a run that sat in the queue
    # across a deploy was produced by the newer build, and recording the
    # older one would be a confident lie in the one column an operator
    # bisects with.
    aerie_revision: Mapped[str | None] = mapped_column(String(40))
    # sha256 over the bars the run actually read - "the lake state it read",
    # which is the plan's wording and the only version of it that survives a
    # partition being rewritten. A run whose data fingerprint differs from its
    # sibling's was not run over the same history, whatever their windows say.
    data_fingerprint: Mapped[str | None] = mapped_column(String(64))
    # `BacktestResult.fingerprint()`.
    result_fingerprint: Mapped[str | None] = mapped_column(String(64))
    error: Mapped[str | None] = mapped_column(Text)

    __table_args__ = (
        CheckConstraint(
            "status IN ('queued', 'running', 'succeeded', 'failed', 'cancelled')",
            name="status",
        ),
        CheckConstraint("attempts >= 0", name="attempts"),
        CheckConstraint("max_attempts > 0", name="max_attempts"),
        CheckConstraint("starting_cash > 0", name="starting_cash"),
        CheckConstraint("window_end > window_start", name="window"),
        CheckConstraint("cardinality(symbols) > 0", name="symbols"),
        CheckConstraint("kind IN ('backtest', 'walk_forward')", name="kind"),
        # The two kinds, told apart by the one column that distinguishes them.
        # Written as an equivalence rather than as two implications so that
        # neither direction can be added without the other.
        CheckConstraint(
            "(kind = 'walk_forward') = (param_set_id IS NULL)",
            name="param_set_by_kind",
        ),
        # A walk-forward evaluates a *sweep's* grid, so it cannot exist without
        # one. An ordinary backtest can: an operator running a single set of
        # parameters by hand is a run with no sweep.
        CheckConstraint(
            "kind <> 'walk_forward' OR sweep_id IS NOT NULL",
            name="walk_forward_has_a_sweep",
        ),
        CheckConstraint(
            "(oos_start IS NULL) = (oos_end IS NULL)",
            name="oos_window",
        ),
        # Inside the run's own window, and a window rather than an instant. A
        # held-out span that reached outside what the run actually read would
        # be a claim about data the run never saw.
        CheckConstraint(
            "oos_start IS NULL OR ("
            " oos_start >= window_start AND oos_end <= window_end AND oos_end > oos_start)",
            name="oos_within_window",
        ),
        # The claim query, and the reason this queue needs no second table.
        # Partial, because the queued rows are a shrinking minority of a table
        # that grows forever: a full index on `status` would be almost entirely
        # entries for finished runs that no claim will ever look at, and it
        # would keep being rewritten as they finish.
        Index(
            "ix_run_claimable",
            "priority",
            "available_at",
            "id",
            postgresql_where=text("status = 'queued'"),
        ),
        # The reaper's query - "which leases have expired" - partial for the
        # same reason and over a set that is smaller still.
        Index(
            "ix_run_leased",
            "lease_expires_at",
            postgresql_where=text("status = 'running'"),
        ),
        # Sweep progress, and the leaderboard's "this sweep's runs" scan.
        Index("ix_run_sweep_status", "sweep_id", "status"),
    )


class Trade(Base):
    """One fill, as the blotter records it.

    The row-per-fill counterpart to ``BacktestResult.fills``. Written only for
    a run that succeeded, in the same transaction that marks it succeeded, so
    a half-written blotter is not a state this table has.

    ``instrument_id`` rather than a symbol string, and that is the whole reason
    Phase 1 built ``instrument`` polymorphic on its first day: *"every call
    within 30 days of expiry, by delta" is a query, not a string operation on a
    symbol*. A blotter naming bare OCC symbols would answer Phase 10's
    questions by parsing them.

    ``reference_price`` sits beside ``price`` because the difference between
    them is what execution cost, and Phase 6 re-scores runs at a higher cost
    assumption - which needs the unslipped price, not a slippage number derived
    from a model that may since have changed.
    """

    __tablename__ = "trade"

    id: Mapped[int] = mapped_column(BigInteger, primary_key=True, autoincrement=True)
    # CASCADE, unlike every other foreign key here. A trade has no meaning
    # away from its run - it is not an observation of the world, it is part of
    # one run's output - so deleting a run and leaving its fills behind would
    # leave rows nothing can interpret. Contrast `data_source`, which records
    # something that happened.
    run_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("run.id", ondelete="CASCADE"),
        nullable=False,
    )
    # Position in the run's fill sequence, from zero. The engine's order is
    # meaningful - it is the order the accounting happened in - and `id` only
    # preserves it by accident of insertion.
    sequence: Mapped[int] = mapped_column(Integer, nullable=False)
    instrument_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("instrument.id", ondelete="RESTRICT"),
        nullable=False,
    )
    filled_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    quantity: Mapped[int] = mapped_column(Integer, nullable=False)
    # Unconstrained NUMERIC on all four, and it is not laziness. The engine's
    # whole money design (engine/money.py) is that a price stops being a float
    # at the boundary; a NUMERIC(18, 6) here would put it back through a
    # rounding on the way to the one table a person reconciles the engine
    # against by hand. Postgres stores what it is given, exactly.
    price: Mapped[Decimal] = mapped_column(Numeric, nullable=False)
    reference_price: Mapped[Decimal] = mapped_column(Numeric, nullable=False)
    commission: Mapped[Decimal] = mapped_column(Numeric, nullable=False)
    realized_pnl: Mapped[Decimal] = mapped_column(Numeric, nullable=False)
    # Which position this leg belongs to. A single-leg equity trade keys on the
    # instrument's own symbol; Phase 10's spread is several rows sharing one
    # key, which is what makes it one position rather than four.
    position_key: Mapped[str] = mapped_column(String(64), nullable=False)
    tag: Mapped[str] = mapped_column(
        String(64), nullable=False, default="", server_default=text("''")
    )

    __table_args__ = (
        CheckConstraint("quantity <> 0", name="quantity"),
        CheckConstraint("sequence >= 0", name="sequence"),
        # A fill is identified by its run and its place in it, and the
        # constraint is what makes a retried write a failure rather than a
        # second blotter. The fence in `runs/queue.py` is what stops it being
        # reached; this is what happens if that is ever wrong.
        Index("uq_trade_run_sequence", "run_id", "sequence", unique=True),
    )


class RunMetric(Base):
    """One number about one run: ``(run_id, name) -> value``.

    Tall rather than wide, and the plan's own next phase is the argument. Phase
    6 adds a walk-forward return, a deflated Sharpe and a re-score at every
    point of a cost-sensitivity sweep; each of those is a migration if metrics
    are columns and an insert if they are rows. The leaderboard sorts with a
    join on ``name``, which is one index away from free.

    **An undefined metric is an absent row, never a NaN.** Sharpe over a run
    whose returns never varied has a zero denominator; ``NUMERIC`` will happily
    store ``NaN`` and Postgres sorts it *above* every number, so one degenerate
    run would top a leaderboard sorted by risk-adjusted return. Absent sorts
    last under ``ORDER BY value DESC NULLS LAST`` and reads correctly as "this
    run does not have one".

    ``name`` is free text with no constraint, deliberately. The set grows with
    every phase, and a CHECK that has to be migrated before a metric can be
    written is friction with no reader - the same argument ``ingest_run.kind``
    makes. ``engine/metrics.py`` is where the names are defined once.
    """

    __tablename__ = "run_metric"

    run_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("run.id", ondelete="CASCADE"),
        primary_key=True,
    )
    name: Mapped[str] = mapped_column(String(48), primary_key=True)
    value: Mapped[Decimal] = mapped_column(Numeric, nullable=False)


class RunCurve(Base):
    """One run's equity curve, as the run detail screen plots it.

    docs/plans/trading.md Phase 7 asks a run detail for *"trades, the equity
    curve, the metrics, and the exact parameters and revision"*. Three of those
    four were already rows; the curve was the one thing ``run_backtest``
    produced and nothing kept.

    **A table beside ``run`` rather than a column on it.** The leaderboard
    scans ``run``, and Postgres reads a row's non-TOASTed columns whether a
    query names them or not: a curve on that row makes every leaderboard page
    carry a payload no leaderboard reads. Here, the curve is fetched by exactly
    the one screen that draws it, and pruning curves to reclaim space is a
    ``DELETE`` against a table nothing else joins to.

    **Timestamp and equity, and deliberately not the whole ``EquityPoint``.**
    Cash, market value, realized and unrealized are recoverable from the
    blotter beside them and are not what a curve is looked at for; storing all
    five would be five times the bytes for a chart that plots one. The plan is
    explicit that *"Grafana carries deep-dive time series"*, and this is the
    shape that keeps that division honest rather than growing a second
    time-series store here.

    **Both values are strings, and that is not laziness.** JSON has one numeric
    type and it is a double; an equity of 100_000.01 written as a JSON number
    comes back as a float, and the run detail would quietly disagree with the
    ``final_equity`` metric that was stored as ``NUMERIC``. Strings round-trip
    the ``Decimal`` the engine actually computed.

    **Bounded, and it says when it was.** A five-year daily run is about 1,250
    points; the same window at one-minute bars is half a million, and a table
    that stored those would be a table whose size is a strategy parameter. See
    ``runs/curve.py``: the curve is sampled to a cap, ``sampled`` records
    whether that happened, and ``points_total`` says what it was sampled from -
    so a chart that is missing a spike can say so instead of looking complete.
    """

    __tablename__ = "run_curve"

    run_id: Mapped[int] = mapped_column(
        BigInteger,
        # CASCADE like ``trade`` and ``walk_forward_fold``: this is part of one
        # run's output, not an observation of the world.
        ForeignKey("run.id", ondelete="CASCADE"),
        primary_key=True,
    )
    #: ``[[timestamp, equity], ...]``, both strings, oldest first.
    points: Mapped[list[list[str]]] = mapped_column(JSONB, nullable=False)
    #: How many points the curve had before sampling. Equal to ``len(points)``
    #: when ``sampled`` is false.
    points_total: Mapped[int] = mapped_column(Integer, nullable=False)
    sampled: Mapped[bool] = mapped_column(
        nullable=False, default=False, server_default=text("false")
    )

    __table_args__ = (CheckConstraint("points_total >= 0", name="points_total"),)


class WalkForwardFold(Base):
    """What one fold of one walk-forward chose, and what the choice then did.

    docs/plans/trading.md Phase 6 asks for a walk-forward number and Phase 7
    asks for a run detail *"so a result can be reproduced"*. This table is
    where those two meet: the stitched figure lives in ``run_metric`` like
    every other number, and the sequence of parameter sets that produced it -
    which is the actual content of a walk-forward - lives here, one row per
    fold, pointing at real ``param_set`` rows.

    **``param_set_id`` rather than a JSONB copy of the parameters.** A fold's
    winner is a point in the same space every sibling run occupies, and the
    interesting query is exactly the join that makes possible: *did the fold
    that chose these parameters also run them as an ordinary backtest, and did
    the two agree*. A copied blob answers it with a comparison of two
    renderings of the same numbers.

    **``train_objective`` beside ``ending_equity`` is the diagnosis.** High
    objectives on every train window and returns that scatter around nothing on
    every test window is what overfitting looks like when you can see both
    columns, and it is invisible when you can only see the stitched total.
    """

    __tablename__ = "walk_forward_fold"

    run_id: Mapped[int] = mapped_column(
        BigInteger,
        # CASCADE, like `trade` and for the same reason: a fold is part of one
        # run's output rather than an observation of the world, and orphaned
        # folds would be rows nothing can interpret.
        ForeignKey("run.id", ondelete="CASCADE"),
        primary_key=True,
    )
    #: Position in the schedule, from zero. Fold order is chronological and
    #: meaningful - the account is carried forward through it - so this is part
    #: of the key rather than an ordering hint.
    fold: Mapped[int] = mapped_column(Integer, primary_key=True)

    train_start: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    train_end: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    test_start: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    test_end: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)

    param_set_id: Mapped[int] = mapped_column(
        BigInteger,
        ForeignKey("param_set.id", ondelete="RESTRICT"),
        nullable=False,
    )
    #: How many candidates produced a score on this fold's train window. Fewer
    #: than the grid means some were skipped - a lookback longer than the train
    #: window, a run with no variance to take a Sharpe of - and a fold that
    #: chose from three of twenty candidates is a fold whose winner means much
    #: less than the count on the sweep suggests.
    candidates: Mapped[int] = mapped_column(Integer, nullable=False)
    train_objective: Mapped[Decimal] = mapped_column(Numeric, nullable=False)
    #: Unconstrained NUMERIC for the reason ``trade`` gives: this is money the
    #: engine accounted for exactly, and a scale here would round it on the way
    #: into the one table a person reconciles a walk-forward against by hand.
    starting_cash: Mapped[Decimal] = mapped_column(Numeric, nullable=False)
    ending_equity: Mapped[Decimal] = mapped_column(Numeric, nullable=False)

    __table_args__ = (
        CheckConstraint("fold >= 0", name="fold"),
        CheckConstraint("candidates > 0", name="candidates"),
        CheckConstraint("train_end > train_start", name="train_window"),
        CheckConstraint("test_end > test_start", name="test_window"),
        # A fold trains on history that ends where its test begins. Stated as a
        # constraint because the one bug this table can hide is a schedule that
        # leaked a test bar into its own train window, and a leak of one bar is
        # invisible in every number downstream.
        CheckConstraint("test_start >= train_end", name="train_precedes_test"),
    )
