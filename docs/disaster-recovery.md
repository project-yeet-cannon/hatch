# Disaster Recovery

> **Written to be followed, not read.** Every command below has been run against
> this installation — most of them on 2026-08-24, while
> [Phase 8](plans/swarm/phase-8-backup-v2.md) was closing. Where something has
> *not* been exercised it says so in the same voice, because a runbook that
> does not distinguish the two is a runbook that gets discovered to be fiction
> at 3am.

## What is backed up

Five rows, and the last one is a decision rather than an omission:

| What | How | Where it lands | Cadence |
|---|---|---|---|
| Postgres, physical + PITR | CNPG `ScheduledBackup` through the barman-cloud plugin ([`cluster.yaml`](../deploy/cluster/data/cluster/cluster.yaml)'s `plugins:` block, [`scheduledbackup.yaml`](../deploy/cluster/data/schema/scheduledbackup.yaml)) | `s3://${WAL_BUCKET}/`, AWS | continuous WAL + a base backup at 02:00 |
| Postgres, logical + portable | `pg_dump -Fc` of `aerie` and `quartz`, pushed by [`backup-cronjob.yaml`](../deploy/cluster/data/backup/backup-cronjob.yaml) | restic: **both** `${RESTIC_S3_REPOSITORY}` and the house share | 03:10 daily |
| The `/aerie/*` parameter tree | `aws ssm get-parameters-by-path --with-decryption`, into the same restic snapshot | same two repositories | 03:10 daily, same snapshot |
| Grafana / Uptime Kuma / Alertmanager volumes | Longhorn `RecurringJob` `aerie-critical-daily` against the three `longhorn-r3` volumes | `s3://${LONGHORN_BACKUP_BUCKET}@${AWS_REGION}/`, AWS | 02:00 daily, 7 retained |
| **Prometheus TSDB, OpenSearch indices** | **nothing, deliberately** | — | — |

**The last row is the one to read twice.** Metric history and log indices are
not backed up because they are the two largest volumes in the cluster and the
least valuable to restore: a metric series that resumes with a gap is a graph
with a gap, and an alert fires on the present, not on last month. Backing them
up would dominate the S3 bill and lengthen every restore to protect data whose
absence nobody would act on. [`design.md`](plans/swarm/design.md#storage-split)
is where that split was decided; this row exists so the decision is visible
from the document someone reads during an outage, rather than inferred from a
gap in a table.

**Replication is not backup.** `synchronous.dataDurability: required` across
three instances means an acknowledged write is on at least two nodes and a
primary failure loses nothing. It does nothing about a `DROP TABLE`, which
replicates faithfully. Longhorn's three replicas of each volume are the same
kind of protection against the same narrow kind of loss.

## The root of trust

**The printed `RESTIC_PASSWORD` is the whole of it**, and after Phase 8b.7 that
is more true than it used to be:

- restic encryption is not recoverable without that password. There is no
  escrow, no recovery key, and no support channel.
- The parameter tree — every secret this installation holds, including the
  copy of `RESTIC_PASSWORD` at `/aerie/backup/restic-password` — is now
  *inside* the restic snapshot. Which means the offline copy is the only thing
  that can open the box that contains the copy.
- **GitHub Actions secrets are write-only.** Provision 2 can seed SSM from
  them; nothing can read them back out. A rebuild that has lost both AWS and
  the offline password has lost the installation's secrets permanently, and
  every one of them has to be reissued from its source of truth (AWS, Route53,
  the registry, the camera passwords, and so on).

[8a.4](plans/swarm/phase-8-backup-v2.md) exists as a step because of this: the
offline copy was read off paper and used to open a repository, on a machine
that had never held it in an environment variable. Do that again whenever the
paper moves.

## Where the copies are

Three destinations, deliberately not three copies of the same dependency:

- **`${WAL_BUCKET}`** — CNPG's WAL and base backups. AWS.
- **`${RESTIC_S3_REPOSITORY}`** — the logical dumps and the parameter export.
  AWS, **same account** as the bucket above.
- **The house share** — `//${SHARE_HOST}/${SHARE_NAME}/${RESTIC_LOCAL_SUBPATH}`,
  a machine in the house, reached over SMB by
  [`local-repo-volume.yaml`](../deploy/cluster/data/backup/local-repo-volume.yaml)'s
  statically-provisioned PV and mounted at `/mnt/restic-local` by the backup
  and verify Jobs. **This copy is not in AWS.** It is the same repository the
  pre-cluster Compose stack wrote to — moved onto the share by
  [8a.2](plans/swarm/phase-8-backup-v2.md), history and all — so it holds both
  the current dailies and the `cutover-final` snapshot from the Phase 7
  cutover.

The rule of three only holds because of the third one. If the AWS account is
gone, the first two are gone together, and what remains is the share: both
databases, the parameter tree, and the pre-cluster archive — encrypted with the
password on the paper.

## How you find out something stopped

Not by looking. [`backup.yaml`](../deploy/cluster/observability/config/alerts/backup.yaml)
carries six alerts and five `absent()` siblings — one per backup path, plus one
that fires when a *metric* disappears, because a rule whose series is missing
is silent and silence is what a working backup looks like. The Delivery
dashboard's **Backups** row is the same state as four numbers.

When you silence something for maintenance: **silence by `alertname`, never by
receiver.** A receiver-wide silence eats the backup-age alert, which is exactly
the one the maintenance window is a reason to keep armed.

---

# Restore procedures

Four, in the order you are most likely to need them. Each is commands.

## 1. One database, from restic

The fastest path back to a known-good `aerie` or `quartz`, and the only one
that works when the AWS account is what was lost. **Restore to a scratch
Postgres first** unless you are certain: `pg_restore` into the live database is
destructive by design (`--clean --if-exists`), and the difference between the
two is one argument.

The weekly verify Job does exactly this against the local repository and is
the thing that keeps this procedure honest —
[`cluster-verify.sh`](../containers/backup/scripts/cluster-verify.sh) restores
the newest snapshot, loads `aerie.dump` into a scratch instance it stands up in
its own `/tmp`, and counts tables. To do it by hand:

```sh
# What is in the newest snapshot, and when it was taken. Host is always
# `aerie` and the tag is always `daily`; the paths are fixed, which is what
# makes `restic dump` below able to name a file.
restic -r /mnt/restic-local snapshots latest --json \
  | jq -r '.[] | "\(.short_id) \(.time) host=\(.hostname) tags=\(.tags|join(","))"'
restic -r /mnt/restic-local ls latest --json \
  | jq -r 'select(.struct_type=="node") | .path'
#   /tmp/aerie-backup/aerie.dump
#   /tmp/aerie-backup/parameters.json
#   /tmp/aerie-backup/quartz.dump

# Pull one dump out without restoring the whole snapshot.
restic -r /mnt/restic-local dump latest /tmp/aerie-backup/aerie.dump > aerie.dump

# Load it. --no-owner --no-privileges because the dump's owner role does not
# exist on a fresh cluster; CNPG generated whatever `aerie` is now.
pg_restore --no-owner --no-privileges -h <host> -p 5432 -U aerie -d aerie_scratch aerie.dump
```

`RESTIC_PASSWORD` comes from the offline copy. Inside the cluster it is also
the `restic` Secret in the `aerie` namespace, which is what every Job here
reads; a Job template that already has the credential, the repository and the
mount is
[`verify-cronjob.yaml`](../deploy/cluster/data/backup/verify-cronjob.yaml) —
copy it rather than assembling one.

**Onto the live cluster**, the tree carries
[`restore-job.yaml`](../deploy/cluster/data/schema/restore-job.yaml): a
suspended Job that restores the newest `daily` snapshot *from S3* and replays
both dumps into `aerie-pg`. Since 8b.10 it needs nothing created by hand — the
credential is the `restic` Secret, the repository arrives as
`${RESTIC_S3_REPOSITORY}` from `aerie-cluster-config`. Its own header carries
the copy-to-a-new-name command; `kubectl patch ... suspend=false` races Flux
and is the wrong way in.

> **Not yet exercised against the current Secret.** The Job was rewired in
> 8b.10 and has not been run since — running it writes over the live
> databases, so it is a deliberate human act, and the honest place to prove it
> is the quarterly rehearsal against a scratch Postgres rather than a Tuesday
> against production.

## 2. Postgres, to a point in time, from CNPG

**Recovery always bootstraps a new `Cluster`; it never acts in place.** That is
CNPG's model and it is a feature — the rehearsal is cheap and cannot be
performed on the live database by accident. This is the shape
[4b.10](plans/swarm/phase-4-data-tier.md) rehearsed, in the plugin form the
cluster actually runs:

```yaml
apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata:
  name: aerie-pg-pitr          # a NEW name. Never the live one.
  namespace: aerie
spec:
  instances: 1                 # a recovery target, not a production cluster
  storage:
    storageClass: local-path
    size: 20Gi
  bootstrap:
    recovery:
      source: aerie-pg
      recoveryTarget:
        # Omit the whole recoveryTarget block to recover to the end of the
        # WAL stream. With it, any moment inside the 30d retention window.
        targetTime: "2026-08-24 02:30:00+00"
  externalClusters:
    - name: aerie-pg
      plugin:
        name: barman-cloud.cloudnative-pg.io
        parameters:
          barmanObjectName: aerie-pg-wal   # ../deploy/cluster/data/cluster/objectstore.yaml
          serverName: aerie-pg             # the *source* server's name in the bucket
```

```sh
kubectl apply -f pitr.yaml
kubectl -n aerie get cluster aerie-pg-pitr -w        # watch it bootstrap
kubectl -n aerie exec -it aerie-pg-pitr-1 -- psql -U postgres aerie -c '<your check>'

# Then decide: extract what you need, or repoint the application at it.
kubectl -n aerie delete cluster aerie-pg-pitr        # when you are done
```

The base backup is the floor and the WAL is the resolution: PITR can reach any
instant after the oldest surviving base backup, which is why the
`CNPGBackupTooOld` alert is a critical and not a warning.

## 3. One Longhorn volume, from a backup

**Verified end to end on 2026-08-24** — restored from the S3 backup target into
a new volume, mounted, and read.

```sh
# 1. Find the backup. The objects carry the PVC name in their KubernetesStatus
#    label, which is how you tell Grafana's from Kuma's.
kubectl -n longhorn-system get backups.longhorn.io -o json \
  | jq -r '.items[] | "\(.metadata.name)  \(.status.backupCreatedAt)  \(.status.labels.KubernetesStatus | fromjson | .pvcName)"'

# 2. Take that backup's restore URL verbatim - do not assemble it by hand.
kubectl -n longhorn-system get backups.longhorn.io <backup-name> -o jsonpath='{.status.url}'
#   s3://${LONGHORN_BACKUP_BUCKET}@${AWS_REGION}/?backup=<backup-name>&volume=<pv-name>
```

```yaml
# 3. A new Longhorn volume from that URL. Never the name of a live volume.
apiVersion: longhorn.io/v1beta2
kind: Volume
metadata:
  name: kuma-restore            # any new name
  namespace: longhorn-system
spec:
  fromBackup: "<the URL from step 2>"
  size: "1073741824"            # status.volumeSize of the backup, in bytes
  numberOfReplicas: 3
  frontend: blockdev
  dataEngine: v1
```

```yaml
# 4. A static PV and PVC so a pod can mount it. storageClassName: "" on both -
#    this volume already exists, so nothing should provision one.
apiVersion: v1
kind: PersistentVolume
metadata:
  name: kuma-restore
spec:
  capacity: { storage: 1Gi }
  accessModes: ["ReadWriteOnce"]
  volumeMode: Filesystem
  persistentVolumeReclaimPolicy: Retain
  storageClassName: ""
  csi:
    driver: driver.longhorn.io
    fsType: ext4
    volumeHandle: kuma-restore        # the Volume's metadata.name
    volumeAttributes:
      numberOfReplicas: "3"
      staleReplicaTimeout: "30"
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: kuma-restore
  namespace: default
spec:
  accessModes: ["ReadWriteOnce"]
  storageClassName: ""
  volumeName: kuma-restore
  resources:
    requests: { storage: 1Gi }
```

Mount it from a throwaway pod and look before you promote anything. The volume
restores lazily — it reports `state: detached` almost immediately and the data
arrives when something attaches it, so an empty-looking volume 30 seconds in is
normal.

**What you get back is a frozen filesystem snapshot, not an application-level
export.** `freeze-filesystem-for-snapshot` is on
([`longhorn.yaml`](../deploy/cluster/infrastructure/controllers/longhorn.yaml)),
so `fsfreeze` runs before the snapshot and the image is filesystem-consistent
rather than merely crash-consistent. SQLite recovers from it exactly as it
recovers from power loss — the restored Kuma volume came back with `kuma.db`,
`kuma.db-wal` and `kuma.db-shm` beside each other, which is the supported path
and not a hope. The whole argument for putting these three volumes on
Longhorn's backup target rather than into restic rests on that setting: check
it before trusting this procedure.

```sh
# 5. Clean up the rehearsal. In this order.
kubectl -n default delete pod <the pod>; kubectl -n default delete pvc kuma-restore
kubectl delete pv kuma-restore
kubectl -n longhorn-system delete volumes.longhorn.io kuma-restore
```

## 4. The parameter tree, from the export

Every secret this installation holds, readable with nothing but the offline
password and a copy of the repository. **Verified on 2026-08-24**: 21
parameters, which is every `required: true` entry in
[`parameters.json`](../scripts/secrets/parameters.json).

```sh
# The export is one file inside the daily snapshot, at a fixed path - which is
# the reason cluster-backup.sh stages into /tmp/aerie-backup rather than a
# mktemp directory.
restic -r <repo> dump latest /tmp/aerie-backup/parameters.json > parameters-export.json

# What is in it, without putting a single value on a terminal or in a log:
jq 'length' parameters-export.json
jq -r '.[].Name' parameters-export.json

# One value, when you actually need one. This prints a secret - do it in a
# shell whose history you control, and not inside a CI job.
jq -r '.[] | select(.Name=="/aerie/backup/restic-password") | .Value' parameters-export.json
```

The file is written with `umask 077` and restic preserves permissions, so a
restore lays it back down `0600`. Keep it that way, and delete it when you are
done: it is the one artifact in this document that is more sensitive than the
backups themselves.

To put the tree back, re-run **Provision 2** from the repository secrets rather
than replaying this file — the seeding path is the one that is tested. This
export is for reading a value back when Parameter Store is gone, and for
rebuilding the repository secrets when the offline copies of *those* are gone.

---

# Full disaster recovery

**If the cluster is gone but AWS is not**, the rebuild is the ordinary
provisioning sequence — which is the whole argument for pull-based delivery
([`delivery-architecture.md`](delivery-architecture.md)):

1. **Provision 0** per host — the node VM.
2. **Provision 5** per node — the Longhorn data disk, *before* the join.
3. **Provision 1** per node — k3s, first node then joins.
4. **Provision 2** — seed SSM. Needs the repository secrets from wherever they
   are stored offline; GitHub Actions will not hand them back.
5. **Provision 3** — install Flux and point it at the repo.
6. **Provision 4** — plant the `aerie-cluster-config` ConfigMap.
7. **Provision 6** — the backup bucket, the `aerie-longhorn` user, and the two
   IAM policy documents. Only if the AWS side is being rebuilt too.
8. **Wait.** Flux reconciles the entire tree: controllers, ingress,
   certificates, observability, the app. Nothing here is a manual apply.
9. **Recover Postgres** with procedure 2 against the surviving bucket, or
   procedure 1 if the restic repositories are what survived.
10. **Restore the three volumes** with procedure 3 — Grafana's dashboards come
    back from git either way, but its users, its alert-rule state, Kuma's
    monitor history and Alertmanager's silences do not.

Prometheus's metric history and OpenSearch's log indices come back empty. That
is the cost of the decision in the table at the top, stated here so nobody
discovers it during the rebuild.

**If the AWS account is gone**, the CNPG archive and the restic S3 repository
are gone with it — they share an account. What survives is the share copy:
both databases as of last night, the parameter tree as of last night, and the
pre-cutover archive. Procedures 1 and 4 are the two that still work, and
`RESTIC_PASSWORD` off the paper is what opens them.

## The pre-cutover archive

Both restic repositories still carry a snapshot tagged **`cutover-final`** —
the last complete image of the old Compose world, including Uptime Kuma's
SQLite and a `pg_dumpall` that exists nowhere else. It is protected from the
nightly `forget` by `--keep-tag cutover-final`, and
[`cluster-backup.sh`](../containers/backup/scripts/cluster-backup.sh) asserts
before and after every prune that the tagged set did not change — deliberately
against the local repository *first*, so a retention change that would eat it
aborts the run before the S3 copy is pruned too.

Those snapshots are also permanently outside the retention policy for a second,
accidental reason: they were written by the compose-era script, which staged
into `mktemp -d`, so each has a unique path and lands in a `--group-by
host,paths` group of one. Harmless, and worth knowing before someone
investigates why they never expire.

## Known gaps

Named, not solved.

- **`restore-job.yaml` has not been run against the `restic` Secret** it was
  rewired to in 8b.10. Prove it in a rehearsal, onto a scratch Postgres, before
  deleting the hand-made `aerie-pg-restore-restic` Secret it replaced.
- **The full-DR sequence above has not been rehearsed end to end.** Its parts
  have: PITR in 4b.10, the restic restore weekly, the Longhorn restore on
  2026-08-24. The sequence has not.
- **Same building, same circuit.** The share and the cluster are one blast
  radius, which is why AWS is the second copy rather than the third —
  [design.md](plans/swarm/design.md) goal 6.
- **Kuma's watchdog push has never completed a cycle** (404, `Monitor not found
  or not active`), so the dead-man's switch that would notice Alertmanager
  itself dying is not armed. The rest of the alert path is proven end to end;
  this one hop is not.
