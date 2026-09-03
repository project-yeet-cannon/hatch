"""Serving the control panel's bundle from this service.

docs/plans/trading.md Phase 7: *"Note the seam: this SPA is served by the
trading service, not by ``Aerie.Api``, so it does not join ``wwwroot/apps/``."*
That one sentence decides everything in this file.

**Where the bundle comes from.** ``src/Aerie.Web/apps/trading`` builds into
``aerie_trading/control/static/``, which is inside the package and therefore
inside the image the Dockerfile copies - the build context is this directory
and Docker refuses a ``COPY`` that escapes it, so a bundle anywhere else could
not be shipped without breaking the extraction seam. The directory is not in
git: it is a build output, produced by ``npm run build -w apps/trading`` before
the image is built, exactly as the .NET apps are produced before theirs.

**A missing bundle is not an error.** Every process in this package imports the
app factory - the worker does not, but the migration init container and a
developer running ``uvicorn`` by hand do - and a service that refused to start
because nobody had run ``npm`` would be a service whose API cannot be exercised
without a frontend toolchain. So the routes are registered only when the
directory exists, and their absence is one log line.

**Deep links, and why this is not just ``StaticFiles``.** ``/runs/42`` is a
route in the SPA's router and a file that does not exist on disk. A bare
``StaticFiles`` mount answers it with a 404, so a reload of the page somebody
is looking at loses it. The catch-all below serves the file when there is one
and ``index.html`` when there is not, which is the standard SPA fallback -
with two rules that keep it from becoming a way to read the pod:

- The resolved path must stay inside the bundle. ``..`` in a URL is normalised
  by most clients and by *not all* of them, and the check is one line.
- ``/api`` is never fallen back to. Without that, a typo'd API path returns
  200 and an HTML document, and a client that parsed it would report a JSON
  error at a point very far from the mistake.
"""

# FastAPI's decorator idiom again - see control/app.py's copy of this note.
# pyright: reportUnusedFunction=false

from __future__ import annotations

import logging
from pathlib import Path
from typing import Any, Final

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles
from starlette.responses import Response

__all__ = ["STATIC_ROOT", "mount_spa"]

logger = logging.getLogger(__name__)

#: Where the bundle lands. Beside this module, inside the package.
STATIC_ROOT: Final = Path(__file__).resolve().parent / "static"

#: Vite writes content-hashed files here, and only here. They are immutable by
#: construction - a change to any of them changes its name - so they are the
#: one thing in the bundle that may be cached hard.
_ASSETS: Final = "assets"

#: One year, which is the convention for content-addressed assets and is what
#: Aerie.Api sets on the same files for the same reason.
_IMMUTABLE: Final = "public, max-age=31536000, immutable"

#: index.html carries no hash and is how a client discovers which hashed
#: bundle to fetch, so it must never be held: a cached index.html is a client
#: pinned to a deploy that no longer exists.
_NO_STORE: Final = "no-cache"


def mount_spa(app: FastAPI, root: Path = STATIC_ROOT) -> bool:
    """Serve the bundle at ``root`` from ``app``. Returns whether it was there.

    Called **last** in ``create_app``, and that ordering is load-bearing:
    Starlette matches routes in the order they were added, so the catch-all
    here only ever sees a path no API route claimed.
    """
    index = root / "index.html"
    if not index.is_file():
        logger.info(
            "No control panel bundle; serving the API only",
            extra={"StaticRoot": str(root)},
        )
        return False

    assets = root / _ASSETS
    if assets.is_dir():
        app.mount(f"/{_ASSETS}", _HashedAssets(directory=assets), name=_ASSETS)

    @app.get("/{path:path}", include_in_schema=False)
    async def spa(path: str) -> FileResponse:
        # An API path that reached here is one no route matched, which is a
        # 404 and not a page. See the module docstring.
        if path.startswith("api/"):
            raise HTTPException(status_code=404, detail=f"no route /{path}")

        candidate = (root / path).resolve()
        if path and root in candidate.parents and candidate.is_file():
            return FileResponse(candidate)
        return FileResponse(index, headers={"Cache-Control": _NO_STORE})

    logger.info("Serving the control panel", extra={"StaticRoot": str(root)})
    return True


class _HashedAssets(StaticFiles):
    """``StaticFiles`` that says its files are immutable, because they are.

    Vite content-hashes everything under ``assets/``: a change to a file
    changes its name, so a client holding one for a year is holding a file that
    is still correct. ``StaticFiles`` has no option for this, and the
    alternative - leaving the default validators - makes every page load a
    conditional request per asset against a pod that could have answered
    nothing instead.
    """

    def file_response(self, *args: Any, **kwargs: Any) -> Response:
        response = super().file_response(*args, **kwargs)  # pyright: ignore[reportUnknownMemberType, reportUnknownArgumentType]
        response.headers["Cache-Control"] = _IMMUTABLE
        return response
