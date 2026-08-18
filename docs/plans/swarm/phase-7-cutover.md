[← Phase 6](phase-6-observability.md) · [Design & decisions](design.md) · [Phase 8 →](phase-8-backup-v2.md)

---

# Phase 7 — Cutover

**Status: Not started**

- [ ] Point pfSense Unbound's `local-data` at the kube-vip VIP (`INGRESS_VIP`,
      set in Phase 3a.2) — a one-line change to the existing zone redirect
- [ ] Verify all seven hostnames — `home`, `kiosk`, `files`, `share`, `status`,
      `logs`, `metrics`. The plan said six before `share` existed; the wildcard
      makes the count irrelevant to the certificate but not to this check
- [ ] **The data cutover** (added while digesting
      [Phase 4](phase-4-data-tier.md), 4b.9 — the original bullet list rebuilt
      the machine holding the live database without ever moving its contents).
      Before rebuilding the old prod box, in order: stop the old host's API so
      nothing is writing to its Postgres, take a final backup with
      [`backup.sh`](../../../containers/backup/scripts/backup.sh) (post-4b.1, so it
      includes `aerie.dump`/`quartz.dump`), then re-run
      [`deploy/cluster/data/schema/restore-job.yaml`](../../../deploy/cluster/data/schema/restore-job.yaml)
      against that dump — `kubectl -n aerie create job --from=job/aerie-pg-restore
      aerie-pg-restore-cutover`. Only once that Job's sanity query passes and a
      hand comparison against the old host matches does the old box stop being
      the source of truth. This is a short deliberate outage — the dump is only
      consistent if nothing is writing to it — which is what makes this the
      cutover step rather than a Phase 4 concern.
- [ ] Rebuild the old prod box as the third k3s server and join it, restoring
      proper 3-node quorum
- [ ] **Raise the replica counts the two-node build window forced down**: the
      `LONGHORN_REPLICA_COUNT` variable to `3`, and (Phase 4, 4b.7)
      `POSTGRES_INSTANCES` to `3` alongside it — re-run Provision 4 for both (no
      commit — that's the point of the ConfigMap), the per-volume Phase 6
      classes to the [storage split](design.md#storage-split)'s 3-and-2, and confirm
      the third CNPG instance actually schedules rather than sitting Pending.
      Confirm Longhorn actually rebuilds onto the new node rather than reporting
      Degraded, which is the first real proof the third node is carrying load.
      **Also reconsider [`cluster.yaml`](../../../deploy/cluster/data/cluster/cluster.yaml)'s
      `synchronous.dataDurability: preferred`** (Phase 4, 4b.6) — it was set to
      avoid a write outage on every staggered reboot with only two instances and
      `number: 1`; with three instances a single node reboot no longer costs
      the synchronous quorum, and `required` (RPO=0) becomes affordable. Record
      the change and the reason, since `preferred` otherwise looks permanent
      rather than like a decision that was always meant to be revisited here.

