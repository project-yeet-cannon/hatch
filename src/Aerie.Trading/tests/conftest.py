"""Fixtures shared across the suite.

The one thing worth sharing is a database that is not a database: every test
here runs in CI with no Postgres anywhere near it, and the endpoints under test
are the ones whose whole job is to report on a database's state. A stub is not
a shortcut around that - it is the only way to exercise the *unreachable*
branch at all, which is the branch that matters.
"""

from collections.abc import Sequence

import pytest

from aerie_trading.collect.runs import RunRecord
from aerie_trading.db.health import CollectorHealth
from aerie_trading.providers.base import MarketDataProvider
from aerie_trading.revision import Revision


class StubDatabase:
    """A ``Database`` that answers however the test needs it to."""

    def __init__(
        self,
        *,
        healthy: bool = True,
        health: Sequence[CollectorHealth] = (),
    ) -> None:
        self.healthy = healthy
        self.health = health
        self.checks = 0
        self.health_reads = 0
        self.disposed = False

    def check(self) -> None:
        self.checks += 1
        if not self.healthy:
            raise ConnectionError("the Ledger is not answering")

    def collection_health(self) -> Sequence[CollectorHealth]:
        """The same failure the readiness check has, on the metrics path.

        Sharing ``healthy`` between the two is what makes "the Ledger is down"
        one condition in a test rather than two that can be set
        inconsistently - and the interesting assertion about ``/metrics`` is
        precisely that it still answers, with ``trading_collection_up 0``,
        when this raises.
        """
        self.health_reads += 1
        if not self.healthy:
            raise ConnectionError("the Ledger is not answering")
        return self.health

    def dispose(self) -> None:
        self.disposed = True


#: A stamped build, for the tests that care what a stamped one looks like. The
#: sha is 40 hex characters because ``read_revision`` refuses anything shorter -
#: see the truncation note there.
STAMPED = Revision(revision="a" * 40, sequence=1234, built_at=None)


@pytest.fixture
def stub_database() -> StubDatabase:
    return StubDatabase()


class RecordingRunLog:
    """A ``RunLog`` that keeps its rows in a list.

    The collectors record one ``ingest_run`` row per run, and the behaviour
    worth testing is what lands in that row when a collection *fails* or comes
    back partly empty. Both are failure paths, and neither is reachable from a
    suite that needs a Postgres to record anything at all - the same argument
    ``StubDatabase`` above is written around.
    """

    def __init__(self) -> None:
        self.started: list[tuple[str, RunRecord]] = []
        self.finished: list[tuple[int, RunRecord, str | None]] = []

    def begin(self, provider: MarketDataProvider, record: RunRecord) -> int:
        self.started.append((provider.name, record))
        return len(self.started)

    def finish(self, run_id: int, record: RunRecord, error: str | None) -> None:
        self.finished.append((run_id, record, error))

    @property
    def errors(self) -> list[str | None]:
        return [error for _, _, error in self.finished]

    @property
    def last(self) -> RunRecord:
        return self.finished[-1][1]


@pytest.fixture
def run_log() -> RecordingRunLog:
    return RecordingRunLog()
