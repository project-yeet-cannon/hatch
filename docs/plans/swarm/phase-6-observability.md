[← Phase 5](phase-5-app-tier.md) · [Design & decisions](design.md) · [Phase 7 →](phase-7-cutover.md)

---

# Phase 6 — Observability

**Status: Not started**

- [ ] fluent-bit rewrite (Finding 4), preserving the `State.Service` attribution
- [ ] `kube-prometheus-stack` with windows_exporter as additional targets (Finding 5)
- [ ] **Alert on `gotk_reconcile_condition{type="Ready",status="False"}`**, held
      for a few minutes to ride out a retry — the durable half of Phase 3b.14.
      Both checks there run before a merge; this is the only thing that notices a
      `HelmRelease` that breaks its own upgrade at 3am with no commit involved
- [ ] Provisioning Jobs (Finding 6)
- [ ] Longhorn PVCs for Grafana / Kuma / OpenSearch / Prometheus
- [ ] OpenSearch stays **single-node** — biggest RAM consumer, and observability
      was explicitly scoped out of HA

---

## Sizing, from the old host

[5a.6](phase-5-app-tier.md) sampled the compose stack before Phase 7 removes it,
so these are the last real numbers this stack will produce. Requests and limits
are required here for the same reason they are in 5b.11 —
[goal 2 does not work without them](design.md#how-goal-2-actually-works).

| Workload | observed | `requests.memory` | `limits.memory` |
|---|---|---|---|
| `opensearch` | 1.48 GiB | `1.5Gi` | `2Gi` |
| `opensearch-dashboards` | 310 MiB | `384Mi` | `768Mi` |
| `grafana` | 226 MiB | `256Mi` | `512Mi` |
| `prometheus` (kube-prometheus-stack) | 141 MiB | `512Mi` | `1.5Gi` |
| `uptime-kuma` | 151 MiB | `192Mi` | `384Mi` |
| `autokuma` | 32 MiB | `64Mi` | `128Mi` |
| `fluent-bit` (DaemonSet) | 7 MiB | `64Mi` | `128Mi` |
| `node-exporter` | 3 MiB | `32Mi` | `64Mi` |

CPU requests in the 10m–50m range and **no CPU limits**, per 5a.6 — the whole
observability stack peaked at 5% of one core.

Two rows are deliberately not the observed number:

- **`prometheus`.** 141 MiB is a compose project's worth of series.
  kube-prometheus-stack additionally scrapes kube-state-metrics, kubelet
  cAdvisor, Longhorn and Flux, at several times the cardinality. Start at
  `512Mi`/`1.5Gi` and re-measure once it has a week of retention behind it;
  this is the one workload here whose request should be revisited rather than
  set once.
- **`opensearch`.** 1.48 GiB resident against `-Xms512m -Xmx512m`
  ([compose.observability.yml](../../../compose.observability.yml#L77)) is heap plus
  mmap'd Lucene segments plus JVM overhead. Size the container from the
  resident figure, not the heap — and if the heap is raised, the container
  needs roughly the increase again on top, not just the increase.
