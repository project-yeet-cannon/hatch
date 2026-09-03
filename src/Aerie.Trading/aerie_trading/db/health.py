"""Collection health, read out of the Ledger.

docs/plans/trading.md Phase 3 gates on *"Collection health is visible: rows
written, gaps detected, last successful run per collector, all on /metrics,
with an alert for a session that collected nothing."*

**Why this is a query and not a counter.** The obvious implementation is a
Prometheus counter the collectors increment. It cannot work here: a collector
is a CronJob pod that runs for thirty seconds and exits, so there is nothing
alive for Prometheus to scrape at scrape time, and a push gateway would be a
second piece of infrastructure to run and to reason about staleness in. The
Ledger already has a durable row per run (``collect/runs.py``), the control
plane is already scraped, and the join between them is this file. Nothing new
is deployed to make collection health visible.

The consequence worth stating: these series are **derived at scrape time**, so
they are exactly as available as the trading database. That is why
``control/collection_health.py`` emits an ``up`` gauge beside them - a scrape
that could not reach the Ledger must not look like a collector that stopped
collecting.

The query is one pass over ``ingest_run`` grouped by source and kind, over a
bounded window. Bounded because this table grows forever and a scrape every
thirty seconds must not get slower every month; ninety days is far longer than
any alert threshold here and short enough that the index
``ix_ingest_run_source_kind_started`` covers it.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from datetime import datetime

from sqlalchemy import text
from sqlalchemy.engine import Engine

__all__ = ["HEALTH_WINDOW_DAYS", "CollectorHealth", "read_collection_health"]

#: How far back the health query looks. See the module docstring.
HEALTH_WINDOW_DAYS = 90


@dataclass(frozen=True)
class CollectorHealth:
    """What one collector, on one data source, has been doing.

    ``last_success_rows`` is the field the plan's "a session that collected
    nothing" alert is actually built on. A run that *succeeded* and wrote zero
    rows is the failure that is invisible in every other column: the CronJob
    exited zero, the Job history is green, the last-success timestamp is
    current, and the lake gained nothing.
    """

    source: str
    kind: str
    last_success: datetime | None
    last_attempt: datetime | None
    last_success_rows: int | None
    last_gap_count: int | None
    rows_written: int
    runs_succeeded: int
    runs_failed: int
    runs_running: int


# One statement rather than several, because a scrape that issued four queries
# could see a run start between two of them and report a state that never
# existed. `DISTINCT ON` is Postgres' own idiom for "the newest row per group"
# and is why the last-success columns can come from the same pass as the
# aggregates rather than from a correlated subquery per group.
_HEALTH_SQL = text(
    """
    WITH window_runs AS (
        SELECT r.data_source_id, r.kind, r.status, r.started_at,
               r.rows_written, r.gap_count, s.name AS source
        FROM ingest_run r
        JOIN data_source s ON s.id = r.data_source_id
        WHERE r.started_at >= now() - make_interval(days => :days)
    ),
    latest_success AS (
        SELECT DISTINCT ON (data_source_id, kind)
               data_source_id, kind, started_at, rows_written, gap_count
        FROM window_runs
        WHERE status = 'succeeded'
        ORDER BY data_source_id, kind, started_at DESC
    )
    SELECT w.source,
           w.kind,
           MAX(l.started_at)                                     AS last_success,
           MAX(w.started_at)                                     AS last_attempt,
           MAX(l.rows_written)                                   AS last_success_rows,
           MAX(l.gap_count)                                      AS last_gap_count,
           COALESCE(SUM(w.rows_written), 0)                      AS rows_written,
           COUNT(*) FILTER (WHERE w.status = 'succeeded')        AS runs_succeeded,
           COUNT(*) FILTER (WHERE w.status = 'failed')           AS runs_failed,
           COUNT(*) FILTER (WHERE w.status = 'running')          AS runs_running
    FROM window_runs w
    LEFT JOIN latest_success l
           ON l.data_source_id = w.data_source_id AND l.kind = w.kind
    GROUP BY w.source, w.kind
    ORDER BY w.source, w.kind
    """
)


def read_collection_health(
    engine: Engine, *, days: int = HEALTH_WINDOW_DAYS
) -> Sequence[CollectorHealth]:
    """One row per (source, collector). Raises if the Ledger does not answer.

    Raising rather than returning an empty sequence is deliberate and is the
    whole reason the caller can distinguish the two states it must not confuse:
    "no collector has ever run" is legitimately empty, and "the database is
    down" is an exception. A function that flattened both into ``[]`` would
    make an unreachable Ledger indistinguishable from a silo that has never
    collected anything, on the one dashboard whose job is to tell them apart.
    """
    with engine.connect() as connection:
        rows = connection.execute(_HEALTH_SQL, {"days": days}).mappings().all()

    return [
        CollectorHealth(
            source=str(row["source"]),
            kind=str(row["kind"]),
            last_success=row["last_success"],
            last_attempt=row["last_attempt"],
            last_success_rows=row["last_success_rows"],
            last_gap_count=row["last_gap_count"],
            rows_written=int(row["rows_written"]),
            runs_succeeded=int(row["runs_succeeded"]),
            runs_failed=int(row["runs_failed"]),
            runs_running=int(row["runs_running"]),
        )
        for row in rows
    ]
