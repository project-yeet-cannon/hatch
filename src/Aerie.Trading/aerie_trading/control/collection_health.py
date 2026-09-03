"""Collection health as Prometheus series, derived at scrape time.

The exposition half of ``db/health.py`` - that module explains why collection
health is a query against the Ledger rather than a counter in a collector, and
this one turns its rows into metrics.

A custom collector rather than a set of gauges the app updates, because the
values do not change when anything in *this* process happens: they change when
a CronJob somewhere else writes a row. A gauge would have to be refreshed on a
timer, which is a second schedule to reason about and a window in which
``/metrics`` reports a number that was true a minute ago. Implementing
``collect()`` means the numbers are read when they are asked for, which is what
a scrape is.

**Every failure produces ``trading_collection_up 0`` and nothing else.** A
scrape that raises is a scrape Prometheus records as a failed target, which
looks identical to the pod being down - so the endpoint answers, says it could
not reach the Ledger, and lets the alert on *that* fire rather than the alerts
on collection staleness, which would otherwise all fire at once and say the
wrong thing.
"""

from __future__ import annotations

import logging
from collections.abc import Iterable, Sequence
from typing import Protocol

from prometheus_client.core import CounterMetricFamily, GaugeMetricFamily
from prometheus_client.registry import Collector

from aerie_trading.db.health import CollectorHealth

__all__ = ["CollectionHealthCollector", "HealthSource"]

logger = logging.getLogger(__name__)

_LABELS = ("source", "kind")


class HealthSource(Protocol):
    """What this collector needs: something that can answer the health query.

    Narrower than ``db.Database`` on purpose, and a Protocol rather than that
    class so the dependency points the right way: this module is the exposition
    layer and has no business being able to dispose a connection pool. A test
    for it implements one method.
    """

    def collection_health(self) -> Sequence[CollectorHealth]: ...


class CollectionHealthCollector(Collector):
    """``trading_collection_*``, read out of the Ledger on every scrape."""

    def __init__(self, source: HealthSource) -> None:
        self._source = source

    def collect(self) -> Iterable[GaugeMetricFamily | CounterMetricFamily]:
        up = GaugeMetricFamily(
            "trading_collection_up",
            "1 when this scrape could read collection health from the Ledger, 0 otherwise.",
        )
        try:
            rows = list(self._source.collection_health())
        except Exception:
            # Bare, and deliberately: a driver error, a DNS failure and a
            # refused connection are all "could not read", and enumerating them
            # would produce an unhandled exception - which is to say a failed
            # scrape - for the one nobody listed. Same argument as `/readyz`.
            logger.warning(
                "Collection health unavailable: the Ledger did not answer", exc_info=True
            )
            up.add_metric((), 0)
            yield up
            return

        up.add_metric((), 1)
        yield up

        last_success = GaugeMetricFamily(
            "trading_collection_last_success_timestamp_seconds",
            "When this collector last completed a run successfully.",
            labels=_LABELS,
        )
        last_attempt = GaugeMetricFamily(
            "trading_collection_last_attempt_timestamp_seconds",
            "When this collector last started a run, successful or not.",
            labels=_LABELS,
        )
        # The series the plan's "a session that collected nothing" alert reads.
        # A successful run that wrote no rows is invisible in every other
        # series here: the Job is green and the last-success timestamp is
        # current.
        last_success_rows = GaugeMetricFamily(
            "trading_collection_last_success_rows",
            "Rows the most recent successful run of this collector wrote.",
            labels=_LABELS,
        )
        gaps = GaugeMetricFamily(
            "trading_collection_last_success_gaps",
            "Gaps the most recent successful run of this collector detected.",
            labels=_LABELS,
        )
        running = GaugeMetricFamily(
            "trading_collection_runs_running",
            "Runs still marked running - a collector that started and never finished.",
            labels=_LABELS,
        )
        rows_total = CounterMetricFamily(
            "trading_collection_rows_written",
            "Rows written by this collector inside the health window.",
            labels=_LABELS,
        )
        runs_total = CounterMetricFamily(
            "trading_collection_runs",
            "Runs by this collector inside the health window, by outcome.",
            labels=(*_LABELS, "status"),
        )

        for row in rows:
            labels = (row.source, row.kind)
            if row.last_success is not None:
                last_success.add_metric(labels, row.last_success.timestamp())
            if row.last_attempt is not None:
                last_attempt.add_metric(labels, row.last_attempt.timestamp())
            if row.last_success_rows is not None:
                last_success_rows.add_metric(labels, row.last_success_rows)
            if row.last_gap_count is not None:
                gaps.add_metric(labels, row.last_gap_count)
            running.add_metric(labels, row.runs_running)
            rows_total.add_metric(labels, row.rows_written)
            runs_total.add_metric((*labels, "succeeded"), row.runs_succeeded)
            runs_total.add_metric((*labels, "failed"), row.runs_failed)

        yield last_success
        yield last_attempt
        yield last_success_rows
        yield gaps
        yield running
        yield rows_total
        yield runs_total
