# Disaster Recovery

> **Deliberately short, and honest about it.** Phase 7 retired the Compose stack
> and the `backup` container that ran with it, and named the resulting gap rather
> than closing it. [Phase 8](plans/swarm/phase-8-backup-v2.md) is the rework. What
> follows describes what is true **today** — one thing is backed up, and the rest
> is not.

## What is backed up

**Postgres. That is the whole list.**

CloudNativePG archives Aerie's database to S3 through the barman-cloud plugin,
in two forms that only work together:

| | What | Where it's configured |
|---|---|---|
| Continuous WAL archiving | Every write, streamed to S3 as it is generated | [`cluster.yaml`](../deploy/cluster/data/cluster/cluster.yaml)'s `plugins:` block, `isWALArchiver: true` |
| Nightly base backup | 02:00 daily, a full physical copy | [`scheduledbackup.yaml`](../deploy/cluster/data/schema/scheduledbackup.yaml) |
| Destination | `s3://${WAL_BUCKET}/`, `base/` and `wals/` prefixes, gzip | [`objectstore.yaml`](../deploy/cluster/data/cluster/objectstore.yaml) |
| Retention | `30d`, pruned by barman itself | same |
| Credentials | the `cnpg-wal-s3` Secret, synced by External Secrets from SSM `/aerie/postgres/wal-s3-*` | [`docs/secrets-architecture.md`](secrets-architecture.md) |

The pair is what makes **point-in-time recovery** possible: a base backup gives
you a starting image, and the WAL stream replays forward from it to any moment
inside the retention window. A base backup alone would be a nightly snapshot with
up to 24 hours of loss behind it.

This has been rehearsed. Phase 4b.10's exit criterion was an actual PITR against
a throwaway Cluster with a `recoveryTarget.targetTime` before a marker change,
confirming the marker was absent — not a claim, a run.

**Replication is not backup.** `synchronous.dataDurability: required` across three
instances means an acknowledged write is on at least two nodes and a primary
failure loses nothing. It does nothing whatsoever about a `DROP TABLE`, which
replicates faithfully. The S3 archive is the only thing that answers that.

## What is not backed up

Everything else. Named individually, because a list is harder to forget than a
sentence:

| Not backed up | What is lost with it | Where it lives |
|---|---|---|
| Grafana's database | dashboards created in the UI, users, alert-rule state | `longhorn-r3` PVC |
| Uptime Kuma | monitor definitions and all history | `uptime-kuma-data`, `longhorn-r3` |
| Alertmanager | silences and notification state | `longhorn-r3` |
| OpenSearch | every log index | Longhorn, explicitly out of scope for HA |
| Prometheus | the TSDB — all metric history | Longhorn, same |
| The `/aerie/*` SSM tree | every seeded secret, if the AWS account goes | AWS Parameter Store |

Three mitigations blunt this, and none of them is a backup:

- **Grafana's dashboards are in git** as ConfigMaps, so the ones that matter are
  reprovisioned on a rebuild. Anything created by hand in the UI is not.
- **Kuma's monitors come from git**, as the `AUTOKUMA__STATIC_MONITORS`
  ConfigMap AutoKuma syncs in, so the monitor *set* comes back. Its history does
  not.
- **Longhorn replicates each volume three ways**, which survives a node loss and
  survives nothing else — see the note on replication above.

Closing this is [Phase 8](plans/swarm/phase-8-backup-v2.md): a restic CronJob to
S3, Longhorn's own backup target for the three `longhorn-r3` volumes, an export of
the parameter tree, and a backup-age alert that actually reaches a person.

## The recovery credentials

Two, and they are not interchangeable:

- **The CNPG archive** is read with the `aerie-cnpg` IAM user's keys, seeded from
  the `CNPG_AWS_*` repository secrets into SSM by Provision 2. A rebuild that can
  run Provision 2 can reach the archive.
- **The pre-cutover restic repos** are read with `RESTIC_PASSWORD`, seeded at
  `/aerie/backup/restic-password`. It is also **stored offline**, and that copy is
  the one that matters: restic encryption is not recoverable without it, and if
  the AWS account is what was lost, SSM went with it.

## The pre-cutover history

The old Compose stack's restic backups still exist and still hold everything the
table above says is unprotected — as of the cutover, and no later. Two repos:

- **The local repo on the old Docker host's data drive** (`E:\restic-repo` on
  this installation), on the machine that is now the third node's Hyper-V host.
  That drive was deliberately **not** reformatted during the Phase 7c rebuild, so
  the repo survived in place.
