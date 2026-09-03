"""One ``ingest_run`` row per collection, written whether or not it worked.

This is the record everything downstream reads. ``control/collection_health.py``
turns these rows into the ``/metrics`` series the plan gates on - *"rows
written, gaps detected, last successful run per collector"* - and it does so by
querying the Ledger rather than by asking the collectors, because the
collectors are CronJob pods that are gone by the time Prometheus scrapes.

**A widening of what Phase 1 meant by this table, stated rather than
discovered.** ``db/models.py`` describes ``ingest_run`` as *"one call out to a
provider"*. A row here is one **collection run** - one invocation of one
collector - which may make many provider calls. The alternative was priced: a
chain snapshot of a four-name watchlist is four provider calls, thirteen times
a session, five days a week, which is a thousand rows a month to record
something nobody asks a question at that granularity about. What is asked is
"did the collector run, and did it produce anything", and that is one row. The
table's own comment has been updated to say so.

**The row is inserted before the work and committed immediately.** A collector
killed mid-run therefore leaves a ``running`` row that never became anything,
which is the only durable evidence that a run started and did not finish - and
is what lets an operator tell a crashed collection from one that was never
scheduled. The alternative, writing the row at the end, records exactly the
runs that did not need recording.
"""

from __future__ import annotations

import logging
from collections.abc import Generator, Mapping, Sequence
from contextlib import contextmanager
from dataclasses import dataclass, field
from datetime import UTC, date, datetime
from enum import Enum
from typing import Protocol, cast

from sqlalchemy import update
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session

from aerie_trading.db.models import IngestRun, IngestStatus
from aerie_trading.providers.base import MarketDataProvider
from aerie_trading.providers.registry import ensure_data_source

__all__ = ["LedgerRunLog", "RunLog", "RunRecord", "record_run", "request_blob"]

logger = logging.getLogger(__name__)


@dataclass
class RunRecord:
    """The mutable tally a collection fills in as it goes.

    Mutable and handed to the body of a ``with`` block, rather than returned
    from the collector and written afterwards, so that a run that raises
    halfway still records what it managed to write. A collection that wrote
    eleven partitions and then failed is a different event from one that failed
    immediately, and only one of them needs the lake inspecting.
    """

    kind: str
    request: Mapping[str, object]
    started_at: datetime
    rows_written: int = 0
    partitions: list[str] = field(default_factory=list[str])
    #: What the run expected to find and did not - a session with no bars, a
    #: watchlist entry with no board. Strings rather than a structured type
    #: because they are read by a person looking at one row, and because what
    #: counts as a gap differs per collector.
    gaps: list[str] = field(default_factory=list[str])
    #: Anything else worth keeping about the outcome. Lands in
    #: ``ingest_run.result`` beside the counts.
    detail: dict[str, object] = field(default_factory=dict[str, object])

    def add_gap(self, description: str) -> None:
        logger.warning("Collection gap: %s", description, extra={"Kind": self.kind})
        self.gaps.append(description)

    def result_blob(self) -> dict[str, object]:
        return {"partitions": list(self.partitions), "gaps": list(self.gaps), **self.detail}


class RunLog(Protocol):
    """Where a run record goes.

    A Protocol for the reason ``db.Database`` is one: the tests for these
    collectors run in CI with no Postgres, and the behaviour under test -
    "a run that raised is still recorded, with its error" - is a failure path
    that a suite unable to fake the log cannot reach at all.
    """

    def begin(self, provider: MarketDataProvider, record: RunRecord) -> int:
        """Record a started run and return its id."""
        ...

    def finish(self, run_id: int, record: RunRecord, error: str | None) -> None:
        """Record how it ended."""
        ...


