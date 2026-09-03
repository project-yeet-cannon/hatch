"""What ``/metrics`` answers with.

kube-prometheus-stack is already deployed and already scrapes
(``deploy/cluster/observability/config/scrape/trading.yaml``); the app's side
of that contract is an endpoint and a registry, which is why this is
``prometheus-client`` and not an exporter framework.

The registry is built here rather than taken from the library's global default,
and that is the one decision in this module. A module-level registry makes
metric registration a process-wide side effect of an import, so creating a
second app in the same process - which every test that exercises two
configurations does - raises "Duplicated timeseries in CollectorRegistry"
rather than doing the obvious thing. Passing the registry in costs one
argument and removes the class of failure. The three default collectors are
registered onto it explicitly, so nothing is lost by not using the global one.
"""

from prometheus_client import CONTENT_TYPE_LATEST, CollectorRegistry, Gauge, generate_latest
from prometheus_client.gc_collector import GCCollector
from prometheus_client.platform_collector import PlatformCollector
from prometheus_client.process_collector import ProcessCollector

from aerie_trading.control.collection_health import CollectionHealthCollector, HealthSource
from aerie_trading.revision import Revision

__all__ = ["CONTENT_TYPE", "build_registry", "render_metrics"]

#: The exposition format's content type, re-exported so the route that serves
#: it does not import from two places.
CONTENT_TYPE = CONTENT_TYPE_LATEST


def build_registry(revision: Revision, health: HealthSource | None = None) -> CollectorRegistry:
    """A registry holding the process defaults and this build's identity.

    ``trading_build_info`` is a constant gauge whose labels carry the identity -
    the Prometheus idiom, rather than an Info metric - because that shape joins
    onto any other series with
    ``* on(instance) group_left(revision) trading_build_info``, which is what
    makes "this change starts exactly at that deploy" visible without leaving
    the dashboard. ``docs/plans/version.md`` asks that every surface be able to
    name its own commit in the same words; this is the metrics surface of that.

    The process and platform collectors bring resident memory, open file
    descriptors, CPU seconds, Python version and GC counts, which is most of a
    "is this pod healthy" panel and none of which had to be written.
    """
    registry = CollectorRegistry()
    ProcessCollector(registry=registry)
    PlatformCollector(registry=registry)
    GCCollector(registry=registry)

    build_info = Gauge(
        "trading_build_info",
        "Build identity of the running trading service; the value is always 1.",
        labelnames=("revision", "sequence"),
        registry=registry,
    )
    build_info.labels(revision=revision.revision, sequence=str(revision.sequence)).set(1)

    # Collection health (docs/plans/trading.md Phase 3), registered onto this
    # registry rather than exposed by the collectors themselves - see
    # aerie_trading/db/health.py for why a CronJob cannot own a counter.
    # Optional so that a registry can be built without a database at all, which
    # is what the build-info tests do.
    if health is not None:
        registry.register(CollectionHealthCollector(health))

    return registry


def render_metrics(registry: CollectorRegistry) -> bytes:
    """The scrape body.

    A rendered response rather than ``make_asgi_app`` mounted as a sub-app: the
    ASGI app that function returns is the one thing in this library pyright
    cannot see the type of, and the alternative it forces - a blanket
    suppression on the import - is a worse trade than losing the OpenMetrics
    content negotiation nothing here asks for. Prometheus scrapes the plain
    exposition format perfectly well, and both calls involved are typed.
    """
    return generate_latest(registry)
