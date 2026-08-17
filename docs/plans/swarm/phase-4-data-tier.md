[← Phase 3](phase-3-platform-services.md) · [Design & decisions](design.md) · [Phase 5 →](phase-5-app-tier.md)

---

# Phase 4 — Data tier

**Status: Not started**

- [ ] CNPG `Cluster`: 3 instances, `minSyncReplicas: 1`, anti-affinity, `local-path`
      — note the same two-node caveat as Longhorn (Phase 3b.11): three instances
      with anti-affinity across two nodes leaves one permanently Pending until
      Phase 7 joins the third. Start at 2 and scale, or accept the Pending pod
- [ ] The CNPG WAL `ExternalSecret`, deferred here from Phase 3 — its IAM user
      doesn't exist yet, which is why `postgres/wal-s3-*` is `required: false`
      in [`parameters.json`](../../../scripts/secrets/parameters.json). Create the user,
      seed via Provision 2, flip the entries to required, give them a
      `kubernetes` block and regenerate (3b.6) — the manifest isn't written by
      hand, and a block on an entry still marked optional is refused
- [ ] `quartz` database via the `Database` CRD
- [ ] Quartz DDL via a one-shot Job — mind the missing `IF NOT EXISTS` (Finding 3)
- [ ] WAL archiving + base backups to S3 → continuous PITR, a strictly better
      copy #3 for the most important data than nightly dumps
- [ ] Restore the Phase 0 backup into it and validate against real data

