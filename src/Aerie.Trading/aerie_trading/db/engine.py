"""Connecting to the Ledger, and the two questions the control plane asks it."""

from collections.abc import Sequence
from typing import Protocol

from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine

from aerie_trading.db.health import CollectorHealth, read_collection_health
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

    def dispose(self) -> None:
        """Release pooled connections at shutdown."""
        ...


class SqlDatabase:
    """The real one: a SQLAlchemy engine over psycopg 3."""

    def __init__(self, settings: Settings) -> None:
        # Small on purpose. This process is a control plane, not a data path;
        # Phase 5's workers size their own.
        self._engine: Engine = create_ledger_engine(settings, pool_size=5)

    @property
    def engine(self) -> Engine:
        return self._engine

    def check(self) -> None:
        with self._engine.connect() as connection:
            connection.execute(text("SELECT 1"))

    def collection_health(self) -> Sequence[CollectorHealth]:
        return read_collection_health(self._engine)

    def dispose(self) -> None:
        self._engine.dispose()
