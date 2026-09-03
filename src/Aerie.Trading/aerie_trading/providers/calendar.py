"""The market calendar, and the only place this silo talks to pandas.

``exchange_calendars`` is one of the three libraries that made this a Python
silo in the first place (docs/plans/trading.md, *Why Python, specifically*): it
knows every NYSE holiday and every half-day forward and back, and a wrong
market calendar does not throw - it silently corrupts every backtest that
crosses one.

**Everything about it is contained in this module**, deliberately, and two
different kinds of containment are going on:

- *Typing.* ``exchange_calendars`` and ``pandas`` ship no type information, so
  under this project's strict pyright every value out of them is Unknown. The
  suppressions below are scoped to this file, which turns "the whole silo is
  loosely typed" into "one adapter is, and what leaves it is
  ``MarketSession``". The same containment argument as the ``pyright:
  reportUnusedFunction`` line in ``control/app.py``.
- *Cost.* The library's session boundaries live in pandas Series. Reading them
  once into plain tuples at construction, rather than per query, means the
  generator's inner loops are pure Python arithmetic - which matters because a
  bar's price is derived by walking every session since the anchor, so a
  pandas lookup per session would be the whole runtime.
"""

# See the module docstring: these are the price of an untyped dependency, paid
# once, here. Every value that leaves this module is a MarketSession.
# pyright: reportMissingTypeStubs=false, reportUnknownMemberType=false
# pyright: reportUnknownVariableType=false, reportUnknownArgumentType=false

from __future__ import annotations

import bisect
from datetime import UTC, date, datetime, time, timedelta
from functools import lru_cache
from typing import Any
from zoneinfo import ZoneInfo

import exchange_calendars as xcals

from aerie_trading.providers.base import MarketClosed, MarketSession

__all__ = ["MarketCalendar", "get_market_calendar"]

#: A regular US equity session closes at 16:00 Eastern. Anything earlier is an
#: early close, which is how ``MarketSession.is_early_close`` is derived rather
#: than enumerated - the list of half-days changes, the rule does not.
_REGULAR_CLOSE = time(16, 0)


def _session_bounds(name: str, start: date) -> list[tuple[date, datetime, datetime, ZoneInfo]]:
    """Read the library's pandas Series into plain values, once.

    The only function in the silo that touches ``exchange_calendars`` directly,
    which is the point: everything it hands back is a builtin, so the untyped
    surface is this signature's width and no wider. That is also why the
    suppressions at the top of the file can be as narrow as they are.
    """
    calendar: Any = xcals.get_calendar(name, start=start.isoformat())
    tz = ZoneInfo(str(calendar.tz))
    opens: Any = calendar.opens
    closes: Any = calendar.closes

    return [
        (
            stamp.date(),
            opened.to_pydatetime().replace(tzinfo=UTC),
            closed.to_pydatetime().replace(tzinfo=UTC),
            tz,
        )
        for stamp, opened, closed in zip(opens.index, opens, closes, strict=True)
    ]


