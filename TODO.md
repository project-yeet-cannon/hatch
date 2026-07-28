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

- [x] **Enumerate every host in the cluster.** Resolved 2026-07-27: scoping to just the primary Aerie host for now (single Docker host, matching what `compose.prod.yml` already describes). Additional hosts can be added later as a follow-up, not blocking this round.
- [x] For any non-Docker host — n/a this round, see above.
- [x] Confirm free RAM on the primary host — confirmed 2026-07-27, plenty of headroom for Prometheus + Grafana + node-exporter/cAdvisor.

## Implementation Progress

Same process as `docs/monitoring-alerting-architecture.md`: implemented one checklist item at a time, each independently verifiable/committed before moving to the next. Item tags: `[code]` (Claude does directly), `[manual]` (needs the live hosts or a web UI), `[verify]` (checkpoint, usually needs the user to confirm observed behavior).

### Checklist

#### Prerequisites

- [x] `[manual]` Resolve the open questions above (host inventory + collection method per host).
- [x] `[manual]` Confirm free RAM per host (see above).

#### Phase 1 — Host + container metrics collection

- [x] `[code]` 1. Add `node-exporter` service to `compose.metrics.yml` (or the relevant compose file per host) — host CPU/RAM/disk/network.
- [x] `[code]` 2. Add `cadvisor` service alongside it on every Docker host — per-container CPU/RAM/network/block IO. Needs read access to `/var/run/docker.sock`, `/sys/fs/cgroup`, `/var/lib/docker` (read-only mounts).
- [x] `[manual]` 3. For non-Docker hosts identified above, install the appropriate exporter natively.
- [x] `[verify]` 4. `curl` each host's `node-exporter:9100/metrics` and `cadvisor:8080/metrics` and confirm real data.

#### Phase 2 — Central Prometheus

- [x] `[code]` 1. Add `prometheus` service to `compose.metrics.yml` on the primary host, with a committed `containers/prometheus/prometheus.yml` listing static scrape targets for every host's `node-exporter`/`cadvisor`.
- [x] `[code]` 2. Retention/storage volume (`prometheus_data`), sane retention window (e.g. 15-30d — cluster-scale metrics don't need OpenSearch's 30d log retention reasoning, just enough for trend dashboards).
- [x] `[verify]` 3. Prometheus's own targets page (`/targets`, loopback-only like OpenSearch's 9200) shows every host `UP`.

#### Phase 3 — Grafana dashboards

- [x] `[code]` 1. Add `grafana` service to `compose.metrics.yml`, joined to `edge` for `metrics.${DOMAIN}`.
- [x] `[code]` 2. Provision the Prometheus datasource from a committed YAML file (`containers/grafana/provisioning/datasources/`), not clicked through.
- [x] `[code]` 3. Provision two dashboards from committed JSON (`containers/grafana/provisioning/dashboards/`): a well-known community "Node Exporter Full" dashboard (per-host CPU/RAM/disk/network) and a "Docker/cAdvisor" dashboard with a top-consumers-by-CPU and top-consumers-by-RAM panel (sorted table/bar gauge, cluster-wide).
- [x] `[manual]` 4. First-run: set a real Grafana admin password (env var at deploy time, same GitHub Actions secrets pattern as `HA_TOKEN`).
- [x] `[code]` 5. Caddy subdomain `metrics.${DOMAIN}` (label pair on the `grafana` service, matching the existing pattern — no other wiring needed per `reverse-proxy-architecture.md`).
- [ ] `[verify]` 6. Load `metrics.${DOMAIN}`, confirm both dashboards populate with live data and the top-consumers panel correctly highlights the heaviest containers.

Blocked on 2026-07-28: the primary Aerie host turned out to be Windows Server running Docker Desktop (WSL2 backend), not bare Linux as originally scoped — the "Enumerate every host" open question above only established host *count*, not OS. This breaks two assumptions in Phases 1-3:

- `network_mode: host` under Docker Desktop binds to the WSL2 VM's network namespace, not the real Windows machine, so `host.docker.internal` (Prometheus's scrape target) can't reach `node-exporter`/`cadvisor` — confirmed via `connection refused` on Prometheus's `/targets` page.
- Even if reachable, a container's `/proc`/`/sys`/`/` under Docker Desktop are the WSL2 VM's, not the real Windows host's — `node_exporter` can structurally never report genuine Windows CPU/RAM/disk this way, only the VM's own view. `cadvisor`'s per-container stats are unaffected (sourced from the Docker daemon itself, accurate regardless of network mode).

Follow-up items below fix cadvisor's reachability and replace `node_exporter` with `windows_exporter` running natively on the Windows host, matching the "correct fix" (not the Docker-Desktop-VM-passing-as-host-metrics shortcut) — same root-cause-over-bandaid bar as the rest of this stack.

- [x] `[code]` 7. Fix `cadvisor` reachability: drop `network_mode: host` from `cadvisor` in `compose.metrics.yml`, join it to the `metrics` network instead, and change its scrape target in `prometheus.yml` from `host.docker.internal:8080` to `cadvisor:8080` (service-name resolution, same pattern already used for the `prometheus` job itself). No mount changes needed — `docker.sock`/`cgroup`/`docker` paths aren't network-mode-dependent.
- [x] `[code]` 8. Remove `node-exporter` from `compose.metrics.yml` and its scrape job from `prometheus.yml` — see "Blocked" note above for why it can't report real host metrics here.
- [x] `[code]` 9. Add a `windows_exporter` install/service step to `.github/workflows/cd.yml`'s `deploy` job, running directly on the Windows self-hosted runner host (not in Docker) — idempotent across re-deploys, same "converges on every run" bar as `kuma-provision`. Pinned to v0.31.8 (latest stable as of 2026-07-28).
- [x] `[code]` 10. Add a `windows-exporter` scrape job to `prometheus.yml` targeting `host.docker.internal:9182` (windows_exporter's default port) — this address correctly reaches the real Windows host from a container, unlike host-network-mode containers (see item 7).
- [x] `[code]` 11. Swap `containers/grafana/provisioning/dashboards/node-exporter-full.json` for a windows_exporter-compatible community dashboard — metric names differ (`windows_cpu_time_total` etc., not `node_cpu_seconds_total`), so the existing dashboard JSON won't render against this data source. Used community dashboard 14510 ("Windows Node 2021"), re-pointed from its original `${DS_PROMETHEUS}` import-time placeholder to the fixed `prometheus` datasource uid so it loads via file provisioning with no manual click-through; saved as `containers/grafana/provisioning/dashboards/windows-exporter.json`.
- [ ] `[verify]` 12. Confirm `windows-exporter` and `cadvisor` both show `up` on Prometheus's `/targets` page, then reload `metrics.${DOMAIN}` and confirm both dashboards populate with live data (supersedes item 6).

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
| `cadvisor` | `ghcr.io/google/cadvisor` | Per-container CPU/RAM/network/IO metrics | every Docker host |
| `prometheus` | `prom/prometheus` | Central scrape + TSDB storage | primary host |
| `grafana` | `grafana/grafana` | Dashboards, incl. top-consumers view | primary host |

## Deferred / not in this round

- Alerting integration (Phase 4 above) — functional value depends on the base dashboards existing first.
- Long-term metrics retention / downsampling beyond Prometheus's local TSDB (e.g. Thanos/Mimir) — unnecessary at homelab scale.
- Auth hardening beyond Grafana's own login (e.g. Caddy `basic_auth` in front of it) — revisit only if this stack's exposure model changes, same trigger condition as `monitoring-alerting-architecture.md`'s deferred auth item.
