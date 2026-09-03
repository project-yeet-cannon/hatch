"""Bars into the lake: a backfill mode and an incremental one, sharing a body.

docs/plans/trading.md Phase 3 asks for *"daily and minute bars: a backfill mode
for whatever depth the configured provider offers, and an incremental mode
after each close."* They are the same method with different arguments here, and
that is the design rather than an economy: the property the phase's gate tests -
that a re-run leaves no duplicate rows - is only obviously true if the two paths
that could disagree are one path.

**The calendar is consulted before anything is collected**, which is the rule
the plan states as *"do not collect through a holiday and record silence as
data"*. Two things follow from taking it seriously:

- A request naming a specific day that is not a session raises ``MarketClosed``
  rather than returning an empty result. The incremental collector is invoked
  by a CronJob whose schedule is "weekdays after the close", which fires on
  Thanksgiving; the refusal is what turns that into a Job that visibly did not
  collect rather than a successful run that wrote nothing.
- A *range* with no sessions in it is not an error - a backfill over a week
  that is entirely holiday is a legitimate request with a legitimate empty
  answer - but a session inside the range that produced no bars **is a gap**,
  and is recorded as one. That distinction is the difference between "there was
  no market" and "there was a market and we have nothing from it".

The provider is asked for **one month of one symbol at a time**, which is the
lake's own partition granularity. That is not an arbitrary batch size: it means
one provider call fills exactly one file, so a run interrupted between calls
leaves whole partitions behind rather than partial ones, and a re-run repeats
one call rather than resuming inside one.
"""

from __future__ import annotations

import logging
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import UTC, date, datetime, time, timedelta

from aerie_trading.collect.runs import RunLog, RunRecord, record_run, request_blob
from aerie_trading.lake.layout import normalise_symbol
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import (
    Bar,
    Interval,
    MarketClosed,
    MarketDataProvider,
    MarketSession,
)

__all__ = ["INGEST_KIND", "BarCollector"]

logger = logging.getLogger(__name__)

#: The ``ingest_run.kind`` every run of this collector records under. A
#: constant because the metrics query and the alert both select on it, and a
#: typo in one of the three would read as a collector that has never run.
INGEST_KIND = "bars"


