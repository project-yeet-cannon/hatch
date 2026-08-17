[← Phase 4](phase-4-data-tier.md) · [Design & decisions](design.md) · [Phase 6 →](phase-6-observability.md)

---

# Phase 5 — App tier

**Status: Not started**

- [ ] First-party Helm chart: `api` (3 replicas) + `files` (2 replicas)
- [ ] Migration Job as a Helm hook (Finding 1)
- [ ] Ingress resources replacing the Caddy labels — no per-Ingress `tls:`
      block needed, the Phase 3b.10d `TLSStore` serves the wildcard to all of them
- [ ] The `kiosk` host's `/` → `/apps/dashboard/` rewrite as a Traefik `Middleware`
- [ ] The kiosk Wi-Fi `ExternalSecret`, deferred here from Phase 3 — it stays
      `required: false` until the value moves out of `SiteSettings`, and an
      `ExternalSecret` for an unseeded parameter never reaches `SecretSynced` —
      which 3b.6's generator now refuses outright rather than leaving to be
      remembered
- [ ] **Resource requests and limits on every workload**
- [ ] Flux image-update-automation watching GHCR and committing tag bumps —
      which removes `cd.yml` entirely rather than rewriting it

