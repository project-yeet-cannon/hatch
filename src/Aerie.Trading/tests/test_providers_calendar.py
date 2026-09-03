"""The market calendar: the piece that fails silently when it is wrong.

docs/plans/trading.md is blunt about why this is a library rather than a hand-
rolled weekday check - "a wrong market calendar does not throw, it silently
corrupts every backtest that crosses a holiday" - and the same sentence is the
reason these are tests rather than a spot check. Every case below is a day
whose answer is a fact about the NYSE that nobody carries in their head.
"""

from datetime import UTC, date, datetime

import pytest

from aerie_trading.providers.base import MarketClosed
from aerie_trading.providers.calendar import MarketCalendar, get_market_calendar

ANCHOR = date(2015, 1, 2)


@pytest.fixture(scope="module")
def calendar() -> MarketCalendar:
    return get_market_calendar("XNYS", ANCHOR)


def test_a_holiday_is_not_a_session(calendar: MarketCalendar) -> None:
    # 4 July 2026 is a Saturday, so the market closes on Friday the 3rd - a
    # holiday whose date is not the holiday's own date, which is exactly the
    # kind of rule a hand-written calendar gets wrong.
    assert not calendar.is_session(date(2026, 7, 3))
    assert calendar.is_session(date(2026, 7, 2))
    assert not calendar.is_session(date(2025, 12, 25))


def test_generating_a_session_on_a_holiday_is_refused(calendar: MarketCalendar) -> None:
    # The plan's rule, one layer down from Phase 3's "do not collect through a
    # holiday and record silence as data": there is no session index for a day
    # that was not a session, so nothing downstream can address one.
    with pytest.raises(MarketClosed):
        calendar.index_of(date(2026, 7, 3))
    with pytest.raises(MarketClosed):
        calendar.session_containing(datetime(2026, 7, 3, 15, 0, tzinfo=UTC))


def test_early_closes_are_detected_rather_than_enumerated(calendar: MarketCalendar) -> None:
    # The day after Thanksgiving 2026 closes at 13:00 Eastern. A collector that
    # assumed 16:00 spends the afternoon recording a flat line, and a generator
    # that assumed it invents three hours of prices.
    half_day = calendar.session_on(date(2026, 11, 27))
    assert half_day.is_early_close
    assert half_day.minutes == 210

    full_day = calendar.session_on(date(2026, 11, 30))
    assert not full_day.is_early_close
    assert full_day.minutes == 390


def test_session_boundaries_move_with_daylight_saving(calendar: MarketCalendar) -> None:
    # 09:30 Eastern is 14:30 UTC in winter and 13:30 UTC in summer. Storing a
    # naive local time is how a backtest crosses a DST seam twice.
    assert calendar.session_on(date(2026, 1, 5)).open == datetime(2026, 1, 5, 14, 30, tzinfo=UTC)
    assert calendar.session_on(date(2026, 7, 6)).open == datetime(2026, 7, 6, 13, 30, tzinfo=UTC)


def test_an_expiry_falls_back_to_the_previous_session(calendar: MarketCalendar) -> None:
    # Good Friday 2026 is 3 April. A board whose Friday is a holiday expires on
    # the Thursday rather than skipping the week.
    assert calendar.session_on_or_before(date(2026, 4, 3)).session == date(2026, 4, 2)


def test_the_session_index_does_not_depend_on_the_request(calendar: MarketCalendar) -> None:
    # The generator's whole clock. If this were derived from the window a
    # caller asked for, a backfill and an incremental collection would produce
    # different prices for the same day.
    index = calendar.index_of(date(2020, 6, 15))
    assert calendar.session_at(index).session == date(2020, 6, 15)
    assert calendar.index_of(ANCHOR) == 0


def test_a_moment_between_sessions_is_not_in_one(calendar: MarketCalendar) -> None:
    # 09:00 Eastern on a trading day is a trading day, and is not a session.
    with pytest.raises(MarketClosed):
        calendar.session_containing(datetime(2026, 9, 2, 12, 0, tzinfo=UTC))
    # And the closing instant belongs to no bar - the interval is half-open, so
    # that consecutive sessions tile without overlapping.
    with pytest.raises(MarketClosed):
        calendar.session_containing(datetime(2026, 9, 2, 20, 0, tzinfo=UTC))


def test_a_naive_datetime_is_refused(calendar: MarketCalendar) -> None:
    with pytest.raises(ValueError, match="timezone-aware"):
        calendar.session_containing(datetime(2026, 9, 2, 15, 0))
