"""Fixtures shared across the suite.

The one thing worth sharing is a database that is not a database: every test
here runs in CI with no Postgres anywhere near it, and the endpoints under test
are the ones whose whole job is to report on a database's state. A stub is not
a shortcut around that - it is the only way to exercise the *unreachable*
branch at all, which is the branch that matters.
"""

import pytest

from aerie_trading.revision import Revision


class StubDatabase:
    """A ``Database`` that answers however the test needs it to."""

    def __init__(self, *, healthy: bool = True) -> None:
        self.healthy = healthy
        self.checks = 0
        self.disposed = False

    def check(self) -> None:
        self.checks += 1
        if not self.healthy:
            raise ConnectionError("the Ledger is not answering")

    def dispose(self) -> None:
        self.disposed = True


#: A stamped build, for the tests that care what a stamped one looks like. The
#: sha is 40 hex characters because ``read_revision`` refuses anything shorter -
#: see the truncation note there.
STAMPED = Revision(revision="a" * 40, sequence=1234, built_at=None)


@pytest.fixture
def stub_database() -> StubDatabase:
    return StubDatabase()
