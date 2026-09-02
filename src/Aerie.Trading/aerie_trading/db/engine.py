"""Connecting to the Ledger, and the one question readiness asks of it."""

from typing import Protocol

from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine

from aerie_trading.settings import Settings

__all__ = ["Database", "SqlDatabase"]


class Database(Protocol):
    """What the control plane needs from a database.

    A Protocol rather than the concrete class, so the readiness endpoint can be
    tested for both of its answers without a Postgres. That matters more than
    it looks: "readiness reports not-ready when the database is unreachable" is
    a claim about a failure path, and a test that can only exercise the happy
    path is not evidence for it.
    """

    def check(self) -> None:
        """Raise if the database cannot answer a trivial query."""
        ...

    def dispose(self) -> None:
        """Release pooled connections at shutdown."""
        ...


class SqlDatabase:
    """The real one: a SQLAlchemy engine over psycopg 3."""

    def __init__(self, settings: Settings) -> None:
        self._engine: Engine = create_engine(
            settings.database_url,
            connect_args=settings.connect_args,
            # A pooled connection that Postgres closed underneath us - a
            # failover, a restart, an idle timeout - otherwise surfaces as one
            # failed request per stale connection rather than as a reconnect.
            # The cost is a round trip per checkout, which is nothing against a
            # workload whose queries are not this cheap.
            pool_pre_ping=True,
            # Small on purpose. This process is a control plane, not a data
            # path; Phase 5's workers will size their own.
            pool_size=5,
            max_overflow=5,
        )

    @property
    def engine(self) -> Engine:
        return self._engine

    def check(self) -> None:
        with self._engine.connect() as connection:
            connection.execute(text("SELECT 1"))

    def dispose(self) -> None:
        self._engine.dispose()
