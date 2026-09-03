"""The flagship: option boards snapshotted through a session, into the lake.

docs/plans/trading.md Phase 3 calls this the phase's reason for existing.
*"It collects noise today and real chains the day Phase 8 lands, and the code
does not know the difference - which is the whole point of building it now."*
Nothing in this module names the synthetic provider, and the acceptance
criterion for the phase is that nothing in it has to change when Schwab
arrives.

Three decisions worth reading before the code:

**A snapshot outside a session is refused, not returned empty.** The collector
asks the calendar which session contains the instant and raises ``MarketClosed``
if none does. A board that is "empty because the market was shut" and a board
that is "empty because the collection failed" are the same rows and different
events, and only the refusal keeps them apart.

**A watchlist entry with no board is a gap, not a failure.** The snapshot of
the other three names still happens, and the missing one is recorded on the run
so it shows up in the collection-health gauge. The alternative - raising -
means one stale watchlist entry stops collecting everything else for the rest
of the session, which is a much larger loss than the thing it is reporting.
Phase 2 put a symbol with no options board in the default universe precisely so
this path is met here rather than on a Schwab-approval morning.

**A session's snapshots are a schedule, computed once.** ``snapshot_times`` is
pure arithmetic over a ``MarketSession``, which means the times a session
*should* have are knowable without collecting it - and that is what makes a
missing snapshot detectable at all. A collector that simply ran every thirty
minutes would have no idea how many times it was supposed to have run.
"""

from __future__ import annotations

import logging
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

from aerie_trading.collect.runs import RunLog, RunRecord, record_run, request_blob
from aerie_trading.lake.layout import normalise_symbol
from aerie_trading.lake.writer import LakeWriter
from aerie_trading.providers.base import (
    MarketClosed,
    MarketDataProvider,
    MarketSession,
)

__all__ = ["INGEST_KIND", "ChainCollector", "snapshot_times"]

logger = logging.getLogger(__name__)

#: ``ingest_run.kind`` for this collector. See ``collect/bars.py``.
INGEST_KIND = "chains"


def snapshot_times(
    session: MarketSession,
    every_minutes: int,
    *,
    include_close: bool = True,
) -> tuple[datetime, ...]:
    """When a board should be snapshotted during ``session``.

    From the open, every ``every_minutes``, up to but not including the close -
    the same half-open convention the bars use, so a snapshot and the bar that
    opens at the same instant describe the same minute.

    ``include_close`` then adds the closing board back on deliberately, because
    the half-open rule is right for tiling windows and wrong for the one
    observation every options analysis starts from: with a thirty-minute
    interval on a regular session, the last regular snapshot is at 15:30
    Eastern and the closing board is never captured at all.

    **It is the closing minute, not the closing instant** - 15:59, not 16:00 -
    and that was a defect before it was a decision. ``MarketSession.contains``
    is half-open, so a provider asked for a board *at* the close raises
    ``MarketClosed``: the schedule was asking for an instant the market was not
    open for, and every session's last snapshot was a gap. 15:59 is also the
    right answer rather than merely a working one, because it is the instant
    the session's last minute bar opens at - so the closing board and the
    closing bar describe the same minute, which is what makes an as-of join
    between them mean anything.

    An early close needs no special case: the session's own boundaries are what
    this reads.
    """
    if every_minutes <= 0:
        raise ValueError("a snapshot interval must be a positive number of minutes")

    step = timedelta(minutes=every_minutes)
    moments: list[datetime] = []
    moment = session.open
    while moment < session.close:
        moments.append(moment)
        moment += step
    if include_close:
        # De-duplicated rather than appended blindly: at a one-minute interval
        # the loop above already reached the closing minute, and a repeated
        # instant would be a second write to the same path - harmless, and a
        # snapshot counted twice in the run record.
        final = session.close - timedelta(minutes=1)
        if final >= session.open and final not in moments:
            moments.append(final)
    return tuple(sorted(moments))


