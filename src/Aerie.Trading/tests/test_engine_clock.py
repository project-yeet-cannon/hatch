"""``ReplayClock``: monotonic, unanswerable before it starts, and finite.

Small file for a small class, and each test is here because the property it
checks is one Phase 9's ``LiveClock`` also has to have. The plan's requirement
for ``engine/clock.py`` is stated as a constraint on the phase after it -
"``LiveClock`` is Phase 9 and must require no change here when it arrives" - so
these are the assertions that will be re-pointed at the second implementation
rather than the first one's implementation details.
"""

from datetime import UTC, datetime, timedelta

import pytest

from aerie_trading.engine.clock import Clock, ReplayClock

START = datetime(2026, 1, 5, 14, 30, tzinfo=UTC)
INSTANTS = tuple(START + timedelta(days=offset) for offset in range(4))


def test_a_replay_clock_satisfies_the_protocol() -> None:
    assert isinstance(ReplayClock(INSTANTS), Clock)


def test_now_is_unanswerable_before_the_first_advance() -> None:
    # A clock that answered with the epoch, or with its first instant, would
    # let a component that never started the loop produce numbers rather than
    # an error. The distinction matters because those numbers would look fine.
    clock = ReplayClock(INSTANTS)

    assert not clock.started
    with pytest.raises(LookupError, match="not been advanced"):
        _ = clock.now


def test_it_walks_its_instants_in_order_and_then_stops() -> None:
    clock = ReplayClock(INSTANTS)

    assert tuple(clock.ticks()) == INSTANTS
    assert clock.now == INSTANTS[-1]
    assert clock.remaining == 0
    assert clock.advance() is None


def test_advance_reports_the_new_instant_and_none_at_the_end() -> None:
    # Returning the instant rather than a bool is what lets a live clock, which
    # blocks until the next tick and then knows what time it is, avoid being
    # asked twice.
    clock = ReplayClock(INSTANTS[:2])

    assert clock.advance() == INSTANTS[0]
    assert clock.advance() == INSTANTS[1]
    assert clock.advance() is None


def test_a_naive_instant_is_refused() -> None:
    with pytest.raises(ValueError, match="timezone-aware"):
        ReplayClock([datetime(2026, 1, 5, 14, 30)])


def test_instants_that_do_not_strictly_increase_are_refused() -> None:
    # The failure this catches is two lake partitions concatenated without a
    # sort, and the consequence of not catching it is an engine that fills an
    # order at a price from earlier in the run.
    with pytest.raises(ValueError, match="strictly increase"):
        ReplayClock([INSTANTS[1], INSTANTS[0]])
    with pytest.raises(ValueError, match="strictly increase"):
        ReplayClock([INSTANTS[0], INSTANTS[0]])


def test_instants_are_normalised_to_utc() -> None:
    # Not cosmetic: every other timestamp in the silo is UTC, and a clock that
    # kept an offset would make `ctx.now == bar.timestamp` false for two
    # instants that are the same moment.
    elsewhere = START.astimezone(tz=None)
    clock = ReplayClock([elsewhere])

    assert clock.advance() == START
    assert clock.instants[0].tzinfo is UTC
