"""The bar collector: sessions in, partitions out, and a refusal on a holiday.

Two of Phase 3's gate conditions live here - *"a full simulated session
collected end to end with no gaps"* and *"a collection attempt on a market
holiday is refused rather than recording silence as data"* - and one property
the plan makes the whole re-cut argument on: **nothing in this file names the
synthetic provider except the fixture that builds one.** The collector is given
a ``MarketDataProvider`` and never asks what kind it is.
"""

from datetime import UTC, date, datetime
from pathlib import Path

import pytest
from conftest import RecordingRunLog

from aerie_trading.collect.bars import BarCollector
from aerie_trading.lake.reader import LakeReader
from aerie_trading.lake.schema import Provenance
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import Interval, MarketClosed
from aerie_trading.providers.synthetic.provider import SyntheticMarketDataProvider

# Good Friday 2026 and Christmas Day 2026: an XNYS holiday that is not a
# weekend, which is the case a weekday-only cron schedule fires on.
GOOD_FRIDAY = date(2026, 4, 3)
CHRISTMAS = date(2026, 12, 25)
SESSION_DAY = date(2026, 3, 4)


@pytest.fixture
def provider() -> SyntheticMarketDataProvider:
    return SyntheticMarketDataProvider()


@pytest.fixture
def collector(
    tmp_path: Path, provider: SyntheticMarketDataProvider, run_log: RecordingRunLog
) -> BarCollector:
    return BarCollector(
        provider=provider,
        writer=LakeWriter(tmp_path, Provenance(provider.name, "a" * 40)),
        log=run_log,
    )


def test_a_full_session_is_collected_end_to_end_with_no_gaps(
    collector: BarCollector, tmp_path: Path, run_log: RecordingRunLog
) -> None:
    """Phase 3's gate, for bars."""
    record = collector.incremental(("ZVZZT", "ZWZZT"), Interval.ONE_MINUTE, SESSION_DAY)

    assert record.gaps == []
    assert record.rows_written == 780
    assert run_log.errors == [None]

    with LakeReader(tmp_path) as reader:
        coverage = reader.bar_coverage(
            ("ZVZZT", "ZWZZT"),
            Interval.ONE_MINUTE,
            datetime(2026, 3, 4, tzinfo=UTC),
            datetime(2026, 3, 5, tzinfo=UTC),
        )
    assert coverage.get_column("bars").to_list() == [390, 390]


def test_the_closing_bar_is_inside_the_collected_window(
    collector: BarCollector, tmp_path: Path
) -> None:
    """390 bars, not 389.

    The provider's window is half-open, so a collection that ended at the
    session close would drop the bar that opens at 15:59 Eastern - the
    most-read bar of the day, missing from every session, forever.
    """
    collector.incremental(("ZVZZT",), Interval.ONE_MINUTE, SESSION_DAY)
    with LakeReader(tmp_path) as reader:
        frame = reader.bars(
            ("ZVZZT",),
            Interval.ONE_MINUTE,
            datetime(2026, 3, 4, tzinfo=UTC),
            datetime(2026, 3, 5, tzinfo=UTC),
        )
    assert frame.get_column("timestamp").max() == datetime(2026, 3, 4, 20, 59, tzinfo=UTC)


@pytest.mark.parametrize("holiday", [GOOD_FRIDAY, CHRISTMAS])
def test_a_collection_on_a_holiday_is_refused_rather_than_recorded_as_silence(
    collector: BarCollector, run_log: RecordingRunLog, holiday: date
) -> None:
    """Phase 3's gate, and the reason ``incremental`` exists apart from ``backfill``.

    The CronJob's schedule is "weekdays after the close", which fires on Good
    Friday. A collector that returned an empty result would leave a successful
    run that wrote nothing, and the last-success metric would advance over a
    day on which nothing was collected.
    """
    with pytest.raises(MarketClosed, match="silence recorded as data"):
        collector.incremental(("ZVZZT",), Interval.ONE_MINUTE, holiday)

    # Refused before the run was ever recorded: there was no collection to
    # record, which is different from a collection that failed.
    assert run_log.started == []


def test_a_range_containing_no_sessions_is_empty_rather_than_a_gap(
    collector: BarCollector, run_log: RecordingRunLog
) -> None:
    """The distinction the module docstring draws.

    A backfill over a weekend is a legitimate request with a legitimate empty
    answer. Calling it a gap would fire the collection-health alert every
    Sunday.
    """
    record = collector.backfill(("ZVZZT",), Interval.ONE_DAY, date(2026, 3, 7), date(2026, 3, 8))
    assert record.gaps == []
    assert record.rows_written == 0
    assert run_log.errors == [None]


