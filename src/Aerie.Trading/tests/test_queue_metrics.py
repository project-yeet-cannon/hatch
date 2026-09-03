"""``/metrics`` reporting how much work is outstanding.

Phase 5 gates on *"the cluster stays responsive under a full sweep, measured
rather than assumed"*, and there is nothing to measure against unless the load
itself is a series. These assertions are about the two properties that make
such a series usable during an incident: every status appears even when it has
no rows, and a Ledger that cannot be reached says so in the response rather than
by failing the scrape.
"""

from collections.abc import Sequence

import pytest
from fastapi.testclient import TestClient
from prometheus_client import CollectorRegistry, generate_latest

from aerie_trading.control.app import create_app
from aerie_trading.control.metrics import build_registry, render_metrics
from aerie_trading.control.queue_depth import QueueDepthCollector
from aerie_trading.db.models import RunStatus
from aerie_trading.runs.queue import QueueDepth
from aerie_trading.settings import Settings
from tests.conftest import STAMPED, StubDatabase


class StubQueue:
    """A ``QueueSource``, which is one method - see that Protocol's docstring."""

    def __init__(self, depth: Sequence[QueueDepth] = (), fail: bool = False) -> None:
        self.depth = depth
        self.fail = fail

    def queue_depth(self) -> Sequence[QueueDepth]:
        if self.fail:
            raise ConnectionError("the Ledger is not answering")
        return self.depth


def scrape(source: StubQueue) -> str:
    """The exposition body for a registry holding only this collector.

    Rendered through ``generate_latest`` rather than by walking the samples,
    because the thing under test is what Prometheus sees and the escaping is
    part of that.
    """
    registry = CollectorRegistry()
    registry.register(QueueDepthCollector(source))
    return generate_latest(registry).decode("utf-8")


def test_every_status_is_reported_even_when_it_has_no_runs() -> None:
    # An absent series and a zero look identical on a graph and completely
    # different to an alert with absent() in it, so a queue that has just
    # drained reports zero rather than dropping the series.
    body = scrape(StubQueue([QueueDepth(status=RunStatus.QUEUED.value, runs=42)]))

    assert 'trading_runs{status="queued"} 42.0' in body
    for status in RunStatus:
        assert f'trading_runs{{status="{status.value}"}}' in body


def test_a_reachable_ledger_reports_up() -> None:
    assert "trading_runs_up 1.0" in scrape(StubQueue([]))


def test_an_unreachable_ledger_reports_down_rather_than_failing_the_scrape() -> None:
    # A scrape that raised would be recorded by Prometheus as a failed target,
    # which looks exactly like the pod being down - and would fire every alert
    # about queue depth at once, all of them saying the wrong thing.
    body = scrape(StubQueue(fail=True))

    assert "trading_runs_up 0.0" in body
    assert "trading_runs{" not in body


@pytest.fixture
def client() -> TestClient:
    settings = Settings(_env_file=None)  # pyright: ignore[reportCallIssue]
    database = StubDatabase(depth=[QueueDepth(status=RunStatus.RUNNING.value, runs=3)])
    return TestClient(create_app(settings=settings, database=database, revision=STAMPED))


def test_the_metrics_endpoint_serves_the_queue_series(client: TestClient) -> None:
    body = client.get("/metrics").text

    assert "trading_runs_up 1.0" in body
    assert 'trading_runs{status="running"} 3.0' in body
    # And the Phase 3 series are still there - one registry, both collectors.
    assert "trading_collection_up 1.0" in body


def test_a_registry_built_without_a_ledger_still_reports_the_build() -> None:
    body = render_metrics(build_registry(STAMPED)).decode("utf-8")

    assert "trading_build_info" in body
    assert "trading_runs_up" not in body
