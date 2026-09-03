"""The four endpoints, and the platform contract each of them stands for."""

from pathlib import Path

from fastapi.testclient import TestClient

from aerie_trading.control.app import REVISION_HEADER, create_app
from aerie_trading.settings import Settings
from tests.conftest import STAMPED, StubDatabase

#: A bundle location with nothing in it, so that these tests describe the API
#: alone. The control panel's bundle is build output (`control/spa.py`), and
#: without this the answers below would depend on whether whoever ran the suite
#: had also run `npm run build -w apps/trading` - the SPA's catch-all serves
#: index.html for any path no route claimed, which is the right behaviour and
#: is asserted in tests/test_control_spa.py.
NO_BUNDLE = Path(__file__).resolve().parent / "no-such-bundle"


def client(database: StubDatabase | None = None, **settings: object) -> TestClient:
    return TestClient(
        create_app(
            settings=Settings(**settings),  # pyright: ignore[reportArgumentType]
            database=database if database is not None else StubDatabase(),
            revision=STAMPED,
            static_root=NO_BUNDLE,
        )
    )


def test_liveness_never_touches_the_database() -> None:
    # The distinction this asserts is the one that decides whether a database
    # failover restarts every replica at once: liveness must answer for the
    # process alone.
    database = StubDatabase(healthy=False)

    response = client(database).get("/healthz")

    assert response.status_code == 200
    assert response.json() == {"status": "ok"}
    assert database.checks == 0


def test_readiness_asks_the_database() -> None:
    database = StubDatabase()

    response = client(database).get("/readyz")

    assert response.status_code == 200
    assert response.json()["database"] == "ok"
    assert database.checks == 1


def test_readiness_reports_degraded_when_the_ledger_is_unreachable() -> None:
    # 503, not an exception: the pod leaves the Service and stays up, which is
    # what lets it rejoin the moment the database comes back.
    response = client(StubDatabase(healthy=False)).get("/readyz")

    assert response.status_code == 503
    assert response.json() == {"status": "degraded", "database": "unreachable"}


def test_the_version_endpoint_speaks_the_ecosystem_vocabulary() -> None:
    # revision / sequence / builtAt, matching GET /api/aerie-revision on the
    # .NET side. docs/plans/version.md fixes one vocabulary for every surface,
    # and a second spelling here is the first crack in it.
    payload = client().get("/api/trading/version").json()

    assert payload == {
        "revision": STAMPED.revision,
        "sequence": STAMPED.sequence,
        "builtAt": None,
    }


def test_the_version_endpoint_refuses_to_be_cached() -> None:
    # The whole question is what is running *right now*.
    response = client().get("/api/trading/version")

    assert response.headers["cache-control"] == "no-store"


def test_every_response_names_its_build() -> None:
    with client() as c:
        for path, status in (("/healthz", 200), ("/api/trading/version", 200), ("/nope", 404)):
            response = c.get(path)
            assert response.status_code == status
            # Including the 404: "which build refused me" is a question worth
            # being able to answer exactly when something is misbehaving.
            assert response.headers[REVISION_HEADER] == STAMPED.revision


def test_metrics_carries_the_build_identity() -> None:
    body = client().get("/metrics").text

    assert 'trading_build_info{revision="' + STAMPED.revision in body
    # The default collectors, registered onto this app's own registry rather
    # than inherited from prometheus_client's global one. python_info rather
    # than a process metric: ProcessCollector reads /proc and so exports
    # nothing on a developer's macOS, which would make this assertion pass in
    # CI and fail on the machine the code was written on.
    assert "python_info" in body


def test_the_database_is_released_at_shutdown() -> None:
    database = StubDatabase()

    with client(database):
        pass

    assert database.disposed


def test_no_sign_in_route_without_a_configured_url() -> None:
    # Local development, and any installation running with the wall off.
    assert client().get("/apps/auth/").status_code == 404


def test_a_bounced_browser_is_sent_to_the_sign_in_shell() -> None:
    # The wall bounces an un-enrolled browser to /apps/auth/ on the host it was
    # going to - which on this host is a service that has no sign-in shell.
    # Without this route the first thing an operator sees at trading.<domain>
    # is a 404 from a service that is working perfectly.
    c = client(sign_in_url="https://home.example.com/apps/auth/")

    for path in ("/apps/auth", "/apps/auth/", "/apps/auth/?r=%2Fapi%2Ftrading%2Fversion"):
        response = c.get(path, follow_redirects=False)
        assert response.status_code == 302
        assert response.headers["location"] == "https://home.example.com/apps/auth/"
