"""The FastAPI application, and the four things it answers at Phase 1.

Four endpoints, and each of them is a contract with a piece of the platform
rather than a feature:

==========================  ============================================
``GET /healthz``            liveness. Never touches the database.
``GET /readyz``             readiness. Fails when the database does not answer.
``GET /metrics``            Prometheus scrape.
``GET /api/trading/version``  which commit this pod was built from.
==========================  ============================================

**Why liveness and readiness are different endpoints, answering differently.**
A liveness probe that checks the database restarts every replica at once during
a failover - the pods are fine, the thing they depend on is not, and killing
them makes the outage longer. A readiness probe that *doesn't* check it leaves
a pod in the Service that cannot serve. The same split is written out in
``charts/aerie/templates/api-deployment.yaml`` for the .NET side, and it is
copied here on purpose rather than re-derived.

The paths are ``/healthz`` and ``/readyz`` because
[`docs/plans/trading.md`](../../../../docs/plans/trading.md) Phase 1 names
them. Note that they are *not* ``/health/live`` and ``/health/ready``, which
are the two paths ``AuthGate``'s allow-list exempts from the wall - and they do
not need to be: the kubelet reaches this pod directly, never through Traefik,
so the wall is not in the path of a probe. An operator curling these from
outside will get a 401 until they are enrolled, which is correct.
"""

# FastAPI registers a route by decorating a function, and inside an application
# factory those functions are never referenced by name afterwards. pyright's
# strict mode reads that as dead code; it is the framework's entire idiom.
# Scoped to this file rather than turned off in pyproject.toml, so the rule goes
# on catching genuinely unreachable helpers everywhere else.
# pyright: reportUnusedFunction=false

import logging
from collections.abc import AsyncGenerator, Awaitable, Callable
from contextlib import asynccontextmanager
from typing import Final

from fastapi import FastAPI, Response
from fastapi.responses import JSONResponse, RedirectResponse
from starlette.requests import Request

from aerie_trading.control.metrics import CONTENT_TYPE, build_registry, render_metrics
from aerie_trading.db import Database, SqlDatabase
from aerie_trading.revision import Revision, read_revision
from aerie_trading.settings import Settings, get_settings

__all__ = ["REVISION_HEADER", "create_app"]

logger = logging.getLogger(__name__)

#: docs/plans/version.md: every response, from every surface, names its build in
#: the same header. No `X-` prefix - RFC 6648 deprecated that in 2012.
REVISION_HEADER: Final = "Aerie-Revision"


