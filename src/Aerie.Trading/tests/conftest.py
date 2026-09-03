"""Fixtures shared across the suite.

Two things worth sharing, and they answer opposite problems.

**A database that is not a database.** Most of this suite runs with no Postgres
anywhere near it, and several of the things under test are the ones whose whole
job is to report on a database's state. ``StubDatabase`` is not a shortcut
around that - it is the only way to exercise the *unreachable* branch at all,
which is the branch that matters.

**A database that is.** Phase 5's queue cannot be tested that way, and the
fixtures at the bottom of this file say why: ``SKIP LOCKED``, a partial index
and a conditional update are Postgres behaviours, so a fake would be a test of
the fake. Those fixtures skip unless ``TRADING_TEST_DATABASE_URL`` names a
scratch database, which keeps ``make trading-test`` runnable on a laptop with
nothing installed while letting CI run them for real.
"""

import os
from collections.abc import Generator, Mapping, Sequence
from datetime import UTC, datetime, timedelta

import pytest
from sqlalchemy import create_engine, text
from sqlalchemy.engine import Engine
from sqlalchemy.exc import OperationalError

from aerie_trading.collect.runs import RunRecord
from aerie_trading.db.health import CollectorHealth
from aerie_trading.db.models import Base
from aerie_trading.engine.instruments import equity
from aerie_trading.engine.strategy import Params, StrategyContext
from aerie_trading.providers.base import Bar, Interval, MarketDataProvider
from aerie_trading.revision import Revision
from aerie_trading.runs.queue import QueueDepth


class StubDatabase:
    """A ``Database`` that answers however the test needs it to."""

    def __init__(
        self,
        *,
        healthy: bool = True,
        health: Sequence[CollectorHealth] = (),
        depth: Sequence[QueueDepth] = (),
    ) -> None:
        self.healthy = healthy
        self.health = health
        self.depth = depth
        self.checks = 0
        self.health_reads = 0
        self.depth_reads = 0
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

    def queue_depth(self) -> Sequence[QueueDepth]:
        """Phase 5's half of the same story, sharing the same failure switch.

        The interesting assertion about ``/metrics`` under a broken Ledger is
        that it still answers - with ``trading_collection_up 0`` *and*
        ``trading_runs_up 0`` - rather than failing the scrape. One flag for
        both, because "the database is down" is one condition and two would be
        settable inconsistently.
        """
        self.depth_reads += 1
        if not self.healthy:
            raise ConnectionError("the Ledger is not answering")
        return self.depth

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


# -- the engine ------------------------------------------------------------
#
# Bars built by hand, with no provider and no lake behind them. Phase 4's
# fixture gate is "a tiny synthetic price series with known correct P&L,
# asserted to the cent", and a fixture assembled from a generator is one whose
# expected P&L has to be computed by the same arithmetic it is checking.

#: A Monday, at 14:30 UTC - which is 09:30 in New York outside daylight saving,
#: so the fixture timestamps read as session opens to anyone who looks at them.
#: Nothing in the engine consults a calendar; this is for the reader.
FIXTURE_START = datetime(2026, 1, 5, 14, 30, tzinfo=UTC)


def daily_bars(
    symbol: str,
    ohlc: Sequence[tuple[float, float, float, float]],
    start: datetime = FIXTURE_START,
    interval: Interval = Interval.ONE_DAY,
) -> list[Bar]:
    """Consecutive bars from ``(open, high, low, close)`` tuples, one per day."""
    return [
        Bar(
            symbol=symbol,
            interval=interval,
            timestamp=start + timedelta(days=offset),
            open=values[0],
            high=values[1],
            low=values[2],
            close=values[3],
            adjusted_close=values[3],
            volume=1_000,
        )
        for offset, values in enumerate(ohlc)
    ]


def flat_bars(
    symbol: str,
    closes: Sequence[float],
    start: datetime = FIXTURE_START,
    interval: Interval = Interval.ONE_DAY,
) -> list[Bar]:
    """Bars whose open, high, low and close are all the same price.

    For the tests about *signals* rather than about fills: a series where the
    fill price and the signal price are the same number makes an off-by-one bar
    visible as a wrong price rather than as a plausible one.
    """
    return daily_bars(symbol, [(close, close, close, close) for close in closes], start, interval)


