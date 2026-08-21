[← Phase 7](phase-7-cutover.md) · [Design & decisions](design.md) · [Phase 9 →](phase-9-productization.md)

---

# Phase 8 — Backup v2 + rehearsal

**Status: Not started**

- [ ] Migrate to cluster-native backup: CNPG/S3 for Postgres, Longhorn backup
      target → S3 for volumes, restic CronJob for the rest plus the local copy
- [ ] **`--keep-tag cutover-final` on that CronJob's `forget`**, inherited from
      [7b.3](phase-7-cutover.md#phase-7b--the-cutover). The retention this phase
      carries over from Phase 0 — `--keep-daily 7 --keep-weekly 4
      --keep-monthly 12` — is pointed at the same S3 repo the old stack wrote
      to, and the snapshot 7b.3 tagged is the last complete copy of the
      pre-cluster world: Kuma's SQLite and the pre-cutover Postgres dumps exist
      nowhere else once 7c.3 reformats the disk holding the local repo. One
      flag, and it belongs in the manifest the first time the `forget` is
      written rather than after the retention has had a year to reach it
- [ ] The `backup/*` `ExternalSecret`, deferred here from Phase 3b.6 — all three
      values are `required: true` and have been seeded since Phase 2, so this is
      a `kubernetes` block on each entry in
      [`parameters.json`](../../../scripts/secrets/parameters.json) and a regeneration,
      landing them in whatever namespace the CronJob above runs in. Until then
      the entries carry a `kubernetesDeferred` note pointing here, which is what
      keeps "seeded but consumed by nothing" a decision rather than an oversight
- [ ] **Alert on backup age and backup-job failure** — the single most valuable
      alert that doesn't exist today
- [ ] Export the `/aerie/*` parameter tree into the restic repos on the same
      schedule. The secret store is now off-site, but "AWS account is gone" is
      the one failure mode ESO introduces, and `RESTIC_PASSWORD` is already
      printed offline — that's what closes the loop
- [ ] Schedule a quarterly DR rehearsal onto throwaway VMs

