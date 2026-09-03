"""The chain collector - the flagship, and the piece Phase 8 lands into.

The plan's claim for this collector is that *"it collects noise today and real
chains the day Phase 8 lands, and the code does not know the difference"*. The
tests are written to hold that claim: they exercise the schedule, the refusals
and the gap reporting, and none of them assert anything about what is *in* a
board - because what is in it is the one thing that changes when the provider
does.
"""

from datetime import UTC, date, datetime, timedelta
from itertools import pairwise
from pathlib import Path

import pytest
from conftest import RecordingRunLog

from aerie_trading.collect.chains import ChainCollector, snapshot_times
from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import MarketClosed, MarketSession
from aerie_trading.providers.synthetic.config import SyntheticConfig
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider

SESSION_DAY = date(2026, 3, 4)
GOOD_FRIDAY = date(2026, 4, 3)
HALF_DAY = date(2026, 11, 27)


@pytest.fixture
def provider() -> SyntheticMarketDataProvider:
    return SyntheticMarketDataProvider()


@pytest.fixture
def collector(
    tmp_path: Path, provider: SyntheticMarketDataProvider, run_log: RecordingRunLog
) -> ChainCollector:
    return ChainCollector(
        provider=provider,
        writer=LakeWriter(tmp_path, Provenance(provider.name, "a" * 40)),
        log=run_log,
    )


def session_on(provider: SyntheticMarketDataProvider, day: date) -> MarketSession:
    return provider.market_hours(day, day)[0]


# -- the schedule --------------------------------------------------------------


def test_the_snapshot_schedule_is_derived_from_the_session(
    provider: SyntheticMarketDataProvider,
) -> None:
    """Every thirty minutes from the open, plus the close.

    Knowing the schedule *without collecting it* is what makes a missing
    snapshot detectable at all - a collector that simply ran on a timer would
    have no idea how many times it was supposed to have run.
    """
    session = session_on(provider, SESSION_DAY)
    moments = snapshot_times(session, 30)

    assert moments[0] == session.open
    # The closing *minute*, not the closing instant: the session is half-open,
    # so a board asked for at 16:00 is a board asked for after the close.
    assert moments[-1] == session.close - timedelta(minutes=1)
    assert len(moments) == 14  # 13 half-hours from 09:30, then the closing minute.
    assert all(
        later - earlier == timedelta(minutes=30) for earlier, later in pairwise(moments[:-1])
    )


def test_the_close_is_captured_because_the_interval_never_reaches_it(
    provider: SyntheticMarketDataProvider,
) -> None:
    """Without ``include_close`` the last board of the day is 15:30 Eastern.

    The half-open rule is right for tiling windows and wrong for the one
    observation every options analysis starts from.
    """
    session = session_on(provider, SESSION_DAY)
    without = snapshot_times(session, 30, include_close=False)
    assert without[-1] == session.close - timedelta(minutes=30)
    assert session.close - timedelta(minutes=1) not in without


def test_an_early_close_shortens_the_schedule_by_arithmetic_not_special_case(
    provider: SyntheticMarketDataProvider,
) -> None:
    session = session_on(provider, HALF_DAY)
    assert session.is_early_close
    moments = snapshot_times(session, 30)
    assert moments[-1] == session.close - timedelta(minutes=1)
    assert len(moments) == 8  # 210 minutes: seven half-hours, then the closing minute.


def test_the_closing_minute_is_not_scheduled_twice_at_a_one_minute_interval(
    provider: SyntheticMarketDataProvider,
) -> None:
    """At one-minute granularity the loop already reaches 15:59."""
    session = session_on(provider, SESSION_DAY)
    moments = snapshot_times(session, 1)
    assert len(moments) == len(set(moments)) == session.minutes


def test_a_zero_interval_is_refused(provider: SyntheticMarketDataProvider) -> None:
    with pytest.raises(ValueError, match="positive number of minutes"):
        snapshot_times(session_on(provider, SESSION_DAY), 0)