@dataclass(frozen=True)
class ChainCollector:
    """Snapshots a watchlist's option boards into the lake."""

    provider: MarketDataProvider
    writer: LakeWriter
    log: RunLog

    def snapshot(self, watchlist: Sequence[str], as_of: datetime) -> RunRecord:
        """One snapshot of every name in ``watchlist``, at one instant.

        What the during-the-session CronJob calls, once per firing. The whole
        watchlist in one run rather than one run per symbol: the question the
        collection-health metrics answer is "did the 15:00 snapshot happen",
        and four rows saying "partly" is a worse answer than one row that
        names the symbol it could not reach.
        """
        moment = _as_utc(as_of)
        session = self._session_containing(moment)
        wanted = tuple(dict.fromkeys(normalise_symbol(symbol) for symbol in watchlist))
        if not wanted:
            raise ValueError("a chain collection needs at least one underlying")

        request = request_blob(
            mode="snapshot", underlyings=wanted, as_of=moment, session=session.session
        )
        with record_run(self.log, self.provider, INGEST_KIND, request) as run:
            run.detail["underlyings"] = len(wanted)
            for underlying in wanted:
                self._snapshot_one(run, underlying, moment)
        return run

    def collect_session(
        self,
        watchlist: Sequence[str],
        session: MarketSession,
        every_minutes: int,
        *,
        include_close: bool = True,
    ) -> RunRecord:
        """Every scheduled snapshot of one whole session, in one run.

        Not what the CronJob uses - the schedule belongs to Kubernetes, for the
        reasons ``deploy/cluster/trading/lake/`` writes out - but it is what
        backfills a session from a source that can answer for the past, and it
        is what the phase's gate means by "a full simulated session collected
        end to end with no gaps". One ``ingest_run`` row for the session, so
        that a session collected this way is one event in the record rather
        than thirteen.
        """
        wanted = tuple(dict.fromkeys(normalise_symbol(symbol) for symbol in watchlist))
        if not wanted:
            raise ValueError("a chain collection needs at least one underlying")

        moments = snapshot_times(session, every_minutes, include_close=include_close)
        request = request_blob(
            mode="session",
            underlyings=wanted,
            session=session.session,
            every_minutes=every_minutes,
            snapshots=len(moments),
        )
        with record_run(self.log, self.provider, INGEST_KIND, request) as run:
            run.detail["underlyings"] = len(wanted)
            run.detail["snapshots"] = len(moments)
            for moment in moments:
                for underlying in wanted:
                    self._snapshot_one(run, underlying, moment)
        return run

    # -- one board at one instant ------------------------------------------

    def _snapshot_one(self, run: RunRecord, underlying: str, moment: datetime) -> None:
        try:
            snapshot = self.provider.chain(underlying, moment)
        except KeyError:
            # The watchlist names something this source has no board for. A
            # gap rather than a failure - see the module docstring - and
            # recorded per instant, so a name that is absent all session is
            # visibly absent all session rather than mentioned once.
            run.add_gap(f"{underlying} has no option board at {moment.isoformat()}")
            return
        except MarketClosed:
            # Reachable when a provider disagrees with the calendar the
            # collector consulted - a vendor that considers a half-day closed,
            # say. Recorded rather than raised, because the disagreement is
            # about one name at one instant and the rest of the watchlist is
            # still collectable.
            run.add_gap(f"{underlying} reported no market at {moment.isoformat()}")
            return

        report = self.writer.write_chain(snapshot)
        run.rows_written += report.rows_written
        run.partitions.extend(report.partitions)
        if report.rows_written == 0:
            # A board with no contracts. Written to the lake anyway (the writer
            # explains why) and still worth flagging: an underlying whose board
            # is empty every snapshot is either delisted or misconfigured, and
            # neither announces itself.
            run.add_gap(f"{underlying} returned an empty board at {moment.isoformat()}")

    def _session_containing(self, moment: datetime) -> MarketSession:
        """The session ``moment`` is inside, or a refusal.

        Answered from ``market_hours`` rather than from a calendar object, so
        this module speaks only the provider interface. The two-day window is
        because a session is named by its Eastern date and this is asking with
        a UTC instant; asking for the day before as well costs one lookup and
        removes the entire class of off-by-one that a timezone boundary
        produces.
        """
        day = moment.date()
        for session in self.provider.market_hours(day - timedelta(days=1), day):
            if session.contains(moment):
                return session
        raise MarketClosed(
            f"{moment.isoformat()} is not inside a session; refusing to snapshot a board."
            " A chain collected while the market is shut is silence recorded as data."
        )


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        raise ValueError("as_of must be timezone-aware; got a naive datetime")
    return value.astimezone(UTC)
