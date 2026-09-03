"""Collection health on ``/metrics``, and what it says when the Ledger is down.

Phase 3 gates on *"Collection health is visible: rows written, gaps detected,
last successful run per collector, all on /metrics"*. The series are derived at
scrape time from ``ingest_run`` (see ``aerie_trading/db/health.py``), which
makes the interesting test the one where the derivation fails: an endpoint that
raised would look identical to the pod being down, and every staleness alert
would fire at once saying the wrong thing.
"""

from datetime import UTC, datetime

from conftest import STAMPED, StubDatabase
from fastapi.testclient import TestClient
from prometheus_client import CollectorRegistry

from aerie_trading.control.app import create_app
from aerie_trading.control.collection_health import CollectionHealthCollector
from aerie_trading.control.metrics import render_metrics
from aerie_trading.db.health import CollectorHealth
from aerie_trading.settings import Settings

LAST_SUCCESS = datetime(2026, 3, 4, 21, 5, tzinfo=UTC)

HEALTHY = (
    CollectorHealth(
        source="synthetic",
        kind="bars",
        last_success=LAST_SUCCESS,
        last_attempt=LAST_SUCCESS,
        last_success_rows=1950,
        last_gap_count=0,
        rows_written=98_000,
        runs_succeeded=42,
        runs_failed=1,
        runs_running=0,
    ),
    CollectorHealth(
        source="synthetic",
        kind="chains",
        last_success=LAST_SUCCESS,
        last_attempt=LAST_SUCCESS,
        last_success_rows=0,
        last_gap_count=3,
        rows_written=0,
        runs_succeeded=13,
        runs_failed=0,
        runs_running=1,
    ),
)


def render(source: object) -> str:
    registry = CollectorRegistry()
    registry.register(CollectionHealthCollector(source))  # type: ignore[arg-type]
    return render_metrics(registry).decode()


def test_the_series_the_plan_asks_for_are_all_present() -> None:
    body = render(StubDatabase(health=HEALTHY))

    assert (
        'trading_collection_last_success_timestamp_seconds{kind="bars",source="synthetic"}' in body
    )
    assert 'trading_collection_rows_written_total{kind="bars",source="synthetic"} 98000.0' in body
    assert 'trading_collection_last_success_gaps{kind="chains",source="synthetic"} 3.0' in body
    assert (
        'trading_collection_runs_total{kind="bars",source="synthetic",status="failed"} 1.0' in body
    )
    assert "trading_collection_up 1.0" in body


def test_a_successful_run_that_wrote_nothing_is_visible() -> None:
    """The plan's "a session that collected nothing" alert reads this series.

    It is invisible in every other one: the Job exited zero, the last-success
    timestamp is current, and the lake gained nothing.
    """
    body = render(StubDatabase(health=HEALTHY))
    assert 'trading_collection_last_success_rows{kind="chains",source="synthetic"} 0.0' in body


def test_a_run_that_started_and_never_finished_is_visible() -> None:
    """The ``running`` row a killed collector leaves behind - the only durable
    evidence that a run began and did not end."""
    body = render(StubDatabase(health=HEALTHY))
    assert 'trading_collection_runs_running{kind="chains",source="synthetic"} 1.0' in body


def test_an_unreachable_ledger_reports_up_zero_rather_than_failing_the_scrape() -> None:
    body = render(StubDatabase(healthy=False))

    assert "trading_collection_up 0.0" in body
    # And nothing else, so that a staleness alert cannot fire on a series that
    # was not read - `absent()` is what covers this case, and `up == 0` is what
    # says which of the two it is.
    assert "trading_collection_last_success_timestamp_seconds" not in body


def test_a_silo_that_has_never_collected_anything_is_up_with_no_series() -> None:
    """Legitimately empty, and distinguishable from the case above.

    The metric families are still *declared* - prometheus_client emits HELP and
    TYPE for a family with no samples - and that is the right shape: a
    declared-but-empty series is what makes ``absent()`` in the alert rules
    mean "nothing has ever collected" rather than "the exporter is a different
    version".
    """
    body = render(StubDatabase(health=()))
    assert "trading_collection_up 1.0" in body
    samples = [line for line in body.splitlines() if not line.startswith("#")]
    assert samples == ["trading_collection_up 1.0"]


def test_the_metrics_endpoint_serves_collection_health(
    stub_database: StubDatabase,
) -> None:
    """End to end: the app registers the collector against its own database."""
    stub_database.health = HEALTHY
    app = create_app(Settings(), stub_database, STAMPED)
    with TestClient(app) as client:
        response = client.get("/metrics")

    assert response.status_code == 200
    assert "trading_collection_up 1.0" in response.text
    assert 'trading_collection_last_success_rows{kind="bars",source="synthetic"} 1950.0' in (
        response.text
    )
    assert stub_database.health_reads == 1


def test_the_metrics_endpoint_still_answers_when_the_ledger_is_down() -> None:
    """A 500 here would read as "the pod is broken" rather than "the database
    is", which is the distinction the whole ``up`` gauge exists to draw."""
    app = create_app(Settings(), StubDatabase(healthy=False), STAMPED)
    with TestClient(app) as client:
        response = client.get("/metrics")

    assert response.status_code == 200
    assert "trading_collection_up 0.0" in response.text
    # The build identity is still reported: it does not come from the database.
    assert "trading_build_info" in response.text
