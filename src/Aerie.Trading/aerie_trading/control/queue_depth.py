"""Queue depth as Prometheus series, derived at scrape time.

docs/plans/trading.md Phase 5 chooses Postgres for the queue partly because
*"queue depth becomes rows the control panel already reads"*, and gates on
*"the cluster stays responsive under a full sweep, measured rather than
assumed"*. This is what does the measuring: without a series for how much work
is outstanding, "responsive under load" is a claim about a load nobody
recorded.

It is the same shape as ``control/collection_health.py`` and for the same
reasons - a custom collector rather than gauges the app updates, because the
numbers change when a *worker* writes a row rather than when anything happens
in this process; and a failure that yields ``trading_runs_up 0`` rather than
raising, because a scrape that raises is indistinguishable from a pod that is
down, and the alert that should fire is the one about the database.

**A gauge per status rather than one per run.** Ten thousand queued runs are
five numbers here, not ten thousand series. The identity of an individual run
is a question for the control panel, which reads the table; Prometheus is being
asked how much work there is.
"""

from __future__ import annotations

import logging
from collections.abc import Iterable, Sequence
from typing import Protocol

from prometheus_client.core import GaugeMetricFamily
from prometheus_client.registry import Collector

from aerie_trading.db.models import RunStatus
from aerie_trading.runs.queue import QueueDepth

__all__ = ["QueueDepthCollector", "QueueSource"]

logger = logging.getLogger(__name__)


class QueueSource(Protocol):
    """What this collector needs. Narrower than ``db.Database``, deliberately.

    The exposition layer has no business being able to dispose a connection
    pool, and a Protocol with one method is what a test implements.
    """

    def queue_depth(self) -> Sequence[QueueDepth]: ...


class QueueDepthCollector(Collector):
    """``trading_runs_*``, read out of the Ledger on every scrape."""

    def __init__(self, source: QueueSource) -> None:
        self._source = source

    def collect(self) -> Iterable[GaugeMetricFamily]:
        up = GaugeMetricFamily(
            "trading_runs_up",
            "1 when this scrape could read the run queue from the Ledger, 0 otherwise.",
        )
        try:
            rows = list(self._source.queue_depth())
        except Exception:
            # Bare, for the reason `/readyz` and the collection-health
            # collector both give: every way the Ledger can fail to answer is
            # the same answer, and enumerating them produces a failed scrape
            # for the one nobody listed.
            logger.warning("Queue depth unavailable: the Ledger did not answer", exc_info=True)
            up.add_metric((), 0)
            yield up
            return

        up.add_metric((), 1)
        yield up

        depth = GaugeMetricFamily(
            "trading_runs",
            "Runs in the Ledger, by status. Queued plus running is the outstanding work.",
            labels=("status",),
        )
        # Every status, including the ones with no rows. A queue that has just
        # drained should report `trading_runs{status="queued"} 0` rather than
        # dropping the series - an absent series and a zero look identical on a
        # graph and completely different to an alert with `absent()` in it.
        counts = {row.status: row.runs for row in rows}
        for status in RunStatus:
            depth.add_metric((status.value,), counts.get(status.value, 0))
        yield depth
