"""Connecting to the Ledger, and the two questions the control plane asks it."""

from __future__ import annotations

from collections.abc import Sequence
from typing import Protocol

from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine

from aerie_trading.db.health import CollectorHealth, read_collection_health
from aerie_trading.runs.queue import QueueDepth, read_queue_depth
from aerie_trading.settings import Settings

__all__ = ["Database", "SqlDatabase", "create_ledger_engine"]


def create_ledger_engine(settings: Settings, *, pool_size: int = 5) -> Engine:
    """An engine against the trading database.

    Shared by the control plane and by every collector process, so the
    connection arguments - and in particular the pre-ping below - are decided
    once rather than per entry point. ``pool_size`` is the one thing that
    differs between them: a control plane serves concurrent requests, and a
    CronJob that runs for thirty seconds and exits does not.
    """
    return create_engine(
        settings.database_url,
        connect_args=settings.connect_args,
        # A pooled connection that Postgres closed underneath us - a failover,
        # a restart, an idle timeout - otherwise surfaces as one failed request
        # per stale connection rather than as a reconnect. The cost is a round
        # trip per checkout, which is nothing against a workload whose queries
        # are not this cheap.
        pool_pre_ping=True,
        pool_size=pool_size,
        max_overflow=pool_size,
    )


class Database(Protocol):
    """What the control plane needs from a database.

    A Protocol rather than the concrete class, so the endpoints can be tested
    for both of their answers without a Postgres. That matters more than it
    looks: "readiness reports not-ready when the database is unreachable" is a
    claim about a failure path, and a test that can only exercise the happy
    path is not evidence for it. The same argument covers ``collection_health``
    below, whose interesting case is the one where the query raises.
    """

    def check(self) -> None:
        """Raise if the database cannot answer a trivial query."""
        ...

    def collection_health(self) -> Sequence[CollectorHealth]:
        """One row per collector, for ``/metrics``. Raise if unreachable."""
        ...

    def queue_depth(self) -> Sequence[QueueDepth]:
        """Runs by status, for ``/metrics``. Raise if unreachable.

        Here rather than on a separate protocol so that one object answers
        every question ``/metrics`` asks of the Ledger. The two collectors take
        narrower protocols of their own (``HealthSource``, ``QueueSource``),
        which is what keeps the exposition layer from being handed a pool it
        could dispose.
        """
        ...

    def dispose(self) -> None:
        """Release pooled connections at shutdown."""
        ...


class SqlDatabase:
    """The real one: a SQLAlchemy engine over psycopg 3.

    Takes the engine rather than building one, since Phase 7: the control
    plane's panel queries (``control/panel/reader.py``) need the same
    connection pool this does, and a class that made its own would give one pod
    two pools against one database - which is twice the connections CNPG
    budgets for it, for no second purpose. ``from_settings`` is the composition
    root's constructor and keeps the sizing decision in one place.
    """

    def __init__(self, engine: Engine) -> None:
        self._engine: Engine = engine

    @classmethod
    def from_settings(cls, settings: Settings) -> SqlDatabase:
        """A database over its own pool. Small on purpose: this process is a
        control plane, not a data path, and the workers size their own."""
        return cls(create_ledger_engine(settings, pool_size=5))

    @property
    def engine(self) -> Engine:
        return self._engine

    def check(self) -> None:
        with self._engine.connect() as connection:
            connection.execute(text("SELECT 1"))

    def collection_health(self) -> Sequence[CollectorHealth]:
        return read_collection_health(self._engine)

    def queue_depth(self) -> Sequence[QueueDepth]:
        return read_queue_depth(self._engine)

    def dispose(self) -> None:
        self._engine.dispose()
