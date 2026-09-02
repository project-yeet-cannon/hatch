"""``python -m aerie_trading.control`` - what the container runs.

A module rather than a console script so the image's ENTRYPOINT names something
that exists in the source tree and can be run identically outside it. Logging
is configured *before* uvicorn starts, so uvicorn's own startup lines come out
in the same JSON shape as everything after them - a service whose first three
lines are unparseable is a service whose failures to start are the ones the log
index cannot show you.
"""

import uvicorn

from aerie_trading.control.app import create_app
from aerie_trading.logging import configure_logging
from aerie_trading.settings import get_settings


def main() -> None:
    settings = get_settings()
    configure_logging(settings.log_level)

    uvicorn.run(
        create_app(settings),
        # Every interface, which in a container means the one it has. The
        # boundary here is the Service and the NetworkPolicy around it, not the
        # bind address.
        host="0.0.0.0",
        port=8080,
        # uvicorn otherwise installs its own dictConfig over the handlers
        # configure_logging just set, and the request lines go back to plain
        # text while the application lines stay JSON.
        log_config=None,
        # X-Forwarded-* is Traefik's, and it is the only thing in front of this
        # pod. Without this the client IP in an access line is the ingress
        # controller's, for every request, forever.
        # X-Forwarded-* is Traefik's, and Traefik is the only thing in front of
        # this pod. Without this every access line records the ingress
        # controller's address as the client, forever. The wildcard is
        # deliberate: the pod IP Traefik connects from is assigned by the
        # cluster and changes on every reschedule, so pinning it would be a
        # value that is wrong more often than it is right.
        proxy_headers=True,
        forwarded_allow_ips="*",
    )


if __name__ == "__main__":
    main()