# -- collecting ----------------------------------------------------------------


def test_a_full_session_of_boards_is_collected_end_to_end_with_no_gaps(
    collector: ChainCollector, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    """Phase 3's gate, for chains."""
    session = session_on(provider, SESSION_DAY)
    record = collector.collect_session(("ZVZZT", "ZWZZT"), session, 30)

    assert record.gaps == []
    assert record.rows_written > 0
    assert len(record.partitions) == 28  # 14 snapshots x 2 underlyings.

    with LakeReader(tmp_path) as reader:
        assert reader.snapshot_times("ZVZZT", SESSION_DAY) == snapshot_times(session, 30)


def test_one_snapshot_writes_one_file_per_underlying(
    collector: ChainCollector, provider: SyntheticMarketDataProvider
) -> None:
    session = session_on(provider, SESSION_DAY)
    record = collector.snapshot(("ZVZZT", "ZWZZT"), session.open + timedelta(minutes=30))

    assert sorted(record.partitions) == [
        "chains/ZVZZT/2026-03-04/1500.parquet",
        "chains/ZWZZT/2026-03-04/1500.parquet",
    ]
    assert record.gaps == []


def test_the_whole_watchlist_is_one_run_rather_than_one_run_per_symbol(
    collector: ChainCollector,
    provider: SyntheticMarketDataProvider,
    run_log: RecordingRunLog,
) -> None:
    """ "Did the 15:00 snapshot happen" is one question, so it gets one row.

    Four rows saying "partly" is a worse answer than one row naming the symbol
    it could not reach.
    """
    session = session_on(provider, SESSION_DAY)
    collector.snapshot(("ZVZZT", "ZWZZT", "ZXZZT"), session.open + timedelta(minutes=30))

    assert len(run_log.finished) == 1
    assert run_log.last.detail["underlyings"] == 3


@pytest.mark.parametrize(
    ("description", "moment"),
    [
        ("before the open", datetime(2026, 3, 4, 13, 0, tzinfo=UTC)),
        ("after the close", datetime(2026, 3, 4, 21, 30, tzinfo=UTC)),
        ("on a holiday", datetime(2026, 4, 3, 15, 0, tzinfo=UTC)),
        ("on a weekend", datetime(2026, 3, 7, 15, 0, tzinfo=UTC)),
    ],
)
def test_a_snapshot_outside_a_session_is_refused(
    collector: ChainCollector,
    run_log: RecordingRunLog,
    description: str,
    moment: datetime,
) -> None:
    """Phase 3's gate: refused, not returned empty.

    A board that is "empty because the market was shut" and one that is "empty
    because the collection failed" are the same rows and different events.
    """
    with pytest.raises(MarketClosed, match="silence recorded as data"):
        collector.snapshot(("ZVZZT",), moment)
    assert run_log.started == [], f"a snapshot {description} was recorded as a run"


def test_the_closing_bell_itself_is_outside_the_session(
    collector: ChainCollector, provider: SyntheticMarketDataProvider
) -> None:
    """``collect_session`` reaches the close through the session it was handed;
    a bare ``snapshot`` at that instant is refused, because ``contains`` is
    half-open. Both are correct and the difference is worth pinning: one is
    "collect this session, including its bell", the other is "is the market
    open right now", and at 16:00:00 the answer to the second is no."""
    session = session_on(provider, SESSION_DAY)
    with pytest.raises(MarketClosed):
        collector.snapshot(("ZVZZT",), session.close)


def test_a_watchlist_entry_with_no_board_is_a_gap_not_a_failure(
    tmp_path: Path, run_log: RecordingRunLog
) -> None:
    """Phase 2 put a symbol with no options board in the universe for this.

    Raising would mean one stale watchlist entry stops collecting everything
    else for the rest of the session, which is a much larger loss than the
    thing it reports.
    """
    provider = SyntheticMarketDataProvider(SyntheticConfig())
    collector = ChainCollector(
        provider=provider,
        writer=LakeWriter(tmp_path, Provenance(provider.name, "a" * 40)),
        log=run_log,
    )
    session = session_on(provider, SESSION_DAY)
    record = collector.snapshot(("ZVZZT", "ZJZZT"), session.open + timedelta(minutes=30))

    assert record.gaps == ["ZJZZT has no option board at 2026-03-04T15:00:00+00:00"]
    assert record.partitions == ["chains/ZVZZT/2026-03-04/1500.parquet"]
    # A successful run with a gap. The alert reads the gap gauge, not the
    # status - see aerie_trading/control/collection_health.py.
    assert run_log.errors == [None]


def test_a_missing_board_is_reported_at_every_instant_it_is_missing(
    tmp_path: Path, run_log: RecordingRunLog
) -> None:
    """A name absent all session is visibly absent all session.

    Reported once, it would read as a transient; reported per snapshot, the
    gap count is proportional to how long it has been wrong.
    """
    provider = SyntheticMarketDataProvider()
    collector = ChainCollector(
        provider=provider,
        writer=LakeWriter(tmp_path, Provenance(provider.name, "a" * 40)),
        log=run_log,
    )
    session = session_on(provider, SESSION_DAY)
    record = collector.collect_session(("ZJZZT",), session, 60)

    assert len(record.gaps) == len(snapshot_times(session, 60))
    assert record.rows_written == 0


def test_a_collection_with_no_underlyings_is_a_caller_error(
    collector: ChainCollector, provider: SyntheticMarketDataProvider
) -> None:
    with pytest.raises(ValueError, match="at least one underlying"):
        collector.snapshot((), session_on(provider, SESSION_DAY).open)


def test_a_naive_instant_is_refused(collector: ChainCollector) -> None:
    with pytest.raises(ValueError, match="naive"):
        collector.snapshot(("ZVZZT",), datetime(2026, 3, 4, 15, 0))


def test_re_running_a_snapshot_replaces_it_rather_than_duplicating(
    collector: ChainCollector, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    """Idempotency by path: a snapshot's file is named after its instant."""
    session = session_on(provider, SESSION_DAY)
    moment = session.open + timedelta(minutes=30)
    first = collector.snapshot(("ZVZZT",), moment)
    collector.snapshot(("ZVZZT",), moment)

    with LakeReader(tmp_path) as reader:
        frame = reader.chain_snapshot("ZVZZT", moment)
    assert frame.height == first.rows_written
    assert reader_snapshot_count(tmp_path) == 1


def reader_snapshot_count(root: Path) -> int:
    return len(list((root / "chains" / "ZVZZT" / "2026-03-04").glob("*.parquet")))


def test_the_run_request_records_the_schedule_it_was_asked_for(
    collector: ChainCollector, provider: SyntheticMarketDataProvider
) -> None:
    session = session_on(provider, SESSION_DAY)
    record = collector.collect_session(("ZVZZT",), session, 60)
    assert record.request == {
        "mode": "session",
        "underlyings": ["ZVZZT"],
        "session": "2026-03-04",
        "every_minutes": 60,
        "snapshots": 8,
    }


def test_the_whole_session_collects_with_no_network(
    collector: ChainCollector,
    provider: SyntheticMarketDataProvider,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """Phase 3's gate, for the collector that will one day talk to a vendor.

    The assertion is worth more here than for bars: this is the collector that
    gains an HTTP client at Phase 8, and this test is what will fail the day
    somebody wires one in without noticing that the suite is meant to be
    offline.
    """
    import socket

    def no_network(*_: object, **__: object) -> None:
        raise AssertionError("a collection reached for the network")

    monkeypatch.setattr(socket, "socket", no_network)
    monkeypatch.setattr(socket, "create_connection", no_network)

    record = collector.collect_session(("ZVZZT",), session_on(provider, SESSION_DAY), 30)
    assert record.gaps == []
    assert record.rows_written > 0