class ScriptedStrategy:
    """Places the orders it was told to, on the bars it was told to.

    The engine's own test double. Every other strategy under test decides
    something; this one decides nothing, so a test using it is measuring the
    loop and the accounting rather than a rule.
    """

    def __init__(self, script: Mapping[int, int], symbol: str = "ZVZZT") -> None:
        #: bar number (1-based, as ``ctx.bar_number`` counts) -> signed quantity.
        self.script = script
        self.symbol = symbol
        self.seen: list[tuple[datetime, int]] = []

    @property
    def params(self) -> Params:
        return Params()

    def on_bar(self, ctx: StrategyContext) -> None:
        self.seen.append((ctx.now, ctx.bar_number))
        quantity = self.script.get(ctx.bar_number)
        if quantity:
            ctx.order(equity(self.symbol), quantity, tag=f"bar{ctx.bar_number}")


# -- the Ledger, when there is one -----------------------------------------
#
# Everything above this line runs with no database anywhere. The queue does
# not get that option: `SELECT ... FOR UPDATE SKIP LOCKED`, a partial index, a
# conditional update and `make_interval` are Postgres behaviours, and a fake
# implementing them in Python would be a test of the fake. So these fixtures
# connect to a real Postgres when one is offered and skip when it is not.
#
# **Opt-in by an environment variable, never discovered.** A fixture that
# defaulted to `localhost:5432` would connect to whatever a developer happens
# to be running - and then TRUNCATE it. Requiring TRADING_TEST_DATABASE_URL to
# be set makes "which database do these tests destroy" an answer someone typed.
# CI sets it against a service container; `make trading-test-db` sets it against
# a throwaway one.

#: The variable that turns the Postgres-backed tests on.
LEDGER_URL_VAR = "TRADING_TEST_DATABASE_URL"

#: Emptied between tests, children first so the foreign keys are satisfied
#: without relying on CASCADE to reach something it should not. RESTART
#: IDENTITY so that a test asserting on an id is not reading the previous
#: test's sequence position.
_LEDGER_TABLES = (
    "run_curve",
    "run_metric",
    "trade",
    "run",
    "sweep",
    "param_set",
    "strategy",
    "ingest_run",
    "data_source",
    "instrument",
)


@pytest.fixture(scope="session")
def ledger_engine() -> Generator[Engine, None, None]:
    """An engine against a scratch Postgres, or a skip.

    Session-scoped because creating the schema costs more than the tests do,
    and because a connection pool per test would be a pool per test.
    """
    url = os.environ.get(LEDGER_URL_VAR)
    if not url:
        pytest.skip(f"{LEDGER_URL_VAR} is not set; the queue tests need a Postgres")

    engine = create_engine(url, pool_pre_ping=True)
    try:
        with engine.connect() as connection:
            connection.execute(text("SELECT 1"))
    except OperationalError as unreachable:  # pragma: no cover - environment, not logic
        engine.dispose()
        pytest.skip(f"{LEDGER_URL_VAR} is set but unreachable: {unreachable}")

    # `create_all` rather than `alembic upgrade head`: the two are asserted
    # equal offline by tests/test_migrations.py, and running Alembic here would
    # mean mutating the process's cached Settings so env.py picks up this URL -
    # which would leak into every other test in the session. The migration is
    # rehearsed against this same live database by
    # tests/test_run_queue.py::test_the_migrations_apply_to_a_real_postgres.
    Base.metadata.create_all(engine)
    yield engine
    engine.dispose()


@pytest.fixture
def ledger(ledger_engine: Engine) -> Engine:
    """The same engine, with every table emptied first."""
    with ledger_engine.begin() as connection:
        connection.execute(text(f"TRUNCATE {', '.join(_LEDGER_TABLES)} RESTART IDENTITY CASCADE"))
    return ledger_engine
