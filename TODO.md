# Host & Container Metrics Architecture

A metrics/dashboard stack for the home cluster (host-level CPU/RAM/disk/network across every server, plus per-container utilization with clear "top consumers" visibility), delivered as a new `compose.metrics.yml` merged alongside `compose.prod.yml` and `compose.observability.yml`, following the same decoupled-compose-file pattern as [`docs/monitoring-alerting-architecture.md`](docs/monitoring-alerting-architecture.md). This was explicitly called out as a deferred Phase 5 in that doc ("Metrics/dashboards ... not part of the original ask").

Proposed decisions for this round:

- **Metrics store + dashboards**: Prometheus + Grafana, not OpenSearch/Dashboards. Metrics are numeric time series, a different shape than the log/text data OpenSearch is already handling — Prometheus is purpose-built for scrape-based collection and Grafana's panel types (especially "top N by value") solve the top-consumer requirement close to out-of-the-box via community dashboards, where Dashboards' visualization builder would fight us for the same result.
- **Host metrics**: `node_exporter` on every host in the cluster.
- **Container metrics**: `cAdvisor` on every host that runs Docker containers.
- **Scrape topology**: one central Prometheus (living on the primary Aerie host) polls every host's `node_exporter`/`cAdvisor` over the LAN. Static scrape targets (`prometheus.yml`), not service discovery — homelab scale doesn't need it, and it keeps config in git instead of a discovery mechanism.
- **Dashboards**: Grafana, provisioned from files (datasource + dashboard JSON committed to the repo), not clicked through — same "reproducible, no manual UI setup" bar as the logging/Kuma stack.
- **Exposure**: `metrics.${DOMAIN}` via the existing Caddy/`edge` pattern from [`docs/reverse-proxy-architecture.md`](docs/reverse-proxy-architecture.md). Unlike OpenSearch Dashboards/Kuma, Grafana's own auth stays **on** by default (real admin account, not disabled) — it's the one UI in this stack with meaningful config (alert rules, provisioning) worth actually gating, and unlike Kuma it isn't a hard technical requirement, just a reasonable default given it costs nothing extra.

## Open questions to resolve before Phase 1

- [ ] **Enumerate every host in the cluster.** This repo's compose files currently describe one server. List every additional machine that should show up on this dashboard (NAS, Pi, pfSense box, secondary compute, etc.) and for each: does it run Docker already, or is it bare-metal/appliance-only?
- [ ] For any non-Docker host (e.g. a Synology NAS, pfSense), decide the collection method per-host — most have either a native `node_exporter` package/binary or a vendor-specific exporter (e.g. Synology's SNMP, a pfSense `node_exporter` pkg). These won't follow the "add a compose service" pattern below and need a one-off install step each.
- [ ] Confirm free RAM across the cluster for Prometheus (retention-dependent, budget ~1-2GB for a homelab-scale TSDB) + Grafana (~150MB) on the primary host, and ~negligible for `node_exporter`/`cAdvisor` on each satellite host.

## Implementation Progress

Same process as `docs/monitoring-alerting-architecture.md`: implemented one checklist item at a time, each independently verifiable/committed before moving to the next. Item tags: `[code]` (Claude does directly), `[manual]` (needs the live hosts or a web UI), `[verify]` (checkpoint, usually needs the user to confirm observed behavior).

### Checklist

#### Prerequisites

- [ ] `[manual]` Resolve the open questions above (host inventory + collection method per host).
- [ ] `[manual]` Confirm free RAM per host (see above).

#### Phase 1 — Host + container metrics collection

- [ ] `[code]` 1. Add `node-exporter` service to `compose.metrics.yml` (or the relevant compose file per host) — host CPU/RAM/disk/network.
- [ ] `[code]` 2. Add `cadvisor` service alongside it on every Docker host — per-container CPU/RAM/network/block IO. Needs read access to `/var/run/docker.sock`, `/sys/fs/cgroup`, `/var/lib/docker` (read-only mounts).
- [ ] `[manual]` 3. For non-Docker hosts identified above, install the appropriate exporter natively.
- [ ] `[verify]` 4. `curl` each host's `node-exporter:9100/metrics` and `cadvisor:8080/metrics` and confirm real data.

#### Phase 2 — Central Prometheus

- [ ] `[code]` 1. Add `prometheus` service to `compose.metrics.yml` on the primary host, with a committed `containers/prometheus/prometheus.yml` listing static scrape targets for every host's `node-exporter`/`cadvisor`.
- [ ] `[code]` 2. Retention/storage volume (`prometheus_data`), sane retention window (e.g. 15-30d — cluster-scale metrics don't need OpenSearch's 30d log retention reasoning, just enough for trend dashboards).
- [ ] `[verify]` 3. Prometheus's own targets page (`/targets`, loopback-only like OpenSearch's 9200) shows every host `UP`.