@dataclass(frozen=True)
class BarCollector:
    """Collects OHLCV bars for a watchlist into the lake."""

    provider: MarketDataProvider
    writer: LakeWriter
    log: RunLog

    def backfill(
        self,
        symbols: Sequence[str],
        interval: Interval,
        start: date,
        end: date,
    ) -> RunRecord:
        """Every session in ``[start, end]``, however many months that spans.

        Inclusive at both ends, unlike every *instant* range in this codebase,
        because these are calendar dates chosen by a person: "backfill 2 January
        to 31 March" that stopped on 30 March would be a surprise, and the
        half-open convention exists to make consecutive *windows* tile, which
        dates a human typed do not need to do.
        """
        if end < start:
            raise ValueError("end is before start")
        sessions = self.provider.market_hours(start, end)
        return self._collect(
            symbols,
            interval,
            sessions,
            request_blob(mode="backfill", interval=interval, symbols=symbols, start=start, end=end),
        )

    def incremental(
        self,
        symbols: Sequence[str],
        interval: Interval,
        session: date,
    ) -> RunRecord:
        """One session, refusing a day the market was not open.

        The refusal is the point of the method existing separately from
        ``backfill`` over a one-day range: a range with no sessions is empty,
        and a *named day* that is not a session is a mistake worth raising.
        """
        sessions = self.provider.market_hours(session, session)
        if not sessions:
            raise MarketClosed(
                f"{session} is not a session; refusing to collect bars for it."
                " An empty collection on a holiday is silence recorded as data."
            )
        return self._collect(
            symbols,
            interval,
            sessions,
            request_blob(mode="incremental", interval=interval, symbols=symbols, session=session),
        )

    def latest_session(self, symbols: Sequence[str], interval: Interval, *, on: date) -> RunRecord:
        """The most recent session at or before ``on``.

        What the after-the-close CronJob actually calls. It takes the day
        rather than deriving it from ``date.today()`` so that the decision
        "which day is this run for" is made once, in one place, by an argument
        a test can supply - a collector that reads the wall clock in the middle
        of its own logic is one whose behaviour on a holiday cannot be tested
        without pretending to be a different day.
        """
        # 30 days back is more than the longest run of consecutive non-sessions
        # any exchange calendar produces (a four-day weekend is the record for
        # XNYS), with enough margin that the query is answered on the first
        # attempt rather than in a loop.
        sessions = self.provider.market_hours(on - timedelta(days=30), on)
        if not sessions:
            raise MarketClosed(f"no session on or before {on} in the last 30 days")
        return self.incremental(symbols, interval, sessions[-1].session)

    # -- the one body ------------------------------------------------------

    def _collect(
        self,
        symbols: Sequence[str],
        interval: Interval,
        sessions: Sequence[MarketSession],
        request: dict[str, object],
    ) -> RunRecord:
        wanted = tuple(dict.fromkeys(normalise_symbol(symbol) for symbol in symbols))
        if not wanted:
            raise ValueError("a bar collection needs at least one symbol")

        with record_run(self.log, self.provider, INGEST_KIND, request) as run:
            run.detail["sessions"] = len(sessions)
            if not sessions:
                # Not a gap. See the module docstring: a range containing no
                # sessions is a legitimate request with a legitimate empty
                # answer, and calling it a gap would fire the collection-health
                # alert every Christmas week.
                logger.info(
                    "No sessions in the requested range; nothing to collect",
                    extra={"Interval": interval.value},
                )
                return run

            for symbol in wanted:
                for group in _by_month(sessions):
                    self._collect_month(run, symbol, interval, group)
        return run

    def _collect_month(
        self,
        run: RunRecord,
        symbol: str,
        interval: Interval,
        sessions: Sequence[MarketSession],
    ) -> None:
        """One symbol, one month: one provider call and one partition write."""
        window_start = sessions[0].open
        # The instant after the last session's close, because the provider's
        # window is half-open and the closing bar has to be inside it. Adding a
        # minute rather than using the close itself is the difference between
        # 390 bars and 389, and the missing one is the most-read bar of the day.
        window_end = sessions[-1].close + timedelta(minutes=1)

        bars = self.provider.bars((symbol,), interval, window_start, window_end)
        report = self.writer.write_bars(bars)
        run.rows_written += report.rows_written
        run.partitions.extend(report.partitions)

        collected = _sessions_with_bars(bars)
        for session in sessions:
            if session.session not in collected:
                run.add_gap(f"{symbol} {interval.value} {session.session.isoformat()}")


def _by_month(sessions: Sequence[MarketSession]) -> list[list[MarketSession]]:
    """Sessions grouped into the lake's monthly partitions, in order.

    Grouped by the **UTC** month of the session's open, matching
    ``layout.BarPartition.containing``. Those agree for every US equity session
    - one runs 13:30-21:00 UTC and never crosses a UTC midnight - and grouping
    by the session *date* instead would be a second rule that agrees today and
    is a silent mis-file on the first venue that does not have that property.
    """
    grouped: dict[tuple[int, int], list[MarketSession]] = {}
    for session in sessions:
        opened = session.open.astimezone(UTC)
        grouped.setdefault((opened.year, opened.month), []).append(session)
    return [grouped[key] for key in sorted(grouped)]


def _sessions_with_bars(bars: Sequence[Bar]) -> set[date]:
    """Which UTC dates the returned bars actually cover."""
    return {bar.timestamp.astimezone(UTC).date() for bar in bars}


def session_end_of_day(day: date) -> datetime:
    """Midnight UTC after ``day`` - the exclusive end of a whole-day window.

    A named helper rather than an inline expression because it is the boundary
    every whole-day read in the suite uses, and "is this inclusive" is the
    question it exists to have one answer to.
    """
    return datetime.combine(day + timedelta(days=1), time.min, tzinfo=UTC)
