[← Phase 6](phase-6-observability.md) · [Design & decisions](design.md) · [Phase 8 →](phase-8-backup-v2.md)

---

# Phase 7 — Cutover

**Status: Not started**

- [ ] Point pfSense Unbound's `local-data` at the kube-vip VIP (`INGRESS_VIP`,
      set in Phase 3a.2) — a one-line change to the existing zone redirect
- [ ] Verify all seven hostnames — `home`, `kiosk`, `files`, `share`, `status`,
      `logs`, `metrics`. The plan said six before `share` existed; the wildcard
      makes the count irrelevant to the certificate but not to this check
- [ ] Rebuild the old prod box as the third k3s server and join it, restoring
      proper 3-node quorum
- [ ] **Raise the replica counts the two-node build window forced down**: the
      `LONGHORN_REPLICA_COUNT` variable to `3` and re-run Provision 4 (no commit
      — that's the point of the ConfigMap), the per-volume Phase 6 classes to
      the [storage split](design.md#storage-split)'s 3-and-2, and CNPG to 3 instances.
      Confirm Longhorn actually rebuilds onto the new node rather than reporting
      Degraded, which is the first real proof the third node is carrying load