def create_app(
    settings: Settings | None = None,
    database: Database | None = None,
    revision: Revision | None = None,
) -> FastAPI:
    """Build the application.

    A factory rather than a module-level ``app``, so a test can substitute a
    database that fails on demand. "Readiness reports not-ready when the
    database is unreachable" is a claim about a failure path, and a suite that
    can only reach the happy path is not evidence for it.
    """
    resolved_settings = settings if settings is not None else get_settings()
    resolved_revision = revision if revision is not None else read_revision()
    # Constructed here rather than in the lifespan because a SQLAlchemy engine
    # opens no connection until it is asked for one - so this is cheap, and it
    # keeps the injected-database seam simple.
    resolved_database = database if database is not None else SqlDatabase(resolved_settings)

    @asynccontextmanager
    async def lifespan(_: FastAPI) -> AsyncGenerator[None]:
        logger.info(
            "Trading control plane starting",
            extra={"AerieRevision": resolved_revision.revision},
        )
        try:
            yield
        finally:
            resolved_database.dispose()
            logger.info("Trading control plane stopped")

    app = FastAPI(
        title="Aerie Trading",
        summary="The trading silo's control plane.",
        version=resolved_revision.revision,
        lifespan=lifespan,
        # No interactive docs route: nothing here is a public API surface, and
        # the one endpoint an operator reads by hand returns JSON they can read
        # by hand. Phase 7's SPA is the interface.
        docs_url=None,
        redoc_url=None,
    )

    @app.middleware("http")
    async def stamp_revision(
        request: Request,
        call_next: Callable[[Request], Awaitable[Response]],
    ) -> Response:
        """``Aerie-Revision`` on every response, including the failures.

        Registered as the outermost middleware so a 404, a 500 and a probe
        response all carry it: "which build refused me" is a question worth
        being able to answer exactly when something is misbehaving.
        """
        response = await call_next(request)
        response.headers[REVISION_HEADER] = resolved_revision.revision
        return response

    @app.get("/healthz", include_in_schema=False)
    async def healthz() -> dict[str, str]:
        """Liveness. Deliberately answers without asking anything of anyone."""
        return {"status": "ok"}

    @app.get("/readyz", include_in_schema=False)
    def readyz() -> Response:
        """Readiness: can this pod reach the Ledger.

        A ``def`` rather than an ``async def`` on purpose - the database driver
        here is synchronous, and FastAPI runs a plain ``def`` handler in a
        worker thread. Written ``async``, the same call would block the event
        loop for the duration of a connection timeout, which is to say it would
        make every other request on this pod hang for exactly as long as the
        problem it exists to report.
        """
        try:
            resolved_database.check()
        except Exception:
            # Any failure to reach the Ledger is the answer, so the bare except
            # is the point rather than a shortcut: a driver error, a DNS
            # failure and a refused connection are all "not ready", and
            # enumerating them would only produce a 500 for the one nobody
            # listed.
            logger.warning("Readiness check failed: the Ledger did not answer", exc_info=True)
            return JSONResponse(
                {"status": "degraded", "database": "unreachable"},
                status_code=503,
            )
        return JSONResponse({"status": "ok", "database": "ok"})

    @app.get("/api/trading/version")
    async def version() -> Response:
        """Which commit this pod was built from.

        Field names match ``GET /api/aerie-revision`` on the .NET side
        (``revision``, ``sequence``, ``builtAt``) because
        ``docs/plans/version.md`` fixes one vocabulary for the whole ecosystem
        and a second spelling here would be the first crack in it.

        ``no-store`` for the same reason that endpoint sets it: the entire
        question is what is running *right now*, and it is polled from clients
        whose HTTP cache nobody here controls.
        """
        return JSONResponse(
            {
                "revision": resolved_revision.revision,
                "sequence": resolved_revision.sequence,
                "builtAt": (
                    resolved_revision.built_at.isoformat()
                    if resolved_revision.built_at is not None
                    else None
                ),
            },
            headers={"Cache-Control": "no-store"},
        )

    # The wall (docs/auth-architecture.md) bounces an un-enrolled *browser* to
    # `/apps/auth/` on the host it was going to - Aerie.Api rebuilds the
    # redirect's origin from X-Forwarded-Host so that kiosk. stays on kiosk. On
    # this host there is no sign-in shell to land on, so without this route the
    # first thing an operator sees at trading.<domain> is a 404 from a service
    # that is working perfectly.
    #
    # A configured URL rather than a derived one: this silo knows its own
    # domain, not Aerie's URL structure, and the day it becomes its own
    # repository this stays a value in a manifest instead of a coupling to
    # unpick. Unset - local development, an installation with the wall off -
    # registers no route at all.
    #
    # The `?r=` is deliberately dropped. It names a path on *this* host, and
    # the shell it is being handed to would resolve it against its own; the
    # honest version of that is one manual navigation back, until Phase 7 puts
    # a real UI here to return to.
    if resolved_settings.sign_in_url:
        sign_in_url = resolved_settings.sign_in_url

        @app.get("/apps/auth", include_in_schema=False)
        @app.get("/apps/auth/{_path:path}", include_in_schema=False)
        async def sign_in(_path: str = "") -> Response:
            return RedirectResponse(sign_in_url, status_code=302)

    registry = build_registry(resolved_revision)

    @app.get("/metrics", include_in_schema=False)
    async def metrics() -> Response:
        """The Prometheus scrape endpoint.

        Reached by the ServiceMonitor in
        ``deploy/cluster/observability/config/scrape/trading.yaml``, in-cluster
        and directly - never through Traefik, so the auth wall is not in front
        of it and does not need an exemption.
        """
        return Response(render_metrics(registry), media_type=CONTENT_TYPE)

    return app