#### Phase 3 — Grafana dashboards

- [ ] `[code]` 1. Add `grafana` service to `compose.metrics.yml`, joined to `edge` for `metrics.${DOMAIN}`.
- [ ] `[code]` 2. Provision the Prometheus datasource from a committed YAML file (`containers/grafana/provisioning/datasources/`), not clicked through.
- [ ] `[code]` 3. Provision two dashboards from committed JSON (`containers/grafana/provisioning/dashboards/`): a well-known community "Node Exporter Full" dashboard (per-host CPU/RAM/disk/network) and a "Docker/cAdvisor" dashboard with a top-consumers-by-CPU and top-consumers-by-RAM panel (sorted table/bar gauge, cluster-wide).
- [ ] `[manual]` 4. First-run: set a real Grafana admin password (env var at deploy time, same GitHub Actions secrets pattern as `HA_TOKEN`).
- [ ] `[code]` 5. Caddy subdomain `metrics.${DOMAIN}` (label pair on the `grafana` service, matching the existing pattern — no other wiring needed per `reverse-proxy-architecture.md`).
- [ ] `[verify]` 6. Load `metrics.${DOMAIN}`, confirm both dashboards populate with live data and the top-consumers panel correctly highlights the heaviest containers.

#### Phase 4 — Alerting tie-in (optional, later)

- [ ] `[manual]` Decide whether threshold alerts (host disk >90%, container OOM-killed, sustained CPU saturation) route through Grafana's own alerting or reuse the existing Home Assistant notify webhook pattern from `monitoring-alerting-architecture.md` Phase 4, for one consistent "alerts hit your phone" path instead of two.

## Architecture (proposed)

```text
[each cluster host] --node_exporter (9100)--\
                     --cAdvisor (8080)--------\
                                                >--- Prometheus (primary host, scrapes all) --- Grafana (metrics.${DOMAIN})
[non-Docker hosts]   --native exporter--------/
```

## Components (proposed)

| Service | Image | Role | Runs on |
| --- | --- | --- | --- |
| `node-exporter` | `prom/node-exporter` | Host CPU/RAM/disk/network metrics | every host |
| `cadvisor` | `gcr.io/cadvisor/cadvisor` | Per-container CPU/RAM/network/IO metrics | every Docker host |
| `prometheus` | `prom/prometheus` | Central scrape + TSDB storage | primary host |
| `grafana` | `grafana/grafana` | Dashboards, incl. top-consumers view | primary host |

## Deferred / not in this round

- Alerting integration (Phase 4 above) — functional value depends on the base dashboards existing first.
- Long-term metrics retention / downsampling beyond Prometheus's local TSDB (e.g. Thanos/Mimir) — unnecessary at homelab scale.
- Auth hardening beyond Grafana's own login (e.g. Caddy `basic_auth` in front of it) — revisit only if this stack's exposure model changes, same trigger condition as `monitoring-alerting-architecture.md`'s deferred auth item.
