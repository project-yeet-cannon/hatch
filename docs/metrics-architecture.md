# Host & Container Metrics Architecture

## Summary

A metrics/dashboard stack for the home cluster (host-level CPU/RAM/disk/network across every server, plus per-container utilization with clear "top consumers" visibility), delivered as a new `compose.metrics.yml` merged alongside `compose.prod.yml` and `compose.observability.yml`, following the same decoupled-compose-file pattern as [`monitoring-alerting-architecture.md`](monitoring-alerting-architecture.md). This was explicitly called out as a deferred Phase 5 in that doc ("Metrics/dashboards ... not part of the original ask").

Decisions locked in for this round:

- **Metrics store + dashboards**: Prometheus + Grafana, not OpenSearch/Dashboards. Metrics are numeric time series, a different shape than the log/text data OpenSearch is already handling — Prometheus is purpose-built for scrape-based collection and Grafana's panel types (especially "top N by value") solve the top-consumer requirement close to out-of-the-box via community dashboards, where Dashboards' visualization builder would fight us for the same result.
- **Host metrics**: `windows_exporter`, running natively on the Windows host (see [Windows Server detour](#windows-server-detour) below for why not `node_exporter` in a container).
- **Container metrics**: `cAdvisor` on every host that runs Docker containers.
- **Scrape topology**: one central Prometheus (living on the primary Aerie host) polls every host's exporters over the LAN. Static scrape targets (`prometheus.yml`), not service discovery — homelab scale doesn't need it, and it keeps config in git instead of a discovery mechanism.
- **Dashboards**: Grafana, provisioned from files (datasource + dashboard JSON committed to the repo), not clicked through — same "reproducible, no manual UI setup" bar as the logging/Kuma stack.
- **Exposure**: `metrics.${DOMAIN}` via the existing Caddy/`edge` pattern from [`reverse-proxy-architecture.md`](reverse-proxy-architecture.md). Unlike OpenSearch Dashboards/Kuma, Grafana's own auth stays **on** by default (real admin account, not disabled) — it's the one UI in this stack with meaningful config (alert rules, provisioning) worth actually gating, and unlike Kuma it isn't a hard technical requirement, just a reasonable default given it costs nothing extra.

## Windows Server detour

Scoping (2026-07-27) settled on just the primary Aerie host for this round (single Docker host, matching what `compose.prod.yml` already describes; additional hosts are a later follow-up) with plenty of free RAM headroom for Prometheus + Grafana + exporters.

That scoping only established host *count*, not OS. Implementation (2026-07-28) found the primary Aerie host is actually **Windows Server running Docker Desktop (WSL2 backend)**, not bare Linux as originally assumed — this broke two assumptions in the original `node_exporter`-in-a-container plan:

- `network_mode: host` under Docker Desktop binds to the WSL2 VM's network namespace, not the real Windows machine, so `host.docker.internal` (Prometheus's scrape target) couldn't reach `node-exporter`/`cadvisor` — confirmed via `connection refused` on Prometheus's `/targets` page.
- Even if reachable, a container's `/proc`/`/sys`/`/` under Docker Desktop are the WSL2 VM's, not the real Windows host's — `node_exporter` can structurally never report genuine Windows CPU/RAM/disk this way, only the VM's own view. `cadvisor`'s per-container stats are unaffected (sourced from the Docker daemon itself, accurate regardless of network mode).

Fix, matching the "correct fix over Docker-Desktop-VM-passing-as-host-metrics shortcut" bar used throughout this repo:

- `cadvisor` dropped `network_mode: host`, joined the `metrics` network instead, and its Prometheus scrape target changed from `host.docker.internal:8080` to `cadvisor:8080` (service-name resolution, same pattern as the `prometheus` job itself). No mount changes needed — `docker.sock`/`cgroup`/`docker` paths aren't network-mode-dependent.
- `node_exporter` was removed entirely (both the compose service and its Prometheus scrape job) — it can't report real host metrics under Docker Desktop, per above.
- `windows_exporter` (v0.31.8) installs/runs directly on the Windows self-hosted runner host via `.github/workflows/cd.yml`'s `deploy` job — not in Docker — idempotent across re-deploys, same "converges on every run" bar as `kuma-provision`.
- Prometheus scrapes it at `host.docker.internal:9182` (windows_exporter's default port) — this address correctly reaches the real Windows host from a container, unlike host-network-mode containers.
- The Grafana dashboard for host metrics is community dashboard **14510 ("Windows Node 2021")**, not "Node Exporter Full" — metric names differ (`windows_cpu_time_total` etc., not `node_cpu_seconds_total`), so the original dashboard JSON wouldn't render against this data source. Saved as `containers/grafana/provisioning/dashboards/windows-exporter.json`, re-pointed from its original `${DS_PROMETHEUS}` import-time placeholder to the fixed `prometheus` datasource uid so it loads via file provisioning with no manual click-through.

## Architecture

```text
[Aerie host, Windows Server + Docker Desktop]
    windows_exporter (native, :9182) ----\
    cAdvisor (container, metrics net) -----\
                                             >--- Prometheus (primary host, scrapes both) --- Grafana (metrics.${DOMAIN})
[future non-Docker/other hosts] --native exporter--/
```

## Components

| Service | Image / install | Role | Runs on |
| --- | --- | --- | --- |
| `windows_exporter` | native Windows service, pinned v0.31.8 | Real host CPU/RAM/disk/network metrics | Windows host (native, not Docker) |
| `cadvisor` | `ghcr.io/google/cadvisor` | Per-container CPU/RAM/network/IO metrics | every Docker host, `metrics` network |
| `prometheus` | `prom/prometheus` | Central scrape + TSDB storage | primary host |
| `grafana` | `grafana/grafana` | Dashboards, incl. top-consumers view | primary host |

## Implementation status

All phases below are implemented and committed. Same process as [`monitoring-alerting-architecture.md`](monitoring-alerting-architecture.md): each checklist item done independently, verified/committed before moving to the next. Item tags: `[code]` (Claude does directly), `[manual]` (needs the live hosts or a web UI), `[verify]` (checkpoint, usually needs the user to confirm observed behavior).

#### Prerequisites

- [x] `[manual]` Enumerate every host in the cluster — scoped to just the primary Aerie host for this round (see [Windows Server detour](#windows-server-detour)).
- [x] `[manual]` Confirm free RAM on the primary host — plenty of headroom for Prometheus + Grafana + exporters.

#### Phase 1 — Host + container metrics collection

- [x] `[code]` Add `cadvisor` service to `compose.metrics.yml` — per-container CPU/RAM/network/block IO. Needs read access to `/var/run/docker.sock`, `/sys/fs/cgroup`, `/var/lib/docker` (read-only mounts).
- [x] `[code]` Add `windows_exporter` install/service step to `.github/workflows/cd.yml`'s `deploy` job, running natively on the Windows self-hosted runner host (supersedes the originally-planned containerized `node-exporter` — see [Windows Server detour](#windows-server-detour)).
- [x] `[verify]` Confirm `cadvisor:8080/metrics` and `windows_exporter`'s `:9182/metrics` return real data.

#### Phase 2 — Central Prometheus

- [x] `[code]` Add `prometheus` service to `compose.metrics.yml` on the primary host, with a committed `containers/prometheus/prometheus.yml` listing static scrape targets (`cadvisor:8080`, `host.docker.internal:9182`).
- [x] `[code]` Retention/storage volume (`prometheus_data`), 15-30d retention window (trend dashboards only, not log-scale retention needs).
- [x] `[verify]` Prometheus's own targets page (`/targets`, loopback-only like OpenSearch's 9200) shows `cadvisor` and `windows-exporter` both `UP`.

#### Phase 3 — Grafana dashboards

- [x] `[code]` Add `grafana` service to `compose.metrics.yml`, joined to `edge` for `metrics.${DOMAIN}`.
- [x] `[code]` Provision the Prometheus datasource from a committed YAML file (`containers/grafana/provisioning/datasources/`), not clicked through.
- [x] `[code]` Provision two dashboards from committed JSON (`containers/grafana/provisioning/dashboards/`): community dashboard 14510 ("Windows Node 2021", host CPU/RAM/disk/network) and a Docker/cAdvisor dashboard with top-consumers-by-CPU and top-consumers-by-RAM panels.
- [x] `[manual]` First-run: set a real Grafana admin password (env var at deploy time, same GitHub Actions secrets pattern as `HA_TOKEN`).
- [x] `[code]` Caddy subdomain `metrics.${DOMAIN}` (label pair on the `grafana` service, matching the existing pattern — no other wiring needed per `reverse-proxy-architecture.md`).
- [ ] `[verify]` Load `metrics.${DOMAIN}`, confirm both dashboards populate with live data and the top-consumers panel correctly highlights the heaviest containers.

#### Phase 4 — Alerting tie-in (optional, later)

- [ ] `[manual]` Decide whether threshold alerts (host disk >90%, container OOM-killed, sustained CPU saturation) route through Grafana's own alerting or reuse the existing Home Assistant notify webhook pattern from `monitoring-alerting-architecture.md` Phase 4, for one consistent "alerts hit your phone" path instead of two.

## Deferred / not in this round

- Alerting integration (Phase 4 above) — functional value depends on the base dashboards existing first.
- Long-term metrics retention / downsampling beyond Prometheus's local TSDB (e.g. Thanos/Mimir) — unnecessary at homelab scale.
- Auth hardening beyond Grafana's own login (e.g. Caddy `basic_auth` in front of it) — revisit only if this stack's exposure model changes, same trigger condition as `monitoring-alerting-architecture.md`'s deferred auth item.
- Extending collection to additional cluster hosts beyond the primary Aerie host — deferred at the prerequisites stage, not blocked by anything built here.
