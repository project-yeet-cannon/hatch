"""``BarHistory``: one timeline, several symbols, and nothing invented.

The lookahead file asserts what a history withholds. This one asserts what it
refuses to be built from, and the one asymmetry in it that is a decision rather
than an implementation detail: **opens are exact and marks are carried
forward.** Valuing a position at the last price it traded at is what a
statement does; *trading* at a stale price is a fill that never happened.
"""

from datetime import UTC, datetime, timedelta

import pytest

from aerie_trading.engine.history import BarHistory
from aerie_trading.engine.money import money
from aerie_trading.providers.base import Interval
from tests.conftest import FIXTURE_START, daily_bars, flat_bars


def test_the_timeline_is_the_union_of_the_bars_that_exist() -> None:
    # ZWZZT is missing its second session. Nothing is interpolated for it: the
    # timeline still visits that instant because ZVZZT printed, and ZWZZT
    # simply has no bar there.
    thin = flat_bars("ZWZZT", [20.0, 21.0, 22.0])
    history = BarHistory.from_bars([*flat_bars("ZVZZT", [10.0, 11.0, 12.0]), thin[0], thin[2]])

    assert history.symbols == ("ZVZZT", "ZWZZT")
    assert len(history) == 3
    assert history.bar_at("ZWZZT", 1) is None


def test_an_open_is_exact_and_a_mark_is_carried_forward() -> None:
    thin = flat_bars("ZWZZT", [20.0, 21.0, 22.0])
    history = BarHistory.from_bars([*flat_bars("ZVZZT", [10.0, 11.0, 12.0]), thin[0], thin[2]])

    # No open for ZWZZT on the bar it did not print, so an order for it does
    # not fill (see SimBroker.fill_at).
    assert set(history.opens_at(1)) == {"ZVZZT"}
    # But it is still worth what it last traded at, so the account is valued.
    assert history.marks_at(1) == {"ZVZZT": money(11.0), "ZWZZT": money(20.0)}


def test_a_symbol_that_has_not_started_yet_has_no_mark() -> None:
    # A late listing, rather than a gap. Carrying a price *backwards* would be
    # inventing history, so the answer is simply that there is nothing there.
    late = flat_bars("ZWZZT", [20.0], start=FIXTURE_START + timedelta(days=2))
    history = BarHistory.from_bars([*flat_bars("ZVZZT", [10.0, 11.0, 12.0]), *late])

    assert history.marks_at(0) == {"ZVZZT": money(10.0)}
    assert history.window("ZWZZT", 0) == ()
    assert history.latest("ZWZZT", 0) is None
    assert set(history.marks_at(2)) == {"ZVZZT", "ZWZZT"}


def test_a_window_shorter_than_asked_for_is_not_padded() -> None:
    # A strategy warming up a 200-bar average has to notice that it does not
    # have 200 bars yet. Padding would let it compute a confident average over
    # invented history.
    history = BarHistory.from_bars(flat_bars("ZVZZT", [10.0, 11.0, 12.0]))

    assert len(history.window("ZVZZT", 1, 200)) == 2
    assert len(history.window("ZVZZT", 2, 2)) == 2


def test_an_unknown_symbol_answers_empty_rather_than_raising() -> None:
    history = BarHistory.from_bars(flat_bars("ZVZZT", [10.0, 11.0]))

    assert history.window("NOPE", 0) == ()
    assert history.bar_at("NOPE", 0) is None
    assert history.latest("NOPE", 0) is None


def test_a_mixed_interval_history_is_refused() -> None:
    # A daily bar and a minute bar sharing an index is two meanings of "the bar
    # at 14:30", and every average computed over the result is arithmetic on
    # incomparable numbers.
    with pytest.raises(ValueError, match="one interval"):
        BarHistory.from_bars(
            [
                *flat_bars("ZVZZT", [10.0], interval=Interval.ONE_DAY),
                *flat_bars("ZVZZT", [10.0], interval=Interval.ONE_MINUTE),
            ]
        )


def test_two_bars_sharing_a_timestamp_are_refused() -> None:
    # Usually a lake partition read twice. Silently keeping both would double
    # every average computed over the window that contains them.
    duplicated = daily_bars("ZVZZT", [(10.0, 10.0, 10.0, 10.0)])
    with pytest.raises(ValueError, match="share a timestamp"):
        BarHistory.from_bars([*duplicated, *duplicated])


def test_an_empty_history_is_refused() -> None:
    with pytest.raises(ValueError, match="at least one bar"):
        BarHistory.from_bars([])


def test_an_instant_off_the_timeline_is_a_lookup_error() -> None:
    history = BarHistory.from_bars(flat_bars("ZVZZT", [10.0, 11.0]))

    assert history.index_of(history.timeline[1]) == 1
    with pytest.raises(LookupError, match="not on this history"):
        history.index_of(datetime(1999, 1, 1, tzinfo=UTC))
