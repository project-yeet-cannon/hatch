"""Fixtures shared across the suite.

The one thing worth sharing is a database that is not a database: every test
here runs in CI with no Postgres anywhere near it, and the endpoints under test
are the ones whose whole job is to report on a database's state. A stub is not
a shortcut around that - it is the only way to exercise the *unreachable*
branch at all, which is the branch that matters.
"""

from collections.abc import Mapping, Sequence
from datetime import UTC, datetime, timedelta

import pytest

from aerie_trading.collect.runs import RunRecord
from aerie_trading.db.health import CollectorHealth
from aerie_trading.engine.instruments import equity
from aerie_trading.engine.strategy import Params, StrategyContext
from aerie_trading.providers.base import Bar, Interval, MarketDataProvider
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