- **The restic S3 bucket**, the same repo's twin. Its location was an
  environment value on the retired `cd.yml`; Phase 8 re-establishes it as a
  config key rather than a remembered string, and until then it is whatever
  `RESTIC_REPOSITORY_S3` was on the old host.

Both carry a snapshot tagged **`cutover-final`** — the last complete image of the
old world, tagged in [7b.3](plans/swarm/phase-7-cutover.md) specifically so that a
future `restic forget` cannot reach it. Phase 8 inherits the obligation to pass
`--keep-tag cutover-final` to whatever prunes these.

Nothing has been written to either repo since the cutover. They are an archive,
not a backup.

## Restoring Postgres

**Recovery always bootstraps a new `Cluster`; it never acts in place.** That is
CNPG's model and it is a feature — a recovery is cheap to rehearse and impossible
to accidentally perform on the live database.

The shape, in outline:

1. Write a new `Cluster` manifest with a different `metadata.name`, a
   `bootstrap.recovery` block, and an `externalClusters` entry naming
   `serverName: aerie-pg` and the same `barmanObjectName: aerie-pg-wal` the live
   cluster uses.
2. For PITR, add `recoveryTarget.targetTime`. Omit it to recover to the end of
   the WAL stream.
3. Apply, and watch it bootstrap. Verify against the recovered instance directly.
4. Only then decide what to do with it — promote it by repointing the
   application, or extract what you need and delete it.

If the *live* cluster is the thing being recovered onto, the same manifest is what
`deploy/cluster/data/cluster/` should temporarily hold; commit it, let Flux apply
it, and remove the recovery block once the new cluster is the primary.

## Restoring from the pre-cutover archive

Only relevant for data that predates the cutover, or for the non-Postgres services
in the table above.

The tree still carries [`restore-job.yaml`](../deploy/cluster/data/schema/restore-job.yaml)
— a **suspended** Job that restores a `pg_dumpall` from the restic S3 repo and
replays it into the cluster's Postgres. It was the one-shot migration path in
Phase 4b.9 and it still works, with one caveat: the `aerie-pg-restore-restic`
Secret it reads was created **by hand** and is not in git. Recreate it from the
`/aerie/backup/*` parameters before unsuspending the Job. (Phase 8 replaces the
hand-made Secret with an `ExternalSecret` and deletes it.)

For anything else in those repos, restic is the tool and there is no wrapper:
mount or restore the repo from a machine that has the password, and put the files
where the workload expects them. There is no automation for this today, which is
the honest version of "Grafana is not backed up."

## Full disaster recovery

**If the cluster is gone but the S3 archive is not**, the rebuild is the ordinary
provisioning sequence — which is the whole argument for pull-based delivery
([`docs/delivery-architecture.md`](delivery-architecture.md)):

1. **Provision 0** per host — the node VM.
2. **Provision 5** per node — the Longhorn data disk, *before* the join.
3. **Provision 1** per node — k3s, first node then joins.
4. **Provision 2** — seed SSM. Needs the repository secrets, from wherever they
   are stored offline; GitHub Actions will not hand them back.
5. **Provision 3** — install Flux and point it at the repo.
6. **Provision 4** — plant the `aerie-cluster-config` ConfigMap.
7. **Wait.** Flux reconciles the entire tree: controllers, ingress, certificates,
   observability, the app. Nothing here is a manual apply.
8. **Recover Postgres** with the procedure above, against the surviving bucket.

Everything in the "not backed up" table comes back empty. That is the current
cost of a total loss, stated so nobody discovers it at 3am.

**If the AWS account is gone**, the CNPG archive and the restic S3 repo are both
gone with it — they share an account. What survives is the local restic repo, on a
machine in the house, holding pre-cutover data only. This is the gap Phase 8's
finding 3 exists to close, by making that repo a live destination again.

## Known gaps

Named, not solved. Each has an owner.

- **One copy of the only backup.** The CNPG archive is one bucket, one account,
  one region. *[Phase 8]*
- **Everything except Postgres.** See the table above. *[Phase 8]*
- **No alert on backup age.** If archiving stopped, nothing would say so until
  someone looked. This is the first alert that has to reach a person, which is why
  it is entangled with giving the notification flows teeth at all. *[Phase 8]*
- **The full-DR sequence above has not been rehearsed end to end** since the
  cutover. The Postgres half has (Phase 4b.10's PITR); the rebuild half is
  Provision workflows that have each run, but not in sequence against a cold
  cluster. *[Phase 8's rehearsal]*
- **Same building, same circuit.** The house's copy and the house are the same
  blast radius, which is why S3 is the second copy rather than the third.
  *[Design goal 6, [design.md](plans/swarm/design.md)]*