def test_a_session_that_produced_no_bars_is_a_gap(
    tmp_path: Path, provider: SyntheticMarketDataProvider, run_log: RecordingRunLog
) -> None:
    """ "There was a market and we have nothing from it" is the reportable case.

    Simulated by a provider that answers ``market_hours`` honestly and
    ``bars`` with nothing, which is what a vendor outage looks like from here.
    """

    class SilentProvider(SyntheticMarketDataProvider):
        def bars(self, *args: object, **kwargs: object) -> tuple[()]:
            return ()

    collector = BarCollector(
        provider=SilentProvider(),
        writer=LakeWriter(tmp_path, Provenance("synthetic", "a" * 40)),
        log=run_log,
    )
    record = collector.incremental(("ZVZZT",), Interval.ONE_MINUTE, SESSION_DAY)

    assert record.rows_written == 0
    assert record.gaps == ["ZVZZT 1m 2026-03-04"]
    # Recorded as a *successful* run with a gap, not as a failure: the
    # collector did its job and the data was not there. The gauge the alert
    # reads is `trading_collection_last_success_gaps`, which is why this
    # distinction matters more than it looks.
    assert run_log.errors == [None]
    assert run_log.last.gaps == ["ZVZZT 1m 2026-03-04"]


def test_a_backfill_and_an_incremental_collection_agree_on_the_same_session(
    tmp_path: Path, provider: SyntheticMarketDataProvider, run_log: RecordingRunLog
) -> None:
    """The property the two-mode design rests on.

    They are the same code path with different arguments, so this is really
    asserting that nothing has grown a special case - and that the idempotent
    merge makes running both leave one copy of the session.
    """
    writer = LakeWriter(tmp_path, Provenance(provider.name, "a" * 40))
    collector = BarCollector(provider=provider, writer=writer, log=run_log)

    collector.backfill(("ZVZZT",), Interval.ONE_MINUTE, SESSION_DAY, SESSION_DAY)
    with LakeReader(tmp_path) as reader:
        after_backfill = reader.bars(
            ("ZVZZT",),
            Interval.ONE_MINUTE,
            datetime(2026, 3, 4, tzinfo=UTC),
            datetime(2026, 3, 5, tzinfo=UTC),
        )

    collector.incremental(("ZVZZT",), Interval.ONE_MINUTE, SESSION_DAY)
    with LakeReader(tmp_path) as reader:
        after_incremental = reader.bars(
            ("ZVZZT",),
            Interval.ONE_MINUTE,
            datetime(2026, 3, 4, tzinfo=UTC),
            datetime(2026, 3, 5, tzinfo=UTC),
        )

    assert after_backfill.equals(after_incremental)


def test_a_backfill_spanning_months_writes_one_partition_per_month(
    collector: BarCollector, run_log: RecordingRunLog
) -> None:
    """One provider call per partition, so an interruption lands between whole
    files rather than inside one."""
    record = collector.backfill(("ZVZZT",), Interval.ONE_DAY, date(2026, 1, 20), date(2026, 3, 10))
    assert sorted(record.partitions) == [
        "bars/1d/ZVZZT/2026/01.parquet",
        "bars/1d/ZVZZT/2026/02.parquet",
        "bars/1d/ZVZZT/2026/03.parquet",
    ]
    assert record.gaps == []


def test_the_after_the_close_collector_skips_back_over_a_holiday(
    collector: BarCollector,
) -> None:
    """What the CronJob calls. Good Friday's run collects Thursday's session.

    The alternative - refusing - would mean the Thursday session is only
    collected if somebody notices. The refusal belongs to a run that names a
    specific day; this one asks for "the latest", and the latest is Thursday.
    """
    record = collector.latest_session(("ZVZZT",), Interval.ONE_DAY, on=GOOD_FRIDAY)
    assert record.request["session"] == "2026-04-02"
    assert record.gaps == []