@contextmanager
def record_run(
    log: RunLog,
    provider: MarketDataProvider,
    kind: str,
    request: Mapping[str, object],
) -> Generator[RunRecord]:
    """Record one collection run around the block that performs it.

    Re-raises whatever the body raised, after recording it. A collector that
    swallowed its own failure in order to leave a tidy record would be the
    single worst thing this module could do: the CronJob would exit zero, the
    Job would report success, and ``kube_cronjob_status_last_successful_time``
    would keep advancing over a collector that has not collected anything for a
    week.
    """
    record = RunRecord(kind=kind, request=dict(request), started_at=datetime.now(UTC))
    run_id = log.begin(provider, record)
    try:
        yield record
    except BaseException as failure:
        # str() rather than a traceback: the traceback is already in the pod's
        # logs with far more context than a text column can hold, and this
        # field is read in a list of runs where one line per row is the point.
        log.finish(run_id, record, error=f"{type(failure).__name__}: {failure}")
        raise
    log.finish(run_id, record, error=None)


class LedgerRunLog:
    """The real one: rows in ``ingest_run``, in the trading database.

    Holds an ``Engine`` and opens a short session per call rather than holding
    one open across the run. A collection can take minutes - a backfill takes
    longer - and a transaction held open for its duration is a transaction
    holding a connection, blocking a CNPG failover's connection drain, and
    pinning the ``running`` row inside an uncommitted transaction where nothing
    else can see it. Which would defeat the whole point of writing it early.
    """

    def __init__(self, engine: Engine) -> None:
        self._engine = engine

    def begin(self, provider: MarketDataProvider, record: RunRecord) -> int:
        from aerie_trading.revision import read_revision

        with Session(self._engine) as session, session.begin():
            # Registration and the run it is for, in one transaction - the
            # ordering `ensure_data_source` documents. Every collector process
            # registers at startup, which is why that function is idempotent by
            # name rather than an insert.
            source = ensure_data_source(session, provider)
            session.flush()
            run = IngestRun(
                data_source_id=source.id,
                kind=record.kind,
                request=dict(record.request),
                status=IngestStatus.RUNNING.value,
                started_at=record.started_at,
                rows_written=0,
                aerie_revision=read_revision().revision,
            )
            session.add(run)
            session.flush()
            return run.id

    def finish(self, run_id: int, record: RunRecord, error: str | None) -> None:
        finished = datetime.now(UTC)
        with Session(self._engine) as session, session.begin():
            session.execute(
                update(IngestRun)
                .where(IngestRun.id == run_id)
                .values(
                    status=(IngestStatus.FAILED.value if error else IngestStatus.SUCCEEDED.value),
                    finished_at=finished,
                    duration_ms=int((finished - record.started_at).total_seconds() * 1000),
                    rows_written=record.rows_written,
                    gap_count=len(record.gaps),
                    result=record.result_blob(),
                    error=error,
                )
            )


def request_blob(**values: object) -> dict[str, object]:
    """A JSON-safe ``request`` blob, with the values a collector was given.

    Dates and datetimes become ISO strings and enumerations become their
    values, because this ends up in JSONB and the point of the column is that a
    person can read it in ``psql`` a year later and reconstruct the call.
    """
    return {key: _jsonable(value) for key, value in values.items()}


def _jsonable(value: object) -> object:
    """One value, in a form JSONB can hold and a person can read.

    Explicit about the four types a collector actually passes rather than
    duck-typed: an ``isoformat`` attribute check would also catch a
    ``time``, and a ``.value`` check would unwrap things that merely have
    one. A type this does not recognise is passed through, so psycopg's own
    JSON adapter is what refuses it - one error, at the point of the write,
    naming the column.
    """
    if isinstance(value, datetime):
        return value.astimezone(UTC).isoformat()
    if isinstance(value, date):
        return value.isoformat()
    if isinstance(value, Enum):
        return _jsonable(value.value)
    if isinstance(value, (list, tuple)):
        # The cast is what tells a strict checker that a sequence of unknowns
        # is a sequence of objects. `object` is already the widest thing this
        # function accepts, so it narrows nothing and asserts nothing false.
        return [_jsonable(entry) for entry in cast("Sequence[object]", value)]
    return value
