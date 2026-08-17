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