def test_a_failing_collection_is_recorded_before_it_is_re_raised(
    tmp_path: Path, run_log: RecordingRunLog
) -> None:
    """A collector that swallowed its own failure would be the worst thing this
    package could do: the Job would exit zero and the last-success metric would
    advance over a collector that has collected nothing for a week."""

    class BrokenProvider(SyntheticMarketDataProvider):
        def bars(self, *args: object, **kwargs: object) -> tuple[()]:
            raise TimeoutError("the vendor did not answer")

    collector = BarCollector(
        provider=BrokenProvider(),
        writer=LakeWriter(tmp_path, Provenance("synthetic", "a" * 40)),
        log=run_log,
    )
    with pytest.raises(TimeoutError):
        collector.incremental(("ZVZZT",), Interval.ONE_MINUTE, SESSION_DAY)

    assert run_log.errors == ["TimeoutError: the vendor did not answer"]


def test_an_early_close_is_collected_to_its_own_bell(
    collector: BarCollector, provider: SyntheticMarketDataProvider, tmp_path: Path
) -> None:
    """The day after Thanksgiving is a 210-minute session, not a 390-minute one.

    A collector that assumed 16:00 would spend the afternoon recording a flat
    line - the failure ``MarketSession.is_early_close`` exists to prevent, here
    checked end to end rather than on the calendar object.
    """
    half_day = date(2026, 11, 27)
    session = provider.market_hours(half_day, half_day)[0]
    assert session.is_early_close

    record = collector.incremental(("ZVZZT",), Interval.ONE_MINUTE, half_day)
    assert record.rows_written == session.minutes
    assert record.gaps == []


def test_a_collection_with_no_symbols_is_a_caller_error(collector: BarCollector) -> None:
    with pytest.raises(ValueError, match="at least one symbol"):
        collector.incremental((), Interval.ONE_MINUTE, SESSION_DAY)


def test_the_whole_session_collects_with_no_network(
    collector: BarCollector, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Phase 3's gate: "the whole session collects in CI without a network".

    Asserted rather than assumed by CI's environment. Every socket constructor
    is replaced with one that raises, so a dependency that quietly reached for
    a calendar over HTTP - or a provider that was not as offline as it claims -
    fails here rather than on a runner that happens to have egress.
    """
    import socket

    def no_network(*_: object, **__: object) -> None:
        raise AssertionError("a collection reached for the network")

    monkeypatch.setattr(socket, "socket", no_network)
    monkeypatch.setattr(socket, "create_connection", no_network)

    record = collector.incremental(("ZVZZT", "ZWZZT"), Interval.ONE_MINUTE, SESSION_DAY)
    assert record.rows_written == 780
    assert record.gaps == []


def test_the_run_request_records_what_was_asked_for(collector: BarCollector) -> None:
    """``ingest_run.request`` is read by a person in psql a year later, so it
    holds ISO strings rather than repr'd Python objects."""
    record = collector.backfill(("ZVZZT",), Interval.ONE_DAY, date(2026, 3, 2), date(2026, 3, 6))
    assert record.request == {
        "mode": "backfill",
        "interval": "1d",
        "symbols": ["ZVZZT"],
        "start": "2026-03-02",
        "end": "2026-03-06",
    }


def test_a_backfill_with_the_dates_the_wrong_way_round_is_refused(
    collector: BarCollector,
) -> None:
    with pytest.raises(ValueError, match="end is before start"):
        collector.backfill(("ZVZZT",), Interval.ONE_DAY, date(2026, 3, 6), date(2026, 3, 2))


def test_collecting_beyond_the_calendar_is_refused(collector: BarCollector) -> None:
    """``latest_session`` looks 30 days back and no further, so a date before
    the calendar's anchor has no session to fall back to."""
    with pytest.raises(MarketClosed):
        collector.latest_session(("ZVZZT",), Interval.ONE_DAY, on=date(2014, 1, 2))


def test_a_symbol_outside_the_universe_fails_the_run_rather_than_inventing_one(
    collector: BarCollector, run_log: RecordingRunLog
) -> None:
    """The provider raises ``KeyError`` for a symbol it does not have, which is
    Phase 2's "recording silence as data" rule one layer down. The collector
    does not catch it: a stale bar watchlist is a configuration error worth
    stopping for, unlike a stale *chain* watchlist entry, which would otherwise
    stop three good boards from being collected all session."""
    with pytest.raises(KeyError):
        collector.incremental(("NOTREAL",), Interval.ONE_MINUTE, SESSION_DAY)
    assert run_log.errors == ["KeyError: \"'NOTREAL' is not in the synthetic universe\""]