class MarketCalendar:
    """Sessions and their boundaries, as plain values.

    Construction reads the whole session list once - a few thousand rows for a
    decade, which is milliseconds - and every query afterwards is a binary
    search over a tuple. That trade is deliberate: the alternative is a pandas
    lookup inside the generator's per-session loop.
    """

    def __init__(self, name: str, start: date) -> None:
        self._name = name
        self._sessions: tuple[MarketSession, ...] = tuple(
            MarketSession(
                session=session,
                open=opened,
                close=closed,
                # An early close is derived from the rule rather than looked up
                # in a list of half-days: the list changes every year and the
                # rule does not. Compared in the exchange's own local time, so
                # that it is a question about the closing bell rather than
                # about daylight saving.
                is_early_close=closed.astimezone(tz).timetz().replace(tzinfo=None) < _REGULAR_CLOSE,
            )
            for session, opened, closed, tz in _session_bounds(name, start)
            if session >= start
        )
        # The parallel key array is what ``bisect`` searches. Kept beside the
        # sessions rather than derived per call, because "which session index
        # is this date" is asked once per bar.
        self._dates: tuple[date, ...] = tuple(session.session for session in self._sessions)
        if not self._sessions:
            raise ValueError(f"calendar {name!r} has no sessions on or after {start}")

    @property
    def name(self) -> str:
        return self._name

    @property
    def sessions(self) -> tuple[MarketSession, ...]:
        """Every session the calendar knows about, in order."""
        return self._sessions

    @property
    def first_session(self) -> MarketSession:
        return self._sessions[0]

    @property
    def last_session(self) -> MarketSession:
        return self._sessions[-1]

    def is_session(self, day: date) -> bool:
        index = bisect.bisect_left(self._dates, day)
        return index < len(self._dates) and self._dates[index] == day

    def index_of(self, day: date) -> int:
        """The session's position in the calendar, counted from the anchor.

        This integer is the generator's clock. Everything about a symbol's
        price at a session is derived from it, so it has to be a property of
        the calendar and the anchor alone - never of the request - or a
        backfill and an incremental collection would disagree about the same
        day.
        """
        index = bisect.bisect_left(self._dates, day)
        if index >= len(self._dates) or self._dates[index] != day:
            raise MarketClosed(f"{day} is not a session on the {self._name} calendar")
        return index

    def session_on(self, day: date) -> MarketSession:
        return self._sessions[self.index_of(day)]

    def session_at(self, index: int) -> MarketSession:
        if not 0 <= index < len(self._sessions):
            raise MarketClosed(f"session index {index} is outside the {self._name} calendar")
        return self._sessions[index]

    def sessions_in_range(self, start: date, end: date) -> tuple[MarketSession, ...]:
        """Every session in ``[start, end]``. Empty is a legitimate answer."""
        low = bisect.bisect_left(self._dates, start)
        high = bisect.bisect_right(self._dates, end)
        return self._sessions[low:high]

    def session_on_or_before(self, day: date) -> MarketSession:
        """The last session at or before ``day``.

        Which is how an expiry lands on a Thursday: monthly and weekly
        expiries are nominally Fridays, and a Friday holiday moves the whole
        board back a day rather than skipping the week.
        """
        index = bisect.bisect_right(self._dates, day) - 1
        if index < 0:
            raise MarketClosed(f"no {self._name} session on or before {day}")
        return self._sessions[index]

    def session_containing(self, moment: datetime) -> MarketSession:
        """The session ``moment`` falls inside, or ``MarketClosed``.

        Raising rather than returning the nearest session is the point. Phase
        3's rule is that a collection attempt on a holiday is refused rather
        than recording silence as data, and a caller that gets back "the
        closest thing we had" has been handed silence with a timestamp on it.
        """
        if moment.tzinfo is None:
            raise ValueError("moment must be timezone-aware")
        utc = moment.astimezone(UTC)
        # The session whose date is the moment's Eastern date is the only
        # candidate; look it up by UTC date and its neighbour, since a session
        # runs 13:30-20:00 UTC and never crosses a UTC midnight.
        for day in (utc.date(), utc.date() - timedelta(days=1)):
            index = bisect.bisect_left(self._dates, day)
            if index < len(self._dates) and self._dates[index] == day:
                session = self._sessions[index]
                if session.contains(utc):
                    return session
        raise MarketClosed(f"{utc.isoformat()} is not inside a {self._name} session")


@lru_cache(maxsize=8)
def get_market_calendar(name: str, start: date) -> MarketCalendar:
    """A calendar, built once per (name, anchor).

    Cached because construction reads a few thousand rows out of pandas and the
    answer cannot change for a given anchor - and because the anchor is
    configuration, the cache is bounded by how many configurations a process
    holds, which is one.

    ``start`` is passed through to ``exchange_calendars`` rather than left to
    its default. The default window is the last twenty years *from today*,
    which would make a fixed anchor fall out of range on a date nobody chose -
    a bug that arrives on a calendar day rather than on a commit.
    """
    return MarketCalendar(name, start)
