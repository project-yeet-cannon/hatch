"""Phase 4's first gate: both reference strategies backtest over collected data.

The other engine tests build their bars by hand, which is right for asserting
arithmetic and wrong for asserting that the vertical is joined up. This one
runs the whole path the plan describes - a provider writes Parquet through
``LakeWriter``, ``LakeReader`` reads it back through DuckDB, ``load_history``
turns that into a timeline, and the two shipped strategies trade over it -
because every one of those seams is a place a dtype or a timezone can differ
without anything failing until it produces a wrong number.

The timezone one is not hypothetical: ``tests/test_lake_reader.py`` records
that DuckDB renders timestamps in the *host's* zone unless told otherwise, and
a history whose instants came back in ``America/New_York`` would build a
perfectly ordered clock that disagrees with every other timestamp in the
system. Reading a real lake here is what makes that a test rather than a
comment.
"""

from collections.abc import Generator
from datetime import UTC, date, datetime, timedelta
from pathlib import Path

import pytest

from aerie_trading.engine.backtest import run_backtest
from aerie_trading.engine.history import BarHistory, load_history
from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider
from aerie_trading.strategies import REGISTRY

SYMBOLS = ("ZVZZT", "ZWZZT", "ZXZZT")
FIRST_SESSION = date(2026, 1, 2)
LAST_SESSION = date(2026, 6, 30)
WINDOW = (datetime(2026, 1, 1, tzinfo=UTC), datetime(2026, 7, 1, tzinfo=UTC))


@pytest.fixture(scope="module")
def lake(tmp_path_factory: pytest.TempPathFactory) -> Generator[LakeReader, None, None]:
    """Half a year of daily bars, collected the way the collectors collect them."""
    root: Path = tmp_path_factory.mktemp("lake")
    provider = SyntheticMarketDataProvider()
    writer = LakeWriter(
        root=root,
        provenance=Provenance("synthetic", "b" * 40, datetime(2026, 7, 1, tzinfo=UTC)),
    )
    sessions = provider.market_hours(FIRST_SESSION, LAST_SESSION)
    writer.write_bars(
        provider.bars(
            SYMBOLS,
            Interval.ONE_DAY,
            sessions[0].open,
            sessions[-1].close + timedelta(minutes=1),
        )
    )
    with LakeReader(root) as reader:
        yield reader


@pytest.fixture(scope="module")
def history(lake: LakeReader) -> BarHistory:
    return load_history(lake, SYMBOLS, Interval.ONE_DAY, *WINDOW)


def test_a_history_read_from_the_lake_is_utc_and_covers_the_window(
    history: BarHistory,
) -> None:
    assert history.symbols == SYMBOLS
    assert history.interval is Interval.ONE_DAY
    # Roughly six months of sessions, and the exact count is the calendar's
    # business rather than this test's - it is asserted as a range so a holiday
    # rule changing in exchange_calendars does not fail the engine's gate.
    assert 115 < len(history) < 130
    assert all(instant.tzinfo is UTC for instant in history.timeline)
    assert history.timeline == tuple(sorted(set(history.timeline)))


@pytest.mark.parametrize("name", sorted(REGISTRY))
def test_each_shipped_strategy_backtests_over_collected_data(
    name: str, history: BarHistory
) -> None:
    result = run_backtest(REGISTRY[name].build({}), history, name=name)

    assert result.strategy == name
    assert result.bars == len(history)
    assert result.trades > 0
    assert result.curve[0].equity == result.starting_cash
    # Nothing was bought that the account could not pay for. A backtest that
    # goes cash-negative is not a losing strategy, it is an engine that has
    # granted a margin loan nobody modelled.
    assert all(point.cash >= 0 for point in result.curve)
    # And every order that expired did so on the last bar - an expiry earlier
    # than that means a symbol stopped printing bars mid-run, which is a data
    # gap rather than a strategy decision. See engine/broker.ExpiredOrder.
    assert all(expired.order.submitted_at == history.timeline[-1] for expired in result.expired)


def test_a_window_the_lake_does_not_cover_is_refused_rather_than_run_empty(
    lake: LakeReader,
) -> None:
    # The alternative - an empty history, then a backtest over zero bars
    # reporting a flat equity curve - is a run that looks like a strategy that
    # never traded. Phase 6 would then rank it beside real ones.
    with pytest.raises(LookupError, match="Collect before backtesting"):
        load_history(
            lake,
            SYMBOLS,
            Interval.ONE_DAY,
            datetime(2019, 1, 1, tzinfo=UTC),
            datetime(2019, 2, 1, tzinfo=UTC),
        )


def test_load_history_refuses_something_that_is_not_a_reader() -> None:
    with pytest.raises(TypeError, match="LakeReader"):
        load_history(object(), SYMBOLS, Interval.ONE_DAY, *WINDOW)
