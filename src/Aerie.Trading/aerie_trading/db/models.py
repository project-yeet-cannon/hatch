"""The Ledger's first three tables.

Nothing about strategies yet - that is Phase 5, and a schema written before the
engine that fills it is a schema written from imagination. What is here is the
vocabulary the collector needs (Phase 3) and the one shape that is genuinely
expensive to retrofit:

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
    func,
    text,
)
from sqlalchemy.dialects.postgresql import JSONB
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, relationship

__all__ = [
    "Base",
    "DataSource",
    "IngestRun",
    "IngestStatus",
    "Instrument",
    "InstrumentKind",
    "OptionRight",
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
