"""Serving the control panel's bundle, and the three ways that goes wrong.

The bundle itself is built by ``npm run build -w apps/trading`` and is not in
git, so these tests write a two-file bundle of their own. What is under test is
not Vite's output - it is the routing around it: that a deep link reaches the
app, that an unmatched API path does not, and that a missing bundle leaves a
working API rather than a service that will not start.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from aerie_trading.control.spa import mount_spa

INDEX = "<!doctype html><title>Trading</title><div id=root></div>"


@pytest.fixture
def bundle(tmp_path: Path) -> Path:
    """A bundle shaped like Vite's: an index and a hashed asset."""
    (tmp_path / "index.html").write_text(INDEX, encoding="utf-8")
    (tmp_path / "assets").mkdir()
    (tmp_path / "assets" / "index-abc123.js").write_text("export default 1;\n", encoding="utf-8")
    return tmp_path


def client(root: Path) -> TestClient:
    app = FastAPI()

    @app.get("/api/trading/queue")
    async def queue() -> dict[str, int]:
        return {"queued": 0}

    mount_spa(app, root)
    return TestClient(app)


def test_the_root_serves_the_app(bundle: Path) -> None:
    response = client(bundle).get("/")

    assert response.status_code == 200
    assert "Trading" in response.text


def test_a_deep_link_reaches_the_router_rather_than_a_404(bundle: Path) -> None:
    # `/runs/42` is a route in the SPA and a file that does not exist on disk.
    # Without the fallback, reloading the page somebody is looking at loses it.
    response = client(bundle).get("/runs/42")

    assert response.status_code == 200
    assert "Trading" in response.text


def test_index_is_never_cached(bundle: Path) -> None:
    # index.html carries no content hash and is how a client discovers which
    # hashed bundle to fetch. A cached one is a client pinned to a deploy that
    # no longer exists.
    assert client(bundle).get("/").headers["Cache-Control"] == "no-cache"


def test_a_hashed_asset_is_cached_hard(bundle: Path) -> None:
    # The other half of the same decision: these filenames change when their
    # contents do, which is the only condition under which a year is safe.
    response = client(bundle).get("/assets/index-abc123.js")

    assert response.status_code == 200
    assert "immutable" in response.headers["Cache-Control"]


def test_an_api_route_still_wins(bundle: Path) -> None:
    # The catch-all is registered last, so it only ever sees a path no API
    # route claimed. If that ordering ever inverts, this is what says so.
    assert client(bundle).get("/api/trading/queue").json() == {"queued": 0}


def test_an_unmatched_api_path_is_a_404_rather_than_a_page(bundle: Path) -> None:
    # Without this, a typo'd endpoint returns 200 and an HTML document, and a
    # client parsing it reports a JSON error very far from the mistake.
    response = client(bundle).get("/api/trading/nothing-here")

    assert response.status_code == 404


def test_a_path_that_climbs_out_of_the_bundle_gets_the_app(bundle: Path, tmp_path: Path) -> None:
    # Most clients normalise `..` away, and not all of them do. The resolved
    # path has to stay inside the bundle, and anything else is the SPA
    # fallback rather than a file.
    (tmp_path.parent / "outside.txt").write_text("secret", encoding="utf-8")

    response = client(bundle).get("/../outside.txt")

    assert response.status_code == 200
    assert "secret" not in response.text


def test_no_bundle_leaves_a_working_api(tmp_path: Path) -> None:
    # A developer who has not run npm, and the migration init container, both
    # import this app factory. Refusing to start without a frontend toolchain
    # would make the API unexercisable without one.
    app = FastAPI()

    @app.get("/api/trading/queue")
    async def queue() -> dict[str, int]:
        return {"queued": 0}

    mounted = mount_spa(app, tmp_path / "nothing-here")

    assert mounted is False
    assert TestClient(app).get("/api/trading/queue").status_code == 200
