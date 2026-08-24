[← Phase 7](phase-7-cutover.md) · [Design & decisions](design.md) · [Phase 9 →](phase-9-productization.md)

---

# Phase 8 — Backup v2 + rehearsal

> Re-scoped once, against a repository that changed underneath the original six
> bullets. Those bullets were written before Phase 4 existed and before Phase 7
> deleted anything, and three facts about what actually got built move most of
> the work:
>
> - **"CNPG/S3 for Postgres" already shipped.** [4b.5](phase-4-data-tier.md)'s
>   `ObjectStore`, [4b.10](phase-4-data-tier.md)'s nightly `ScheduledBackup` and
>   the PITR rehearsal that proved it are in the tree and running. This phase
>   must not re-do it. What it owes Postgres is the *other* copy — logical,
>   portable, and not in the same AWS account — which is a different artifact
>   for a different failure.
> - **Phase 7 deleted the only running backup, and named the gap rather than
>   closing it.** From [7b.2](phase-7-cutover.md#phase-7b--the-cutover) onward
>   the only thing backed up anywhere is Postgres, physically, into one bucket
>   under one set of credentials. Kuma's monitor history, Grafana's database,
>   Alertmanager's silences and the `/aerie/*` parameter tree have **no copy at
>   all**. That is what decides this phase's order: restic and its alert first,
>   Longhorn second, rehearsal last.
> - **The alerting flows have no teeth.** Phase 7 says so explicitly and hands
>   the wiring here, because the backup-age alert is the first one that has to
>   arrive somewhere. An alert that fires into a receiver nobody has ever tested
>   is the same category of object as a `--keep-tag` whose pruner does not know
>   about it: paperwork that looks like protection.
>
> Two of the original bullets survive unchanged (`--keep-tag cutover-final`, the
> `backup/*` `ExternalSecret`), two get sharper (the alert, the parameter
> export), one splits in half (Postgres is done; volumes are not), and one grows
> a prerequisite (a rehearsal needs a document worth rehearsing, and
> [7c.10](phase-7-cutover.md#phase-7c--the-old-server-docker-host--k3s-node)
> left `docs/disaster-recovery.md` deliberately minimal).
>
> Eight findings, and the first two are the ones that decide the architecture.

## Findings

1. **No single pod can snapshot both SQLite databases, and neither image ships
   `sqlite3`.** Grafana (`longhorn-r3`, 2Gi,
   [kube-prometheus-stack.yaml](../../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml))
   and Uptime Kuma (`longhorn-r3`, `uptime-kuma-data`,
   [uptime-kuma.yaml](../../../deploy/cluster/observability/controllers/uptime-kuma.yaml))
   are separate Deployments whose PVCs are `ReadWriteOnce`. RWO means *one
   node*, not one pod — a second pod **on the same node** may co-mount the
   volume read-only — but nothing places these two workloads on the same node,
   so no one CronJob can reach both. The `kubectl exec` escape (run
   `sqlite3 .backup` inside the workload's own container) fails on a different
   axis: neither the Grafana nor the Kuma image carries a `sqlite3` binary, and
   `tar cf - grafana.db` out of a live container is precisely the live-file copy
   [Phase 0](phase-0-backup-and-dr.md) refused to do.

   **This is what puts the two SQLite volumes on Longhorn's backup target
   rather than into restic**, and it is only a *correct* answer with the second
   half of the finding attached: Longhorn's `freezeFilesystemForSnapshot`
   setting calls `fsfreeze` before taking the snapshot, which turns a
   crash-consistent block image into a filesystem-consistent one. Phase 0's
   rule — "back up correctly per service, not by copying volume directories" —
   was aimed at `cp` racing a writer. A frozen, atomic, whole-filesystem
   snapshot is not that: SQLite recovers from it exactly as it recovers from
   power loss, which is a supported path rather than a hope. Without the freeze
   setting this design is worse than Phase 0's and should not be built.

2. **`recurringJobSelector` cannot be added to the StorageClasses, and
   Longhorn's `default` group backs up the wrong things.** Longhorn selects
   volumes for a `RecurringJob` by label on the **Volume** CR, and a
   StorageClass's `recurringJobSelector` parameter only stamps that label at
   volume-creation time. A StorageClass's `parameters` are immutable
   ([longhorn-storageclasses.yaml](../../../deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml)
   already carries the whole argument), and even a delete-and-recreate would not
   reach the volumes that already exist. The documented shortcut — a
   `RecurringJob` in the `default` group, which applies to every volume carrying
   no recurring-job label of its own — is worse than useless here: it sweeps in
   Prometheus's TSDB and OpenSearch's indices, the two volumes
   [design.md](design.md#storage-split) says are not worth backing up and the
   two that would dominate the S3 bill.

   The wanted set is exactly `longhorn-r3` — Grafana, Kuma, Alertmanager — which
   is the split the class names already encode. So the selection is **a label
   applied by hand, once, to three existing Volume CRs** (8a.3), and asserted by
   the phase gate so that a volume recreated later without it is a failed check
   rather than a silent gap.

3. **The local repo already has a home, and it is 7c.1's copy.**
   [7c.1](phase-7-cutover.md#phase-7c--the-old-server-docker-host--k3s-node)
   copies `E:/restic-repo` onto the house share and says to keep it "until Phase
   8's cluster backup has taken **and verified** its own first snapshot". Do not
   retire it — **make it the local repo.** Mounted over `smb.csi.k8s.io` (the
   driver [5b.3](phase-5-app-tier.md) already registered, the PV shape
   [share-volumes.yaml](../../../charts/aerie/templates/share-volumes.yaml)
   already establishes), the same repo keeps the entire pre-cluster history,
   keeps `cutover-final` in two places instead of one, and turns 7c.1's expiry
   condition into a no-op rather than a decision someone has to remember to
   make. The rule-of-three from
   [design.md](design.md#goals) is only satisfied if this copy exists: the CNPG
   bucket and the restic S3 bucket are both in the same AWS account, so the
   share is the only copy that survives "the AWS account is gone".

4. **`aerie-restic`'s IAM policy has to grow, or the parameter export needs a
   second identity.** The export reads `/aerie/*` `SecureString` values, which
   needs `ssm:GetParametersByPath` and `kms:Decrypt`; `aerie-restic` is scoped
   to the backup bucket alone. Grow the existing policy rather than minting a
   third user: after this phase the repository that identity writes to
   *contains* the tree, so a credential that can read the tree and a credential
   that can read the repo holding the tree have the same blast radius, and one
   fewer IAM user is one fewer thing to rotate. The alternative — an
   `aerie-ssm-export` user, isolated the way `aerie-eso` and `aerie-restic` are
   from each other — buys nothing once the two sets of bytes have been made
   equivalent, and this note is here so that reasoning is on the record rather
   than inferred from a policy diff.

5. **Three of the old repository's variables died with `cd.yml`.**
   `RESTIC_S3_REPOSITORY`, `RESTIC_AWS_REGION` and the local repo path were
   environment values [`compose.backup.yml`](phase-7-cutover.md) read and
   `cd.yml` supplied; 7b.9 deleted both. They come back as
   [`cluster-config.json`](../../../scripts/k3s/cluster-config.json) keys, and
   **the region key does not come back at all** — a regional endpoint inside the
   repository string (`s3:s3.us-east-1.amazonaws.com/<bucket>`) carries it, and
   one key that cannot disagree with itself beats two that can.

6. **`pg_dumpall` cannot come across, and that is correct.** CNPG disables the
   superuser role by default, so the cluster-wide dump
   [backup.sh](../../../containers/backup/scripts/backup.sh) takes today has no
   credential to run under. It is also the wrong artifact:
   [4b.1](phase-4-data-tier.md) added the two custom-format per-database dumps
   precisely because `CREATE ROLE`/`CREATE DATABASE` collide with what CNPG's
   own bootstrap created. Both databases are owned by the `aerie` role
   ([quartz-database.yaml](../../../deploy/cluster/data/schema/quartz-database.yaml)'s
   `owner: aerie`), which is the credential in the CNPG-generated
   `aerie-pg-app` Secret — so `pg_dump -Fc aerie` and `pg_dump -Fc quartz` come
   across unchanged, the globals do not come across at all, and
   [restore.sh](../../../deploy/cluster/data/schema/restore.sh) already expects
   exactly those two files.

7. **One repo, several clients, one exclusive lock.** Backup, verify and export
   are three restic invocations against the same two repositories, and
   `forget --prune` takes an exclusive lock that a concurrent `backup` will
   refuse to wait behind. **Retention is owned by exactly one job**, and the
   schedules are staggered around CNPG's existing 02:00 `ScheduledBackup` rather
   than chosen independently of it.

8. **Every backup in this system is a Kubernetes CronJob or a CNPG object**,
   which answers the question [6b.8](phase-6-observability.md) left open —
   "whether that metric comes from a textfile-collector-style sidecar or a small
   exporter" — with **neither**. kube-state-metrics is already scraping the
   cluster and already emits `kube_cronjob_status_last_successful_time` and
   `kube_job_status_failed` for the restic CronJob *and* for the Kubernetes
   CronJob Longhorn creates behind a `RecurringJob`; CNPG's own collector
   already emits the Postgres backup and WAL-archiver timestamps
   ([cloudnative-pg.yaml](../../../deploy/cluster/infrastructure/controllers/cloudnative-pg.yaml)'s
   note 3 says so). The backup-age alert is a `PrometheusRule` and nothing else
   — no new workload, no push gateway, no scrape target.

   Two metric names in that paragraph are asserted from documentation rather
   than read off this cluster. **Check them before trusting them** — the failure
   mode of a wrong metric name in a `PrometheusRule` is a rule that never fires,
   which is indistinguishable from a system that is fine, and this is the phase
   where that distinction is the whole product. 8b.11 says how.

9. **A CronJob's pod name is restic's snapshot hostname, and that silently
   disables retention.** Found by running `cluster-backup.sh` ten times against
   a throwaway repository and noticing that a deliberately broken retention
   removed nothing. restic stamps each snapshot with the machine's hostname; in
   Kubernetes that is the pod name, unique to every run of a CronJob. `restic
   forget` groups snapshots before applying its policy and **groups by
   `host,paths` by default**, so a per-run hostname puts every night's snapshot
   in a group of one, where `--keep-daily 7` dutifully keeps all seven of the
   single snapshot it can see. Ten runs left ten snapshots; every `forget`
   reported success.

   The failure has no symptom until the bill or the share fills up, and it
   defeats 8b.6 from the other direction too — a `--keep-tag` protecting a
   snapshot from a policy that never runs is protecting it from nothing. The
   fix is one flag, `restic backup --host aerie`, which also restores restic's
   parent-snapshot lookup (keyed on the same host+paths pair, and without it
   every run logs "no parent snapshot found" and re-reads both dumps in full).
   With it, the same ten runs leave three snapshots. `--group-by host,paths` is
   written on the `forget` explicitly rather than left to the default, because
   the whole argument depends on it.

   **The `paths` half of the same finding, seen on the real repositories.** The
   six snapshots 8a.2's repository already holds were written by the compose-era
   script, which staged into `mktemp -d` — so their paths are
   `/tmp/tmp.hFmFIa/aerie.dump`, `/tmp/tmp.MlfIcE/aerie.dump`, and four more,
   every one unique. Under `--group-by host,paths` that is six groups of one,
   and a `forget --dry-run` with the full policy confirms it: **0 removals, 6
   keep-groups, 6 snapshots, in both repositories.** The pre-cluster history is
   therefore permanent rather than merely protected — `cutover-final` is
   safe by tag *and* by being alone in its group — which is benign, since the
   set is closed and no new `mktemp` snapshot will ever join it, but it is a
   fixed floor (~1.7 GiB local, and in S3 a 2.634 GiB snapshot carrying the
   OpenSearch and Prometheus data [design.md](design.md#storage-split) says is
   not worth backing up) that should be known rather than rediscovered as "why
   won't these expire".

   Worth recording for a second reason: **8b.3's fixed `/tmp/aerie-backup`
   staging path is what makes the going-forward case work**, and that change
   was justified purely on `restic dump` addressability — being able to name
   the file in a recovery. It turns out to be load-bearing for retention
   grouping as well, which nobody knew when it was made. Two independent
   reasons for one decision, the second discovered three steps later.

## What this phase is

Three backup paths, one alert family, one rehearsal:

| What | How | Where it lands |
|---|---|---|
| Postgres, physical + PITR | CNPG `ScheduledBackup` (Phase 4, unchanged) | WAL bucket, AWS |
| Postgres, logical + portable | `pg_dump -Fc`, restic CronJob | restic S3 **and** the share |
| `/aerie/*` parameter tree | `aws ssm get-parameters-by-path`, same CronJob | restic S3 **and** the share |
| Grafana / Kuma / Alertmanager volumes | Longhorn `RecurringJob` → backup target | Longhorn bucket, AWS |
| Prometheus TSDB, OpenSearch indices | **nothing, deliberately** | — |

## Phase 8a — Manual prerequisites

- [x] **1. Two AWS changes, neither of them a new bucket policy you can skip**

      **A bucket and a user for Longhorn.** A dedicated bucket, for the reason
      [objectstore.yaml](../../../deploy/cluster/data/cluster/objectstore.yaml)
      gives for the WAL one: Longhorn owns its own `backupstore/` layout and
      expects to be the only writer, and a lifecycle rule written for one
      layout applied to another is how a restore discovers a missing object.
      A dedicated `aerie-longhorn` IAM user scoped to it, same isolation
      discipline as `aerie-restic` and `aerie-eso`.

      **A policy addition for `aerie-restic`.** `ssm:GetParametersByPath` and
      `ssm:GetParameters` on **both**
      `arn:aws:ssm:<region>:<account>:parameter/aerie` and
      `…:parameter/aerie/*` — the bare path ARN is not optional, for the
      reason [`iam/aerie-eso.policy.json`](../../../scripts/secrets/iam/aerie-eso.policy.json)
      already carries: `GetParametersByPath` authorizes against the *path*,
      which `/aerie/*` does not match, so the natural one-ARN policy denies
      the export with `is not authorized to perform: ssm:GetParametersByPath`.
      Plus `kms:Decrypt` on the key the `SecureString` values were sealed with
      — scoped by `kms:ViaService` rather than by key id, which covers
      `alias/aws/ssm` without naming a key that changes if Provision 2 is ever
      pointed at a CMK. Finding 4 is why this is a policy addition rather than
      a fourth user; it is an *addition* rather than an edit because Phase 0's
      own document for that user is not committed here, and `put-user-policy`
      overwrites by name.

      **Both halves are scripted.** Actions -> *Provision 6: Backup AWS
      resources*, or
      [`Set-AerieBackupAws.ps1`](../../../scripts/secrets/Set-AerieBackupAws.ps1)
      by hand. It creates and configures the bucket, creates
      `aerie-longhorn`, and delegates the two policies to
      [`Set-AerieSecretsIam.ps1`](../../../scripts/secrets/Set-AerieSecretsIam.ps1)
      and the committed documents in
      [`scripts/secrets/iam/`](../../../scripts/secrets/iam/) — so this step
      is a dispatch rather than a console session, and re-runnable by the next
      operator. What stays manual is `aerie-longhorn`'s **access key**, for
      the reason that script has always given: minting one means printing one.
      The one new thing it asks for is `PROVISION_AWS_*`, an operator
      credential more powerful than anything else this repository stores —
      [`scripts/secrets/README.md`](../../../scripts/secrets/README.md#phase-8s-backup-bucket-and-the-one-credential-that-outranks-the-others)
      carries how to scope it and why deleting it between runs is the cheapest
      posture.

      *Exit:* `aws s3 ls s3://<longhorn-bucket>` succeeds as `aerie-longhorn`
      and fails as `aerie-restic`; `aws ssm get-parameters-by-path --path
      /aerie --recursive --with-decryption` returns values as `aerie-restic`.
      `-Stage verify-only` asserts exactly these, and asserts them twice —
      once through `iam simulate-principal-policy`, which needs no access key
      and so can run *before* Longhorn's is minted, and once literally as each
      identity, which is the authority and the only half that would also catch
      a bucket policy or an SCP.

- [x] **2. Put 7c.1's copy where the cluster will look for it**

      Finding 3. Move (do not re-copy) the verified copy of the old
      `E:/restic-repo` to a fixed subdirectory of the house share —
      **`restic-repo/`** at the share root, which is the value 8b.2's
      `RESTIC_LOCAL_SUBPATH` takes. The directory keeps the name it had on the
      old server rather than being shortened to `restic/`: 7c.1 and
      `compose.backup.yml` before it already called the repository by that
      name, so keeping it means the path is one string across the pre-cluster
      history and the post-cluster config, and there is no window in which a
      half-renamed 1 GiB directory exists on the share.
      It must be reachable at `//${SHARE_HOST}/${SHARE_NAME}/restic-repo` with
      the credential the `smb-share` Secret already holds, and it must be
      writable by that identity, which the read-only half of
      [share-volumes.yaml](../../../charts/aerie/templates/share-volumes.yaml)
      is a reminder to check rather than assume.

      *Exit:* `restic -r <path> snapshots --tag cutover-final` lists the
      snapshot 7b.3 tagged, from a machine that is not the old server.

      **Done 2026-08-23.** Verified from `aerie-node-0` through a throwaway pod
      mounting the existing `aerie-share-rw` PVC — the same SMB path and the
      same `smb-share` credential 8b.4's PV will use, rather than a hand-made
      mount that would have proved a different thing. `3bb9e508`
      (`daily,cutover-final`, 2026-08-20) lists; the whole history came across
      (6 snapshots, 957.8 MiB, 66 pack files); `restic check` reports no errors
      over all packs, snapshots, trees and blobs; and the repository is
      writable by that identity — `check` took and released its exclusive lock,
      and `locks/` is empty afterward. Not checked: whether `E:/restic-repo` on
      the old server is actually gone, which is the *move* half rather than the
      copy half and needs that host's filesystem, not its share.

- [x] **3. Label the three `longhorn-r3` Volume CRs**

      Finding 2. For each of the Volumes backing Grafana, `uptime-kuma-data` and
      Alertmanager — the three PVCs whose `storageClassName` is `longhorn-r3`:

      ```sh
      kubectl -n longhorn-system label volume <vol> \
        recurring-job-group.longhorn.io/aerie-critical=enabled
      ```

      Volume names are the PV names, not the PVC names —
      `kubectl get pvc -A -o custom-columns=NS:.metadata.namespace,PVC:.metadata.name,PV:.spec.volumeName,SC:.spec.storageClassName`
      is the mapping, and filtering it on `longhorn-r3` is also the check that
      the set is still three and not four.

      *Exit:* exactly three Volumes carry the label, and every `longhorn-r3`
      PVC's Volume is one of them. 8b.16 asserts this permanently; the reason it
      is a gate assertion rather than a one-time step is that a volume recreated
      by a restore comes back **without** the label and with no error anywhere.

      **Done 2026-08-23.** The mapping still reads three and not four:
      `kube-prometheus-stack-grafana` ->
      `pvc-5924bbc8-a067-4ff8-a0d3-a859160efd37`, `uptime-kuma-data` ->
      `pvc-0f0d44e7-9829-423d-91c1-236cb7dbc7bb`, and Alertmanager's
      `alertmanager-kube-prometheus-stack-alertmanager-db-...-0` ->
      `pvc-78197967-bd29-4e68-889c-f6608ba0b0ff`. All three are labelled, the
      label selects exactly those three, and the two sets are equal in both
      directions — which is the half of the exit condition that a count alone
      would not have proved.

      One correction to finding 2, found here rather than at 8b.10 where it
      would have mattered more: the `default` group is **not** implicit on these
      volumes. Longhorn has stamped
      `recurring-job-group.longhorn.io/default=enabled` explicitly on all five
      Longhorn volumes, Prometheus's and OpenSearch's included, so the three
      critical volumes now carry two group labels rather than one. That is inert
      today — `kubectl -n longhorn-system get recurringjob` is empty, and 8b.10
      creates its job in `aerie-critical` — and the labels were left alone
      rather than stripped, because stripping `default` from these three would
      not have protected the two volumes finding 2 is actually worried about.
      What changes is the shape of the trap: a `default`-group job added later
      sweeps in the TSDB and the indices *and* double-backs-up the three, and
      nothing in the label state warns about it. If that is worth closing, it is
      closed by never creating a `default` job, not by a label.

- [x] **4. Prove the offline `RESTIC_PASSWORD` copy still exists and still works**

      Not "confirm you have it". Read it off the paper (or out of the safe, or
      wherever [Phase 0](phase-0-backup-and-dr.md) put it), type it, and open a
      repo with it. An offline copy nobody has exercised since the day it was
      printed is a hypothesis, and this is the last phase in which discovering
      it is wrong is cheap — the parameter store still holds a working copy
      today, and 8b.7 is about to make the printed one the only thing that can
      decrypt the export of that store.

      *Exit:* `restic -r <s3 repo> snapshots` succeeds with `RESTIC_PASSWORD`
      typed from the offline copy, on a machine that has never held it in an
      environment variable.

- [x] **5. Confirm 6a.3's Home Assistant automation reaches a person**

      Finding 8's other half, and the prerequisite 8b.12 cannot supply for
      itself. `POST` a hand-rolled body to
      `http://${HA_HOST}:${HA_PORT}/api/webhook/${HA_ALERT_WEBHOOK_ID}` and
      confirm a notification lands on a phone. If the automation fires and
      notifies nothing, fix that here — 8b.12's test-fire is meant to prove the
      *Alertmanager* half of the path, and a single test that can fail in two
      places proves neither.

      *Exit:* a notification arrives on a device a person carries.

## Phase 8b — Scriptable, in this order

Steps 1–4 are the plumbing every later step reads: credentials, config keys, the
image, the volume. 5–8 are the restic path and the parameter export. 9–10 are
Longhorn. 11–12 are the alert and the thing that makes an alert mean something.
13–15 are the paperwork the rehearsal needs, and 16 is the gate.

- [x] **1. Flip three deferrals, add two parameters, regenerate** —
      [`parameters.json`](../../../scripts/secrets/parameters.json),
      [`New-ExternalSecrets.ps1`](../../../scripts/secrets/New-ExternalSecrets.ps1).

      **The three `backup/*` entries lose `kubernetesDeferred` and gain
      `kubernetes`.** This is the bullet this phase inherited from
      [3b.6](phase-3-platform-services.md), and the note each entry carries is
      the thing being redeemed: "seeded but consumed by nothing" has been a
      decision in writing since Phase 2, and this is where it stops being one.
      All three land in **`aerie`**, secretName `restic`:

      | Parameter | secretKey |
      |---|---|
      | `backup/restic-password` | `password` |
      | `backup/s3-access-key-id` | `access-key-id` |
      | `backup/s3-secret-access-key` | `secret-access-key` |

      `aerie`, not a new `backup` namespace, and the reason is
      [restore-job.yaml](../../../deploy/cluster/data/schema/restore-job.yaml):
      it is in `aerie`, it reads the same three values, and 8b.10 deletes the
      hand-made Secret standing in for them. One namespace means one Secret
      serves both the thing that writes backups and the thing that reads them,
      and the CronJob needs `aerie` anyway to reach `aerie-pg-app` and
      `aerie-pg-rw` (finding 6).

      **Two new parameters for Longhorn**, `required: true`, `phase: 8`,
      landing in `longhorn-system` as secretName `longhorn-backup-target`:

      | Parameter | env | githubKind | secretKey |
      |---|---|---|---|
      | `longhorn/backup-s3-access-key-id` | `LONGHORN_AWS_ACCESS_KEY_ID` | variable | `AWS_ACCESS_KEY_ID` |
      | `longhorn/backup-s3-secret-access-key` | `LONGHORN_AWS_SECRET_ACCESS_KEY` | secret | `AWS_SECRET_ACCESS_KEY` |

      The `secretKey` values are SCREAMING_SNAKE_CASE against every other entry
      in the file, and they have to be: Longhorn reads its
      `backupTargetCredentialSecret` by those exact key names and silently
      reports an unusable backup target if they are anything else. `AWS_ENDPOINTS`
      is the third key Longhorn documents and is deliberately absent — it exists
      for non-AWS S3, and setting it empty is not the same as omitting it.

      The four-part `kubernetesDeferred` ordering [4b.3](phase-4-data-tier.md)
      established **does** apply to the two new parameters and does not apply to
      the three flips: the generator refuses a `kubernetes` block on a parameter
      no run has seeded, so the new pair is seed-then-manifest (edit, dispatch
      Provision 2, then generate and commit) while the `backup/*` three — seeded
      on every run since Phase 2 — are one edit and one `New-ExternalSecrets.ps1`.

      *Exit:* `New-ExternalSecrets.ps1 -Check` exits 0; `kubectl -n aerie get
      externalsecret restic` and `kubectl -n longhorn-system get externalsecret
      longhorn-backup-target` both report `SecretSynced`; the generated
      `aerie-restic.yaml` carries three keys and `longhorn-system-longhorn-backup-target.yaml`
      two.

      **Done 2026-08-23**, in the two commits the seed-then-manifest ordering
      requires. The three `backup/*` flips land in the first with
      `aerie-restic.yaml`; the `longhorn/*` pair is added `required: true` with
      a `kubernetesDeferred` note, `provision-2-seed-secrets.yml` maps both onto
      `vars.LONGHORN_AWS_ACCESS_KEY_ID` /
      `secrets.LONGHORN_AWS_SECRET_ACCESS_KEY`, and
      [`scripts/secrets/README.md`](../../../scripts/secrets/README.md#one-time-setup)
      lists that pair as one-time setup — the first credential here `cd.yml`
      never held, so nothing supplies it by accident. Provision 2
      (`stage=parameters-only`) then seeded both paths, and the second commit
      flips them to `longhorn-system` / `longhorn-backup-target` with
      Longhorn's own `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` key names.
      `-Check` exits 0 over 15 files and `kubectl kustomize` builds the
      directory.

      Confirmed in the cluster by the Phase 3 gate rather than by eye: after
      `infra-config` reconciled, `verify-cluster-platform.yml` reports **14
      ExternalSecrets, none failing to sync** — twelve before this step plus
      `aerie/restic` and `longhorn-system/longhorn-backup-target`, which is the
      count the map now renders. The gate names only the Phase 3 parameters
      individually, so the aggregate is what covers these two; it is the same
      assertion either way, since an unsynced one would appear there.

      One lesson worth the line, because 4b.3's ordering does not mention it and
      it costs a whole cycle: **the seed reads `parameters.json` from the
      remote, not from the working tree.** The first Provision 2 dispatch
      succeeded and seeded nothing new — `actions/checkout` had a commit that
      predated the two entries, so from that run's point of view there was no
      new parameter, and success looked identical to a run that had done the
      work. Push before dispatching, and confirm with `aws ssm
      get-parameters-by-path --path /aerie --recursive --query
      'Parameters[].Name'` rather than with the run's conclusion — a green run
      is not evidence the tree changed.

- [x] **2. Three new `cluster-config.json` keys, and a Provision 4 re-dispatch** —
      [`cluster-config.json`](../../../scripts/k3s/cluster-config.json),
      [`provision-4-cluster-config.yml`](../../../.github/workflows/provision-4-cluster-config.yml).

      | Key | Required | Pattern notes | `consumedBy` |
      |---|---|---|---|
      | `RESTIC_S3_REPOSITORY` | yes | A restic S3 repository string — `s3:s3.<region>.amazonaws.com/<bucket>`, leading `s3:`, no trailing slash. The regional endpoint is what removes the need for a region key (finding 5) | 8b.5 CronJob env |
      | `RESTIC_LOCAL_SUBPATH` | yes | A relative path under the share, no leading slash, no `..` — the same shape `MEDIA_LIBRARY_SUBPATH` already uses, and 8a.2's directory, which is `restic-repo` | 8b.4 PV `source` |
      | `LONGHORN_BACKUP_BUCKET` | yes | An S3 bucket name, same pattern as `WAL_BUCKET`, and **not** either of the other two buckets | 8b.9 `backupTarget` |

      `LONGHORN_BACKUP_BUCKET` is a bucket name rather than the full
      `s3://bucket@region/` target Longhorn wants, because `AWS_REGION` is
      already a key and a second copy of the region is a second thing that can
      be wrong. 8b.9 assembles the two.

      Note the asymmetry with the line above it: restic gets a full repository
      string, Longhorn gets a bucket name. That is not inconsistency for its own
      sake — restic's region lives in an endpoint hostname it will not accept
      separately, and Longhorn's lives in a field of its own. Each key is the
      smallest thing its consumer cannot derive.

      *Exit:* Provision 4 prints all three in its run log (they are variables,
      and that is the point) and `kubectl -n flux-system get cm
      aerie-cluster-config -o yaml` holds them.

      **Landed 2026-08-23 — the committed half.** All three keys are in
      `cluster-config.json` and mapped in `provision-4-cluster-config.yml`, and
      the file's own rule is what kept the change to two files: the script reads
      the map, so a key needs a `vars.` line and nothing else. Three notes on
      the patterns, since that field is the whole point of this map:
      `RESTIC_S3_REPOSITORY` requires the *regional* endpoint
      (`s3:s3.<region>.amazonaws.com/<bucket>`), which is what makes finding 5's
      missing region key safe — a bare `s3.amazonaws.com` is rejected here rather
      than resolving to us-east-1 at first use — and it accepts an optional
      prefix under the bucket while rejecting a trailing slash, an `s3://` URL
      and a bare bucket name. `RESTIC_LOCAL_SUBPATH` takes
      `MEDIA_LIBRARY_SUBPATH`'s pattern exactly, and `required: true` where that
      one is optional: an unset media subpath serves nothing, an unset backup
      subpath is 8b.4's PV mounted on the share root.
      `LONGHORN_BACKUP_BUCKET` takes `WAL_BUCKET`'s pattern exactly, which also
      rejects the `s3://bucket@region/` target string that is the likely paste.
      All three were exercised against .NET's engine with `-cmatch` — 28 cases,
      valid and invalid, including the case-sensitivity the map's comment warns
      `-match` does not give.

      **What remains:** set the three repository variables (Settings → Secrets
      and variables → Actions → Variables) — the restic repository string and
      `restic-repo` from 8a.2, and the Longhorn bucket 8a.1's Provision 6 run
      created — then dispatch Provision 4 against a server. `preflight_only`
      first is worth the extra run here: it validates and diffs without
      applying, and a rejected value at that point costs nothing. The exit
      condition is that dispatch, not this commit.

- [x] **3. Rework `containers/backup/` from a compose sidecar into a cluster job** —
      [`containers/backup/`](../../../containers/backup/).

      The image survives Phase 7 for this step
      ([7b.9](phase-7-cutover.md#what-this-phase-deletes-and-what-survives)
      lists it as surviving), and it survives as the same image
      `restore-job.yaml` already pulls — one image, both directions, which is
      what keeps `pg_restore` and `pg_dump` version-matched to each other and to
      the CNPG cluster. What changes:

      - **`crontab` and the supercronic `ENTRYPOINT` go.** The schedule is a
        `CronJob` now; a container that schedules itself inside a cluster that
        schedules containers is two schedulers disagreeing about what "daily"
        means. `ENTRYPOINT` becomes a shell, and each Job names its script.
      - **`scripts/backup.sh` → `scripts/cluster-backup.sh`.** Drops the
        `pg_dumpall` (finding 6) and the Kuma SQLite snapshot (finding 1, now
        Longhorn's). Keeps the two `pg_dump -Fc` invocations, pointed at
        `$PGHOST` from `aerie-pg-app` rather than at `db`. Adds the parameter
        export (8b.7). Gains `--keep-tag cutover-final` on the `forget` (8b.6).
      - **`scripts/verify-restore.sh` → `scripts/cluster-verify.sh`.** Nearly
        unchanged — it already stands up a throwaway Postgres with
        `initdb`/`pg_ctl` from the image's own install — but it restores
        `aerie.dump` with `pg_restore` rather than replaying
        `postgres-dumpall.sql` with `psql`, since that file no longer exists.
      - **`scripts/init-repos.sh` survives unedited.** Its refusal to init
        against a blank password, and its `cat config` probe, are exactly as
        correct in a CronJob as in a deploy step. Its two repository env vars
        keep their names.
      - **`aws-cli` joins `restic`, `sqlite3` and `curl` in the `apk add`.**
        For 8b.7. `sqlite3` stays: 8b.15's rehearsal restores a Longhorn volume
        backup and then has to prove the database inside it opens, and this is
        the image with a shell in it.

      *Exit:* `publish.yml`'s existing `build-and-push-backup-image` job pushes
      a new tag on the merge; `docker run --rm --entrypoint sh <image> -c 'restic
      version && aws --version && sqlite3 -version'` answers three times.

      **Landed 2026-08-23.** All four bullets above, as written, plus two notes.

      The base image's comment now version-matches itself to
      [cluster.yaml](../../../deploy/cluster/data/cluster/cluster.yaml)'s
      `postgresql:18.4` rather than to the `db` service Phase 7 deleted — the
      same tag, a live reason for it. `ENTRYPOINT ["sh"]`, so a `command:` in
      either Job replaces it outright and a bare `docker run` lands in a shell
      instead of idling; [restore-job.yaml](../../../deploy/cluster/data/schema/restore-job.yaml)'s
      comment, which described the supercronic it was overriding, was updated
      in the same commit rather than left to describe an image that no longer
      exists.

      **One deviation, and it is the reason `restic dump` will work later.**
      `cluster-backup.sh` stages into a fixed `/tmp/aerie-backup` rather than
      `mktemp -d`. restic records the *absolute* path of every file it backs
      up, so a random directory per run puts the same dump at a different path
      in every snapshot — which is why 8b.7's exit condition below had to write
      `restic dump latest /…/parameters.json` with an ellipsis in it. With a
      fixed staging path the name is `/tmp/aerie-backup/parameters.json` in
      every snapshot and that command is typeable. `cluster-verify.sh` still
      finds its dump by `find … -name`, so it reads older snapshots too.

      Exercised end-to-end locally rather than asserted: a throwaway
      `postgres:18.4-alpine` with `aerie` (a `public` table and a `storage`
      one) and `quartz`, two local restic repos, `cluster-backup.sh` and then
      `cluster-verify.sh` against them. Both repos took a snapshot carrying
      `aerie.dump`, `quartz.dump` and `parameters.json`; `forget` reported
      `keep … and all snapshots with tags [[cutover-final]]`; the verify
      restored into a scratch instance and counted 2 tables across
      `public`+`storage`. `init-repos.sh` reported "already initialized" on the
      second run, unedited, as predicted. The exit command's three versions:
      restic 0.18.1, aws-cli 2.34.63, sqlite3 3.53.4.

      **What remains — and it is a sequencing hazard, not a leftover.**
      `cluster-backup.sh` calls `/app/scripts/export-parameters.sh`, which is
      8b.7's file and does not exist yet. The image builds and the exit
      condition above passes without it, but a run of the backup job fails at
      that line (`not found`, after the dumps and before any `restic backup`,
      so it produces no snapshot rather than a partial one). **8b.7 has to land
      before 8b.5's first manual run** — which 8b.5's own exit condition
      already implies, since it asks for `parameters.json` in the snapshot.
      Either land 8b.7 first or expect one loud failed Job.

- [x] **4. The local repo, as a PV** —
      `deploy/cluster/data/backup/local-repo-volume.yaml`.

      A statically-provisioned `smb.csi.k8s.io` PV/PVC pair in `aerie`, in the
      shape [share-volumes.yaml](../../../charts/aerie/templates/share-volumes.yaml)
      established — `storageClassName: ""`, an explicit `claimRef`, a
      `volumeHandle` **distinct from `aerie-share-rw` and `aerie-share-ro`**
      (that file's own comment says why: two PVs sharing a handle are one
      volume to the CSI layer, and the second mount silently inherits the
      first's options, which here would mean a backup repo mounted read-only).
      `source: //${SHARE_HOST}/${SHARE_NAME}/${RESTIC_LOCAL_SUBPATH}`,
      `nodeStageSecretRef` naming the `smb-share` Secret ESO already syncs into
      `aerie`.

      `ReadWriteMany`, because that is what SMB is, and `Retain`, because this
      volume is a backup repository and `prune: true` reconciles this tree.

      One property to check on the first run rather than assume: **restic's
      locking on CIFS.** restic coordinates through lock files, and CIFS's
      handling of them is the least-exercised corner of this design. If a
      `forget --prune` ever reports a stale lock that `restic unlock` has to
      clear, that is the finding, and it belongs in this file's comment rather
      than in someone's memory.

      *Exit:* a throwaway pod mounting the PVC lists the repo's `config`,
      `data/` and `snapshots/` — the same repo 8a.2 moved, not an empty
      directory the mount silently created.

      **Landed 2026-08-23 — the committed half**, as
      [`local-repo-volume.yaml`](../../../deploy/cluster/data/backup/local-repo-volume.yaml)
      plus the directory's `kustomization.yaml`, in the shape above:
      `aerie-restic-local` for the PV, the PVC and — the part that matters —
      the `volumeHandle`, `storageClassName: ""` with a reciprocal
      `claimRef`/`volumeName`, `ReadWriteMany`, `Retain`, and
      `nodeStageSecretRef` naming the `smb-share` Secret ESO already syncs
      into `aerie`.

      **One deviation from share-volumes.yaml, and it decides whether the
      backup job can write at all: `uid=70,gid=70`, not `uid=0,gid=0`.**
      [containers/backup/Dockerfile](../../../containers/backup/Dockerfile)
      ends with `USER postgres` — it has to, since `initdb` refuses to run as
      root and 8b.8's verify needs it — and in `postgres:18.4-alpine` that is
      uid/gid 70. The share is a guest CIFS mount, so ownership is *presented*
      by these mount options and the permission check is client-side against
      them: copied verbatim from the media PVs, every path in the repository
      would present as root-owned and restic's first write would fail a
      permission check that says nothing about mount options. The mode bits
      tighten to `0770`/`0660` for the same reason they can — only the backup
      and verify jobs mount this, both as that uid, so the `other` bits serve
      nobody. This is the one number in the file coupled to an image tag; a
      base-image change that moves the postgres uid presents here as a backup
      job that cannot write.

      **Wiring, since "not a new Kustomization" left it open.** `../backup`
      joins [`data/schema/kustomization.yaml`](../../../deploy/cluster/data/schema/kustomization.yaml)'s
      `resources` as a *base*, not a file: kustomize's default load restrictor
      forbids a file reference outside the build root but takes a directory
      carrying its own `kustomization.yaml`. That puts the tenant behind
      data-schema's `dependsOn: data-cluster` — which 8b.5's CronJob needs,
      since a `PGHOST` from a Secret CNPG has not created yet is exactly the
      failure the cluster/schema split exists to prevent — and inherits its
      `postBuild.substituteFrom`, where `${SHARE_HOST}`, `${SHARE_NAME}` and
      `${RESTIC_LOCAL_SUBPATH}` resolve. The directory keeps its own
      `kustomization.yaml` regardless, because `ci.yml`'s `deploy-manifests`
      job discovers build roots by finding them. Confirmed locally: 16
      kustomizations build (15 before this), the namespace transformer leaves
      the cluster-scoped PV alone rather than stamping `aerie` onto it, and
      the three tokens are declared keys in `cluster-config.json`.

      **What remains:** the exit condition, which needs a reconciled cluster
      and 8b.2's Provision 4 dispatch before it. Then run the throwaway pod.
      The CIFS locking question above stays open on purpose: it is answered by
      8b.5's first `forget`, not by this mount.

      **Correction, 2026-08-23, from the cluster rather than from reading.**
      This note originally said `${RESTIC_LOCAL_SUBPATH}` "resolves to the
      empty string until that variable is set", making an unresolved subpath a
      PV mounted on the share *root* — "the one failure mode here that looks
      like success". **That is not what Flux does.** kustomize-controller
      v1.9.4 applies post-build substitution in strict mode by default, and
      when this file reached the cluster one Provision 4 dispatch ahead of its
      key, the result was:

      ```text
      post build failed for 'PersistentVolume/aerie-restic-local':
      envsubst error: variable substitution failed:
      variable not set (strict mode): "RESTIC_LOCAL_SUBPATH"
      ```

      No PV was created, so the silent failure this whole note was written to
      guard against cannot occur on this version. What happens instead is not
      free, though, and is worth more attention than the thing it replaced:
      **`data-schema` stops reconciling entirely**, retrying every 60s, and
      `apps` — which `dependsOn` it — stalls behind it at whatever revision it
      last applied. Running workloads keep running; nothing new deploys. A typo
      in a token is therefore a stalled tenant rather than a quiet wrong value,
      which makes [ci.yml](../../../.github/workflows/ci.yml)'s token check
      worth more than its own comment claimed (both comments are corrected).

- [x] **5. The backup CronJob** —
      `deploy/cluster/data/backup/backup-cronjob.yaml`.

      Namespace `aerie`. `image: ${IMAGE_REGISTRY}/aerie-backup:latest` and
      `imagePullSecrets: [ghcr-pull]`, both for the reasons
      [restore-job.yaml](../../../deploy/cluster/data/schema/restore-job.yaml)
      spells out at length — the registry is a substitution because a host baked
      into this tree is one installation's fact, and the pull secret's absence
      presents as a bare `401` that reads like a missing image.

      Env: `PGHOST`/`PGPORT`/`PGUSER`/`PGPASSWORD` from `aerie-pg-app`,
      `RESTIC_PASSWORD`/`AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY` from the
      `restic` Secret 8b.1 creates, `RESTIC_REPOSITORY_S3` from
      `${RESTIC_S3_REPOSITORY}`, `RESTIC_REPOSITORY_LOCAL` a fixed
      `/mnt/restic-local` mount path over 8b.4's PVC.

      `schedule: "10 3 * * *"` — the same 03:10 Phase 0 used, an hour and ten
      minutes after CNPG's 02:00 `ScheduledBackup`, which is finding 7's
      stagger and not a coincidence worth losing. `concurrencyPolicy: Forbid`,
      `successfulJobsHistoryLimit: 1`, `failedJobsHistoryLimit: 3`,
      `ttlSecondsAfterFinished`, `backoffLimit: 2` — the shape
      [opensearch-provision.yaml](../../../deploy/cluster/observability/config/provisioning/opensearch-provision.yaml)
      already uses, and `Forbid` matters more here than there: two concurrent
      restic writers against one repo is finding 7.

      *Exit:* one manual run (`kubectl -n aerie create job --from=cronjob/…`)
      produces a snapshot in **both** repos containing `aerie.dump`,
      `quartz.dump` and `parameters.json`, and `restic snapshots` on each shows
      it.

      **Landed 2026-08-23 — the committed half**, as
      [`backup-cronjob.yaml`](../../../deploy/cluster/data/backup/backup-cronjob.yaml)
      plus its line in the directory's `kustomization.yaml`: `aerie-backup` in
      `aerie`, `${IMAGE_REGISTRY}/aerie-backup:latest` behind `ghcr-pull`,
      `command: ["sh", "/app/scripts/cluster-backup.sh"]` against the bare
      shell 8b.3 left as the `ENTRYPOINT`, the four `aerie-pg-app` refs and the
      three `restic` refs, `03:10`, `Forbid`, `1`/`3`, `backoffLimit: 2`, and
      `/mnt/restic-local` over 8b.4's PVC. Three decisions the bullet above
      left open, and one bug that had to be fixed before any of it could be
      committed at all.

      **No `timeZone`, and that is what preserves the stagger.** A CronJob
      fires in UTC unless `.spec.timeZone` says otherwise, and the obvious
      tidy-up here is `timeZone: ${TZ}` — the key already exists. It would be
      wrong: [scheduledbackup.yaml](../../../deploy/cluster/data/schema/scheduledbackup.yaml)
      is a CNPG object, CNPG's `ScheduledBackup` has no timezone field at all,
      and it fires in the operator's time. Pinning only this side to a local
      zone slides the 70-minute gap by the UTC offset, and at
      `America/New_York` that puts the restic job at 07:10 UTC — five hours
      *after* the base backup rather than seventy minutes, on a different
      calendar day, and the stagger finding 7 asks for is gone without anything
      reporting it. Both sides read UTC; the gap is the one that was designed.
      `${TZ}` is the app tier's key and is deliberately not used here.

      **One env var the bullet's list does not have: `AWS_DEFAULT_REGION`, from
      `${AWS_REGION}`.** Finding 5 is right that restic needs no region key —
      it reads one out of the endpoint hostname in `RESTIC_S3_REPOSITORY`. The
      `aws-cli` 8b.7 calls has no endpoint to read one out of, and with the
      variable unset `aws ssm get-parameters-by-path` exits non-zero on "You
      must specify a region" before it fetches anything. It lands here rather
      than with 8b.7 because this file *is* 8b.5 + 8b.6 + 8b.7 by the layout
      above, and because a script cannot set its own pod's environment.

      **`ttlSecondsAfterFinished: 259200`, not the 3600 the shape came with.**
      The two retention fields fight, which the opensearch CronJob never
      notices because it runs hourly: the TTL controller deletes a finished Job
      on its own timer regardless of what `failedJobsHistoryLimit` wants to
      keep, so an hour-long TTL on a once-a-day job means the pod logs of a
      03:10 failure are gone before anyone reads the alert about it. Three days
      of a daily job is exactly the three failures the history limit asks for.
      The memory limit is loose for a related reason — 1Gi against a 256Mi
      request, because an OOMKill lands mid-`forget --prune`, which leaves a
      stale lock for the next run and throws away the repack it had already
      done.

      **The bug, and it is 8b.4's rather than this step's: the whole directory
      was gitignored.** `.gitignore` carries Visual Studio's `Backup*/`, which
      on a case-insensitive checkout (`core.ignorecase=true`, i.e. every macOS
      clone) matches any directory named `backup`. That trap had already been
      found once and re-included — `!containers/backup/` is right there with a
      comment saying why — and `deploy/cluster/data/backup/` walked into it
      unannounced. The consequence is not cosmetic: 8b.4's commit landed
      [`schema/kustomization.yaml`](../../../deploy/cluster/data/schema/kustomization.yaml)'s
      `../backup` line and the doc note, and **neither of the two files that
      line points at**, so HEAD referenced a base git had never been shown.
      `kubectl kustomize deploy/cluster/data/schema` builds fine in the working
      tree and fails on a fresh clone — which is exactly the CI job that would
      have caught it, and exactly the shape of failure that job exists for.
      Fixed with a `!deploy/cluster/data/backup/` line and the same style of
      comment beside it; verified by checking every directory in the repository
      against `git check-ignore` and finding nothing else but build output.

      Verified locally: 16 kustomizations build, the token check passes with 23
      tokens all declared (22 before, the new one being `AWS_REGION` reaching
      `deploy/` for the first time), no address literals, and the namespace
      transformer stamps `aerie` on the CronJob while still leaving 8b.4's PV
      alone.

      **Exit condition met 2026-08-24**, against the real cluster and the real
      repositories. One manual run
      (`kubectl -n aerie create job --from=cronjob/aerie-backup aerie-backup-manual-1`)
      completed in **38 seconds** and produced a snapshot in **both** repos —
      `56dee9d1` local, `0618f9ce` in S3 — each carrying `aerie.dump`,
      `quartz.dump` and `parameters.json` at `/tmp/aerie-backup/`, 74.628 MiB
      read, 13.9 MiB and 16.9 MiB stored. `init-repos.sh` reported both repos
      already initialized, the export reported 21 parameters, both `forget`s
      ran, and the 8b.6 guard passed silently. Seven snapshots in each repo now,
      six of them the pre-cluster history.

      **And the CIFS locking question 8b.4 left open has its first answer: no
      lock trouble.** A `grep -icE "lock|warn|error|fatal"` over the whole job
      log returns **0** — the `backup` and the `forget --prune` both took and
      released their locks on the SMB mount without a stale-lock message. One
      run is not a season, so the note in
      [local-repo-volume.yaml](../../../deploy/cluster/data/backup/local-repo-volume.yaml)
      stays where it is, but the least-proven thing in this design is now the
      least-proven thing that has worked once.

      One expected line worth not mis-reading: `no parent snapshot found, will
      read all files`, on both repos. That is correct for the *first* run under
      `--host aerie` (finding 9) — there was no prior snapshot from that host to
      parent against. The nightly 03:10 run is what proves the other half: it
      should find a parent, and its `forget` should put both `aerie`-host
      snapshots in one group, which is the first time retention will have
      grouped anything on real data.

      **What remains: nothing here.** The sequencing hazard 8b.3 and this
      step both flagged — `cluster-backup.sh` calling an
      `export-parameters.sh` that did not exist — is closed: 8b.7 landed in the
      same push, so the first 03:10 tick has a complete script to run. One env
      var was added here for it (`PARAMETER_PREFIX`), and one line of this
      file's own env list belongs to it (`AWS_DEFAULT_REGION`).
      Before the manual run, confirm 8b.4's exit first — not for the unset
      case, which 8b.4's correction above shows announces itself as a failed
      reconciliation, but for a subpath that resolved to the *wrong* directory,
      which still looks like success. Expect the CIFS locking question that
      mount left open to be answered by this job's first `forget`, not before
      it.

- [x] **6. `--keep-tag cutover-final`, in the same commit as the `forget`** —
      the retention half of 8b.5's script.

      Inherited from [7b.3](phase-7-cutover.md#phase-7b--the-cutover), and the
      reason it is its own step rather than a line item is the ordering claim in
      the original bullet: it belongs in the manifest **the first time the
      `forget` is written**, not after the retention has had a year to reach it.

      `restic forget --keep-daily 7 --keep-weekly 4 --keep-monthly 12
      --keep-tag cutover-final --prune`, against both repos, run by **this job
      and no other** (finding 7). The snapshot being protected is the last
      complete copy of the pre-cluster world: Kuma's SQLite and the pre-cutover
      Postgres dumps exist nowhere else, and after 7c.3 reformatted the disk
      holding the old local repo the copies of it are 8a.2's move and the S3
      repo — both of which this `forget` now prunes.

      *Exit:* after a run, `restic snapshots --tag cutover-final` still lists
      exactly one snapshot in each repo, and its date is still the pre-cutover
      one. Assert this rather than read it: it is the check that a policy
      change three phases from now will break silently.

      **The manifest half landed with 8b.3**, which is the whole point of this
      step: `--keep-tag cutover-final` is on the `forget` in the commit that
      first wrote the `forget`, with the reasoning in the script beside it.
      What is left is the assertion, which needs a run against the real repos.

      **The assertion landed 2026-08-23, inside the loop rather than in the
      gate** — [`cluster-backup.sh`](../../../containers/backup/scripts/cluster-backup.sh).
      8b.16 still owns the standing check ("`cutover-final` is still present in
      both repos"), and this is not a duplicate of it: the gate answers *later*,
      and this answers *between the two repositories*. The loop prunes the
      local repo first, the projected snapshot list is captured either side of
      each `forget`, and a mismatch aborts under `set -e` — so a retention
      change that eats the tagged snapshot in the local repo stops the run
      before the S3 `forget` reaches the last remaining copy. A check that ran
      afterwards could only report the loss of both.

      Two details that decide whether it is correct rather than merely present.
      It compares `[{short_id, time}]` — a projection, so an image bump that
      adds a field to restic's snapshot JSON (`summary` is the recent one) does
      not read as a lost snapshot, and `time` is what makes "still the
      pre-cutover one" part of the assertion rather than just "still one".
      And **the empty case must pass**: `.[]?` normalises a repository with no
      such snapshot, because a fresh installation of this repository has no
      cutover-final at all and its backups must not fail every night on the
      absence of one. The property being defended is not "the tag exists", it
      is "retention never removes it", which is exactly what before-equals-after
      says and the only form of it that survives being shipped to someone else.

      **That exercise is also what turned up finding 9**, which matters more
      than the assertion does: the retention this step protects a snapshot
      *from* was not running at all. A CronJob pod's name is restic's snapshot
      hostname, `forget` groups by `host,paths`, and so every night's snapshot
      was its own group and nothing was ever pruned. `--host aerie` on the
      `backup` and an explicit `--group-by host,paths` on the `forget` are the
      fix; ten runs go from ten retained snapshots to three. A `--keep-tag` on
      a policy that never fires protects nothing, so 8b.6 was not finished
      until this was.

      **Exit condition met 2026-08-24, on the real repositories.** After the
      first manual run's `forget --prune` against both, `restic snapshots
      --tag cutover-final` still reports exactly one snapshot in each and still
      the pre-cutover date — `3bb9e508` local and `43ab5d45` in S3, both
      `2026-08-20T03:10`. Asserted rather than read, twice over: by the
      in-script guard that ran inside the job and said nothing, and by this
      check afterwards. A `--dry-run` of the same policy beforehand had already
      predicted it — 0 removals, 6 keep-groups, 6 snapshots, both repos — which
      is how the first real `--prune` against 957 MiB of irreplaceable history
      was made a known quantity rather than a hope.

      Exercised rather than asserted, against two throwaway local repos each
      seeded with a `--time 2026-08-09` snapshot tagged `cutover-final`. The
      real script keeps it. A deliberately regressed copy — `--group-by ''`,
      `--keep-last 2`, `--keep-tag` deleted, which is the plausible
      "simplification" rather than a strawman — destroyed it in the local repo,
      and the guard fired, exited 1, and **left the S3 copy intact**: after the
      aborted run `restic snapshots --tag cutover-final` was `[]` locally and
      still the 2026-08-09 snapshot in S3.

- [x] **7. Export the `/aerie/*` tree into the repos, on the same schedule** —
      `containers/backup/scripts/export-parameters.sh`, called by 8b.5's script.

      ```sh
      aws ssm get-parameters-by-path --path /aerie --recursive --with-decryption
      ```

      written to the scratch directory as JSON and picked up by the same
      `restic backup` invocation as the dumps, so it is one snapshot rather than
      two things that can be a different age.

      This is the loop [docs/secrets-architecture.md](../../secrets-architecture.md)
      says Phase 8 closes. The secret store is off-site and that is the point of
      it, but "the AWS account is gone" is the failure mode ESO *introduces* —
      before it, every value lived in a GitHub Actions secret, and the whole
      point of moving them was that a cluster could read them without a human.
      `RESTIC_PASSWORD` is printed offline (8a.4 just proved it), so the export
      is readable from outside the account it describes, which is the property
      that makes it a recovery plan rather than a second copy of the same
      dependency.

      Two things this file must not do: it must not write to the parameter
      store (it is `get-parameters-by-path` only, and the credential should not
      be able to write either), and it must not be logged. `set -x` anywhere in
      this script puts every secret in the house into a Kubernetes pod log that
      Fluent Bit ships to OpenSearch — which is a searchable, unauthenticated
      index ([6b.9](phase-6-observability.md)'s written-down exposure). Say so
      in the script.

      *Exit:* the newest snapshot contains a `parameters.json` whose key count
      equals `parameters.json`'s own entry count plus the two bootstrap keys,
      and a `restic dump latest /…/parameters.json | jq` on a machine holding
      only the offline password reads a value back.

      **Landed 2026-08-23** as
      [`export-parameters.sh`](../../../containers/backup/scripts/export-parameters.sh),
      the `aws ssm get-parameters-by-path` above with both prohibitions written
      into the header where the next person to reach for `set -x` will read
      them. Four decisions the bullet left open, and one correction to its exit
      condition.

      **The path is `$PARAMETER_PREFIX`, not `/aerie`.** The prefix is already a
      variable everywhere else in this chain — `parameters.json` carries a
      `prefix` field, `Sync-AerieSecrets.ps1` takes a `-ParameterPrefix` whose
      help text names the case ("two installations sharing one AWS account give
      each its own prefix"), and 8a.1's committed
      [`aerie-restic-ssm.policy.json`](../../../scripts/secrets/iam/aerie-restic-ssm.policy.json)
      is written against a `<PARAMETER_PREFIX>` placeholder. A hardcoded
      `/aerie` here would be the one link that could not follow. It defaults to
      `/aerie` so a stock installation needs no environment, and 8b.5's CronJob
      sets it explicitly anyway so the change has one obvious home. A bare `/`
      is rejected: it is a legal path that exports every parameter in the
      account into a repository scoped to Aerie.

      **`--query 'sort_by(Parameters, &Name)'`, which is doing two jobs.** It
      drops the `{"Parameters": […]}` envelope so the file *is* the array and
      its `length` is the parameter count — the number the exit condition wants
      — and it makes the bytes deterministic. SSM returns pages in no promised
      order, so an unsorted export differs from yesterday's even when nothing
      changed and restic stores a new blob for it every night forever. Sorted,
      an unchanged tree deduplicates to nothing. Pagination is deliberately left
      to the CLI: `get-parameters-by-path` caps `MaxResults` at 10, so a
      `--no-paginate` would quietly export the first ten of twenty-two.

      **It refuses to snapshot an empty export.** A wrong prefix and a policy
      that lost its Resource ARNs both land on "succeeded, captured nothing",
      which is a green job and a `[]` file nobody opens until the day they need
      it. `jq length` rather than counting matches in the text, because the text
      is every secret in the house and a value containing the pattern would
      inflate the count — which is one of the two reasons `jq` joins the image's
      `apk add` (the other is 8b.6's assertion). `umask 077` before the
      redirection, so the file is born 0600 and restic carries that mode into
      the snapshot.

      **The exit condition's arithmetic is wrong, and the right number is 22.**
      It says "`parameters.json`'s own entry count plus the two bootstrap
      keys". The two bootstrap keys are never SSM parameters: `Sync-AerieSecrets.ps1`
      resolves them from the environment and applies them straight to the
      cluster as the `aerie-eso-bootstrap` Secret over SSH — that is the whole
      point of them, since a credential ESO reads from the store it needs the
      credential to reach would be circular. The single `put-parameter` in the
      repository is driven by `.parameters` alone. So the tree holds **at most
      22**, and fewer if either optional entry (`kiosk/wifi-password`,
      `tailscale/auth-key`) is unset — which makes the shape 8b.16 should assert
      "every *required* parameter appears in the export" (20 of them) rather
      than an equality against a total, and that is 4b.11's read-the-expected-
      set-from-`parameters.json` rule anyway.

      Exercised in the built image against a stub `aws` returning a
      representative 22-entry tree: 22 exported, file mode 0600, the array
      sorted. The four refusals all fire — empty result, `PARAMETER_PREFIX=/`,
      missing output argument — and a custom prefix is passed through to the
      CLI unaltered. Then end-to-end through `cluster-backup.sh` against a
      throwaway `postgres:18.4-alpine`: both dumps, the export, both repos, one
      snapshot each carrying `aerie.dump`, `quartz.dump` and `parameters.json`,
      and `cluster-verify.sh` afterwards restoring that snapshot into a scratch
      instance and counting 2 tables. `jq` reports 1.8.1 beside 8b.3's other
      three versions.

      **Exit condition met 2026-08-24, and the count settles the arithmetic
      above at 21.** `restic dump latest /tmp/aerie-backup/parameters.json`
      out of the local repo returns a 21-entry array, and diffing its `.Name`
      values against `parameters.json` gives exactly what the corrected reading
      predicts: **all 20 required parameters present**, plus one of the two
      optional ones (`tailscale/auth-key`), with the absent entry being the
      other optional one (`kiosk/wifi-password`). Not 22, and nowhere near the
      24 the original exit condition asked for. That is the shape 8b.16 should
      assert — the required set read from `parameters.json`, with the optional
      pair allowed to be absent — rather than any fixed total.

      The `restic dump` path is typeable rather than elliptical because of
      8b.3's fixed staging directory, which is now doing its third job:
      addressability here, deterministic bytes for deduplication in the export,
      and retention grouping in finding 9.

- [x] **8. The verify CronJob** —
      `deploy/cluster/data/backup/verify-cronjob.yaml`.

      Weekly, Sunday 04:00 — Phase 0's slot, and far enough from 03:10 that a
      slow backup and a verify never share a lock. Runs `cluster-verify.sh`
      against the **local** repo, which is deliberate on two axes: it exercises
      the copy that is not in AWS, and it exercises the CIFS mount that 8b.4
      flagged as the least-proven thing in this phase.

      *Exit:* a run restores the newest `aerie.dump` into a throwaway Postgres
      and reports a non-zero table count. This is the cluster's replacement for
      the check Phase 0 had and the cluster has not had since 7b.2; until it
      passes once, this phase has produced backups nobody has restored.

      Written, built, and unverified on the cluster — the exit condition is a
      Sunday 04:00 run, so it is a `kubectl -n aerie create job --from` away
      rather than a wait, and it belongs in the same pass as 8b.16's gate.

      Two decisions the step did not specify, both about the lock rather than
      the restore. **`activeDeadlineSeconds: 3600`**, which 8b.5's CronJob
      deliberately does not carry: a slow dump that finishes is still a backup,
      but a verify wedged on the CIFS mount leaves its restic lock in the
      repository, and the next `forget --prune` — Monday 03:10, twenty-three
      hours later — fails against a stale lock rather than waiting behind it.
      The deadline kills the pod at 05:00 Sunday, inside the gap and outside
      any honest run. And **no AWS credentials in the pod at all**, not just no
      `RESTIC_REPOSITORY_S3`: the local-repo choice is only load-bearing if the
      off-site copy cannot be what makes this job pass, and a workload that
      never reads a credential should not be a place one is mounted.

- [x] **9. Longhorn's backup target, and the freeze setting finding 1 depends on** —
      [`longhorn.yaml`](../../../deploy/cluster/infrastructure/controllers/longhorn.yaml).

      Three `defaultSettings` keys:

      ```yaml
      backupTarget: s3://${LONGHORN_BACKUP_BUCKET}@${AWS_REGION}/
      backupTargetCredentialSecret: longhorn-backup-target
      freezeFilesystemForSnapshot: true
      ```

      The `s3://bucket@region/` form is Longhorn's own and is not a URL anyone
      else will parse; the `@region` is not optional and a target missing it
      fails at first use rather than at apply.

      **Read every one of these back.** That file's own note 4 is the reason and
      it is not a general caution: Longhorn *swallows* a bad setting — a value
      that does not parse is logged at warn level by the manager and skipped, so
      `freezeFilesystemForSnapshot: "yes"` is not a failed install, it is
      filesystem freezing quietly off and finding 1's argument quietly false.
      There is no `values.schema.json` on this chart either, so a misspelled key
      installs cleanly and is never read.

      A rollout note this file has earned the hard way: 5b.11's history says a
      change to `longhorn-manager`'s pod template stalled the `HelmRelease`
      three times running against timeouts raised twice. These three keys write
      the `longhorn-default-setting` ConfigMap, which the manager reads without
      a restart — so this should *not* roll that DaemonSet. Confirm that from
      the reconcile rather than assume it, because the widened timeouts on that
      file and on `infra-controllers` are the margin if it does.

      *Exit:* `kubectl -n longhorn-system get settings.longhorn.io backup-target
      -o jsonpath='{.value}'` reads back the assembled target, the credential
      setting names the Secret, and `freeze-filesystem-for-snapshot` reads
      `true`. Expect the data-engine-specific settings to answer
      `{"v1":…,"v2":…}` rather than a bare scalar, per that file's note 4 —
      these three are not in that family, but the habit of comparing against
      what Longhorn *returns* rather than what was set is the one that catches
      it.

      **Landed 2026-08-23, and the step as written would not have worked.**
      Three of its claims are false against chart 1.11.3 and longhorn-manager
      v1.11.3, all checked against the extracted chart and the tagged source
      rather than argued from the docs. The shape that shipped:

      ```yaml
      defaultSettings:
        freezeFilesystemForSnapshot: true
      defaultBackupStore:
        backupTarget: s3://${LONGHORN_BACKUP_BUCKET}@${AWS_REGION}/
        backupTargetCredentialSecret: longhorn-backup-target
      ```

      **Only one of the three is a `defaultSettings` key.** Longhorn 1.9 turned
      the backup target from a Setting into a `BackupTarget` CR so a cluster can
      have several, and the chart moved `backupTarget` and
      `backupTargetCredentialSecret` out of `defaultSettings` into a top-level
      `defaultBackupStore` map that writes a *different* ConfigMap,
      `longhorn-default-resource` (`templates/default-resource.yaml`;
      `templates/default-setting.yaml` renders neither key). Written where the
      step said to write them they would not have been swallowed the way note 4
      describes — that path at least logs at warn — they would have been keys no
      template reads, in a values file with no schema: a clean install, a silent
      manager, and an empty backup target. This is the same trap one layer
      further out than the one the step quoted, and it is the reason the chart
      was pulled and rendered before the commit.

      **`settings.longhorn.io backup-target` does not exist on 1.11**, so the
      *Exit* command above reads a NotFound rather than a value. `backup-target`
      is absent from `types/setting.go`'s `settingDefinitions`; the name survives
      only in `types/backupstore.go` as a key name for that ConfigMap. The
      read-back is the CR: `kubectl -n longhorn-system get
      backuptargets.longhorn.io default -o jsonpath='{.spec.backupTargetURL}
      {.spec.credentialSecret} {.status.available}'`.

      **The freeze setting *is* in note 4's data-engine-specific family**, in
      spite of the step saying these three are not. Its definition carries
      `DataEngineSpecific: true` with default `{"v1":"false"}`, so
      `types.parseSettingSingleBool` expands the scalar and
      `freeze-filesystem-for-snapshot` reads back `{"v1":"true"}`. The step's
      own closing habit — compare against what Longhorn returns — is what this
      would have cost someone otherwise.

      And one thing the step assumed that is true for the wrong ConfigMap.
      `longhorn-default-setting` is watched, so the freeze setting lands without
      a restart and the rollout note holds: nothing rolls the DaemonSet.
      `longhorn-default-resource` is **not** watched — it is read once, in
      `app/daemon.go`'s startup path, by `CreateOrUpdateDefaultBackupTarget`,
      and the chart puts no ConfigMap checksum on the manager pod template. So
      on this cluster the Helm upgrade updates a ConfigMap nothing then reads,
      and **one `longhorn-manager` pod has to restart** before the `BackupTarget`
      CR moves. One pod, not a rollout, and outside Helm, so it cannot stall the
      HelmRelease; a fresh install needs nothing. Note 5 in the file carries
      this, because it is 8b.10's prerequisite rather than a detail of this step.

      Verified locally: both ConfigMaps rendered from the committed values
      through `helm template` against the 1.11.3 artifact, and CI's kustomize
      build and substitution-token checks pass. **What remains** is the cluster
      half — reconcile, restart one manager pod, and read the CR back — which
      belongs in the same pass as 8b.10, since an unavailable target and a
      `RecurringJob` that has never run look identical from the bucket.

- [x] **10. The `RecurringJob`, and the retirement of the hand-made Secret** —
      `deploy/cluster/infrastructure/config/longhorn-recurringjob.yaml`,
      [`restore-job.yaml`](../../../deploy/cluster/data/schema/restore-job.yaml).

      **The `RecurringJob`:** `task: backup`, `groups: [aerie-critical]` —
      matching 8a.3's label and *not* `default`, which is finding 2 — `cron:
      "0 2 * * *"`, `retain: 7`, `concurrency: 1`. Backups after the first are
      incremental at the block layer, so seven dailies of three small volumes is
      a number chosen for restore convenience rather than for cost.

      **The Secret:** 4b.9 created `aerie-pg-restore-restic` by hand, with a
      comment saying "Delete it once Phase 8's own `ExternalSecret` exists and
      this Job has been updated to read that instead." Both halves happen here:
      `restore-job.yaml`'s five `secretKeyRef`s move to the `restic` Secret
      (three of them) and to `${RESTIC_S3_REPOSITORY}` (`RESTIC_REPOSITORY`,
      now a substitution rather than a Secret key, since a repository path is
      not a credential), the region key disappears with finding 5's endpoint,
      and then `kubectl -n aerie delete secret aerie-pg-restore-restic`.

      Editing that Job re-applies an immutable pod template, so its own file
      comment applies: delete the suspended Job and let Flux recreate it.

      *Exit:* `kubectl -n longhorn-system get recurringjob` lists it; after one
      night, `kubectl -n longhorn-system get backups.longhorn.io` shows three
      completed backups and the bucket holds a `backupstore/` prefix. And
      `aerie-pg-restore-restic` is gone while `restore-job.yaml` still renders —
      the second half is the one that fails at 3am if it is wrong, so run the
      restore Job once against the new Secret before ticking this.

      **Both files written 2026-08-23, and the step as written holds — no
      corrections this time.** The `RecurringJob` is
      [`longhorn-recurringjob.yaml`](../../../deploy/cluster/infrastructure/config/longhorn-recurringjob.yaml),
      layer 2 beside the StorageClasses and needing no substitution at all; the
      restore Job's five `secretKeyRef`s became three against `restic` plus
      `RESTIC_REPOSITORY: ${RESTIC_S3_REPOSITORY}`, and `AWS_DEFAULT_REGION` is
      gone rather than moved (`restore.sh` runs no aws-cli, unlike 8b.7's
      export, so there is nothing left that needs a region outside the
      endpoint). It validates against the extracted 1.11.3 CRD schema, all 16
      kustomizations build, and both portability checks pass.

      Six mechanical facts checked against the 1.11.3 chart and longhorn-manager
      v1.11.3 rather than the docs — 8b.9's lesson applied prospectively rather
      than after the fact. The first three are in the manifest's own comments
      because they change what an operator reading the object should expect:

      - **`spec.name` is required**, and the mutating webhook fills it from
        `metadata.name` a moment before `ValidateRecurringJob` rejects an empty
        one. It is spelled out in the file instead, so the required field is in
        git rather than supplied by a webhook to a manifest that never declared
        it. The same mutator defaults `spec.labels` and `spec.parameters` to
        `{}`, so the read-back carries two fields the file does not — the same
        genre of "compare against what Longhorn returns" as 8b.9's
        `{"v1":"true"}`, and not drift.
      - **`volume-backup-policy` is read by nothing on this path.** The
        validator accepts it for a `backup` task, and only the `system-backup`
        task ever consults it — a parameter that would apply cleanly and do
        nothing, which is 8b.9's trap in miniature and is why it is documented
        rather than set.
      - **A detached volume is skipped with a warn log and nothing else.**
        `filterVolumesForJob` drops anything not `Attached` unless
        `allow-recurring-job-while-volume-detached` is on, and it is off
        (Longhorn's default; `longhorn.yaml` does not change it). All three
        volumes are attached in steady state, so this is not a gap today — but
        Grafana scaled to zero for an afternoon means that night's backup of its
        volume does not happen and does not fail. **8b.11 owns this**: the
        staleness alert on the newest Longhorn backup is the only thing that
        would catch it, which is a requirement on that step rather than a note
        here.
      - `retain: 7` governs *two* things — seven backups in S3 and seven
        snapshots kept on the volume's own replicas — and backup cleanup matches
        on the `RecurringJob` label, so it only deletes what this job created.
        7 is comfortably under `recurring-job-max-retention`, whose default is
        100.
      - With `full-backup-interval` unset, `doRecurringBackup` uses
        `BackupModeIncremental` on every run forever, which is what makes the
        step's "incremental at the block layer" claim true rather than hopeful.
      - The generated CronJob is `ForbidConcurrent` and carries no `timeZone`,
        so `0 2 * * *` is **02:00 UTC** — the same clock the CNPG
        `ScheduledBackup` and the two restic CronJobs are staggered on. It
        deliberately shares the hour with CNPG's base backup: finding 7's
        stagger exists for one restic repository lock, and this job shares
        neither a repository nor a volume with any of them.

      One file outside the step's list changed, because this step made its
      instructions wrong rather than merely stale:
      [`docs/disaster-recovery.md`](../../disaster-recovery.md) told a reader to
      recreate `aerie-pg-restore-restic` from the `/aerie/backup/*` parameters
      before unsuspending the Job. That is now the opposite of the truth, and it
      is a paragraph someone reads at 3am. Corrected in place to name the
      `restic` Secret and `${RESTIC_S3_REPOSITORY}` — along with its claim that
      the Job replays a `pg_dumpall`, which finding 6 has been the answer to
      since 4b.1. 8b.14 still rewrites that file; this was the minimum that
      keeps it from lying in the meantime.

      **What remains is the cluster half**, and it is the same pass 8b.9 left
      open — an unavailable backup target and a `RecurringJob` that has never
      run are indistinguishable from the bucket, so neither box is ticked until
      both are done, in this order:

      1. Reconcile `infra-controllers`, then restart **one** `longhorn-manager`
         pod (`longhorn.yaml` note 5) and read the CR back:
         `kubectl -n longhorn-system get backuptargets.longhorn.io default -o
         jsonpath='{.spec.backupTargetURL} {.spec.credentialSecret}
         {.status.available}'`. Not a `settings.longhorn.io` read — 8b.9's
         correction, which **8b.16's gate bullet still has in the old shape and
         needs updating with it.**
      2. Reconcile `infra-config`; confirm `kubectl -n longhorn-system get
         recurringjob` lists `aerie-critical-daily` and that a CronJob
         `aerie-critical-daily-c` exists beside it.
      3. `kubectl -n aerie delete job aerie-pg-restore` and let Flux recreate
         it — a pod template is immutable, so the `secretKeyRef` rename does not
         apply over the existing Job. Flux reports the failed apply and leaves
         the old Job in place, still referencing a Secret that is about to stop
         existing.
      4. Run the restore Job once against the new Secret (the file header's copy
         command), *then* `kubectl -n aerie delete secret
         aerie-pg-restore-restic`. That order, not the step's: a restore proved
         against the new credential before the old one is thrown away costs one
         extra day and removes the only way this fails silently.
      5. After one night, three `backups.longhorn.io` completed and a
         `backupstore/` prefix in the bucket.

      **Cluster half done 2026-08-24, except one item that is the operator's
      to make.** Steps 1 and 2 were already true. Step 3 was not, and it was
      louder than the step implied: the stale `aerie-pg-restore` Job had
      `data-schema` reporting `Job.batch "aerie-pg-restore" is invalid:
      spec.template: field is immutable` on every reconcile, which took
      `apps` down with it through `dependsOn` - so the immutable pod template
      this step predicted had, by the time it was found, stopped the whole
      application tenant from reconciling rather than merely failing its own
      apply. Deleting the Job cleared both within ninety seconds; Flux
      recreated it suspended.

      Step 5 did not wait for the night. The `RecurringJob`'s cron was moved
      to `*/5 * * * *` long enough for one run and put back: three
      `backups.longhorn.io` objects reached `Completed` (77.6 MiB, 81.8 MiB
      and 348.1 MiB of snapshot), `longhorn_volume_last_backup_at` went from
      0 to a unix timestamp on all three volumes, and
      `kube_cronjob_status_last_successful_time` appeared for
      `longhorn-system/aerie-critical-daily` - which is also what let 8b.11's
      rules be written against a series that exists rather than one that
      should. **The generated CronJob is named `aerie-critical-daily`, not
      `aerie-critical-daily-c`** as step 2 above says.

      Step 4 is **not done and is deliberately left for a person**:
      `restore.sh` runs `pg_restore` into the live `aerie` and `quartz`
      databases, which is a write to production that no automated run should
      make on its own. It is in this phase's manual list, and 8b.15's
      rehearsal - a restore onto a scratch Postgres - is the cheaper way to
      prove the same credential. `aerie-pg-restore-restic` therefore still
      exists, which 8b.16's gate asserts against; that assertion is correct
      and will fail until step 4 happens.

- [x] **11. The alert family** —
      `deploy/cluster/observability/config/alerts/backup.yaml`.

      A fourth `PrometheusRule` beside
      [`cluster.yaml`](../../../deploy/cluster/observability/config/alerts/cluster.yaml)
      and `flux.yaml`, selected the same way and for the same reason
      (`ruleSelectorNilUsesHelmValues: false`). Finding 8 is why this is the
      whole of the work: no exporter, no sidecar, no scrape target.

      Five rules, in the order of how much they would cost you:

      | Alert | Shape | `for` | severity |
      |---|---|---|---|
      | `ResticBackupTooOld` | `time() - kube_cronjob_status_last_successful_time{namespace="aerie",cronjob="aerie-backup"} > 36h` | 1h | critical |
      | `ResticBackupJobFailed` | `kube_job_status_failed{namespace="aerie",job_name=~"aerie-backup.*"} > 0` | 15m | warning |
      | `ResticVerifyStale` | same shape as the first, against the verify CronJob, `> 10d` | 1h | warning |
      | `CNPGBackupTooOld` | `time() - cnpg_collector_last_available_backup_timestamp{namespace="aerie"} > 36h` | 1h | critical |
      | `LonghornBackupTooOld` | the first rule's shape against the CronJob Longhorn creates for the `RecurringJob`, in `longhorn-system` | 1h | warning |

      36 hours, not 25: a daily backup that misses one night is a backup that
      missed one night, and paging for it at 03:11 trains someone to ignore the
      rule that matters. Ten days for the weekly verify, same reasoning one
      cadence up.

      **Every one of these needs an `absent()` sibling**, and that is the part
      that is easy to skip and expensive to have skipped. `time() - <a series
      that does not exist>` produces no samples, so a rule whose metric name is
      wrong, whose CronJob was renamed, or whose CronJob was *deleted* is not a
      firing alert — it is silence, which is exactly what a working backup looks
      like. `absent(kube_cronjob_status_last_successful_time{…})` for 1h,
      severity critical, per CronJob.

      Two names in that table are asserted from documentation and not read off
      this cluster — `kube_cronjob_status_last_successful_time` and
      `cnpg_collector_last_available_backup_timestamp` — and one thing is
      asserted about Longhorn's implementation: that a `RecurringJob` is backed
      by a Kubernetes `CronJob` in `longhorn-system` that kube-state-metrics
      therefore already sees. **Check all three in Prometheus's own expression
      browser before committing**, the same way
      [cluster.yaml](../../../deploy/cluster/observability/config/alerts/cluster.yaml)'s
      comment flags its unverified `EtcdMemberDown` job label. If Longhorn's
      recurring job turns out not to surface as a `CronJob`, fall back to
      `longhorn_backup_state` or to the age of the newest
      `backups.longhorn.io` object via kube-state-metrics custom resource state
      config — and write down which, because a reader cannot tell a deliberate
      fallback from a wrong guess.

      *Exit:* all five (and their `absent()` siblings) appear in Prometheus's
      Rules page in state `inactive` rather than `unknown`, and each expression
      returns a number when pasted into the expression browser. `inactive` and
      `unknown` look nearly identical in the UI and mean opposite things.

      **Done 2026-08-24 — eleven rules, and the step's own instruction to
      check three asserted facts is what earned two of them.**
      [`backup.yaml`](../../../deploy/cluster/observability/config/alerts/backup.yaml)
      carries six alerts and five `absent()` siblings in three groups, and all
      eleven read `inactive`/`ok` in Prometheus's rules API with every
      expression returning a number.

      Of the three facts:

      - `kube_cronjob_status_last_successful_time` is real, with the
        `namespace` and `cronjob` labels the table assumed.
      - A Longhorn `RecurringJob` **is** backed by an ordinary Kubernetes
        CronJob that kube-state-metrics already scrapes, so the fallback this
        step pre-authorized was not needed. (Its name is `aerie-critical-daily`
        — see 8b.10 above.)
      - `cnpg_collector_last_available_backup_timestamp` is real, is scraped,
        and reads **0** on all three instances of a cluster that has completed
        a base backup every night since 4b.10. So does
        `cnpg_collector_first_recoverability_point`. Both render Cluster status
        fields (`status.lastSuccessfulBackup`,
        `status.firstRecoverabilityPoint`) that nothing writes under the
        barman-cloud **plugin** — the path
        [objectstore.yaml](../../../deploy/cluster/data/cluster/objectstore.yaml)
        takes and the only one CNPG 1.28 offers. `time() - 0` is fifty-six
        years, so the rule as specified would have fired every night forever.
        This step's warning was about a rule that can never fire; this is the
        same defect in its loud form, and it is worth naming as a pair.

      **The replacement is a second `CustomResourceState` entry** in
      [kube-prometheus-stack.yaml](../../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml),
      over `backups.postgresql.cnpg.io`, emitting
      `aerie_cnpg_backup_stopped_at` from `status.stoppedAt` with a `phase`
      label — the mechanism this step pre-authorized as Longhorn's fallback,
      spent on CNPG instead. It cost two findings of its own:

      - **`path: [status, stoppedAt]`, not `valueFrom:`.** Written the
        obvious way, kube-state-metrics registered the family, logged
        `Custom resource state added metrics`, answered `/metrics` with HELP
        and TYPE lines, and emitted **zero samples** with no error. A Gauge
        whose path resolves to a map iterates that map's entries, so an
        omitted path is five failed lookups against the object's top-level
        keys. Pointed at the leaf it takes the scalar branch and gets the
        RFC3339-to-unix-seconds conversion. `CNPGBackupMetricAbsent` went
        `pending` within a minute of the rules loading, which is how this was
        found — the sibling doing precisely the job it was added for, on its
        first day, against its own author.
      - **The chart sets no checksum annotation on that ConfigMap**, so a
        Helm upgrade changing only the CustomResourceState config updates the
        ConfigMap and leaves the pod running the old one. It needs
        `kubectl -n observability rollout restart
        deploy/kube-prometheus-stack-kube-state-metrics`.

      Two departures from the table, both argued in the file:
      `kube_job_failed{condition="true"}` rather than
      `kube_job_status_failed` (the latter counts failed *pods* and keeps its
      value after the Job succeeds, so one pod lost to a reboot would page for
      the three days the Job object survives), bounded to failures younger
      than a day; and a sixth rule, `LonghornVolumeBackupStale`, which is
      where 8b.10's detached-volume gap landed. It selects volumes by joining
      `longhorn_volume_last_backup_at` against the PVCs whose class is
      `longhorn-r3` — finding 2's own definition of the wanted set, and a
      better selector than 8a.3's label for the trap 8a.3 names: a volume
      restored without the label keeps its class, so it stays in the rule's
      set and goes stale loudly instead of dropping out of it silently.

      One thing this step did not have to fix and did anyway, because nothing
      could reach the cluster until it was fixed: `infra-config` had been
      failing its health check on an `ExternalSecret` for a camera parameter
      that Parameter Store does not hold, which stalled every Kustomization
      below it — data, apps and both observability layers. That is the exact
      failure
      [`parameters.json`](../../../scripts/secrets/parameters.json)'s own
      schema note predicts for a `kubernetes` block over an unseeded value,
      and the fix is the field beside it: `kubernetesDeferred` until Provision
      2 has seeded `GO2RTC_STREAMS`. It is in this phase's manual list because
      flipping it back is the operator's, not this phase's.

- [x] **12. Give the flows teeth, and prove it once** —
      [`kube-prometheus-stack.yaml`](../../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml),
      Home Assistant.

      [Phase 7](phase-7-cutover.md#what-phase-7-deliberately-does-not-do) is
      explicit that the alerting flows are a POC — routes and providers exist,
      nothing reaches a person — and hands the wiring here on the grounds that
      8b.11's alert is the first one that has to arrive. 6b.8's route already
      points at `home-assistant`; 8a.5 already proved the automation notifies.
      What is left is the middle:

      - **Test-fire through Alertmanager**, not through the webhook. `amtool
        alert add` (or a temporary always-firing rule) into the real route, and
        confirm the notification arrives with the alert's own annotations in it.
        This is the half 8a.5 deliberately did not cover.
      - **Confirm `severity: critical` is distinguishable on the receiving end.**
        Four of 8b.11's rules are warnings and two are criticals; if Home
        Assistant renders both identically, the severity label is decoration and
        the phase should either make the automation branch on it or stop
        setting it.
      - **Write down the silencing discipline** Phase 7 handed over: silence by
        `alertname`, never by receiver. A maintenance window silenced at the
        receiver eats the one alert that was supposed to survive it, and the
        first person to plan a Longhorn upgrade will reach for the receiver
        because it is the easier button.

      *Exit:* a deliberately-fired alert arrives on a phone with its summary
      text intact, and resolves when it clears (`send_resolved: true` is already
      set — this is the run that proves it does something).

      **Done 2026-08-24, and the middle of the path is now measured rather
      than assumed.** A hand-built alert (`AerieAlertPipelineTest`, severity
      critical, one summary annotation) was POSTed to Alertmanager's
      `/api/v2/alerts` from a throwaway pod — through the real route, not
      through the webhook — and Home Assistant's `automation.aerie_alert`
      recorded a trigger **34 seconds later**, which is `group_wait: 30s`
      visible from the far end. The alert carried an `endsAt` 90 seconds out;
      the automation recorded a second trigger at **exactly five minutes**
      after the first. That is `send_resolved: true` working, and it is also
      the answer to a question nobody had asked yet: a resolution is delivered
      on the group's next flush, so `group_interval` is the floor on how long
      "it cleared" takes to arrive. Both timings are in
      [kube-prometheus-stack.yaml](../../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml)'s
      Alertmanager comment.

      **`severity` is not decoration on the receiving end** — checked by
      reading the automation's own config through Home Assistant's API rather
      than by inference. It renders `CRITICAL: <alertname>` with an iOS
      `time-sensitive` interruption level when `commonLabels.severity` is
      critical, `<SEVERITY>: <alertname>` at normal priority otherwise, and
      `RESOLVED: <alertname>` when `status` is resolved; the message body is
      `commonAnnotations.summary`, and repeated notifications collapse on a
      per-alertname-and-severity `tag`. So this step's third option — "stop
      setting it" — does not apply: the split between the phase's two
      criticals and four warnings survives the trip. That automation is not in
      this repository, which is why its shape is now written down in the
      Alertmanager comment: nothing in the tree would otherwise tell a reader
      the label has a consumer.

      **The silencing discipline is written where the route is**, not in a
      runbook that gets read after the fact: silence by `alertname`, never by
      receiver, because the wrong button is the easier one — one matcher on
      `receiver="home-assistant"` eats every alert including the backup-age
      rule the maintenance window is the reason to keep armed.

      **One finding, found here and fixed the same day.** The counter
      `alertmanager_notifications_failed_total{integration="webhook",
      reason="clientError"}` stood at 999 of 1344 notifications. Every one is
      the Watchdog route: Kuma answers 6b.12's pinned push URL with `404
      {"ok":false,"msg":"Monitor not found or not active."}`, every five
      minutes since the route was created, so **the dead-man's switch had
      never completed a cycle**. Uptime Kuma's `monitor` table was *empty* —
      not "the watchdog is missing", but all six of 6b.12's monitors, on an
      installation that has looked provisioned since 2026-08-20.

      The cause is one default and one Kubernetes implementation detail.
      A ConfigMap volume is not projected as files: Kubernetes writes them
      into a timestamped directory, points a `..data` symlink at it, and makes
      every key a symlink to `..data/<key>`. AutoKuma's file source skips
      symlinks unless `AUTOKUMA__FILES__FOLLOW_SYMLINKS=true`, so it scanned
      `/static-monitors`, found six entries it would not open, and logged
      **nothing at all** — no error, no "0 monitors", no warning. Setting it
      created all six within 300ms.

      Worth keeping for its shape: the thing that surfaced this was not the
      component that was broken. AutoKuma was silent and Kuma looked like a
      fresh install; what reported it was Alertmanager's failure counter
      climbing every five minutes because the push URL answered `404 Monitor
      not found or not active` — the dead-man's switch correctly reporting
      that it did not exist. That is the "newer path proving itself over this
      older one" from 6b.8's comment, working in the direction nobody
      designed it for.

      One correction to finding 1 while in there: the Uptime Kuma image
      **does** ship `sqlite3` (`/usr/bin/sqlite3` in `2.4.0-slim`), which is
      how the empty `monitor` table was read. The finding's conclusion is
      unaffected — its load-bearing half is that no single pod can mount both
      RWO volumes, and that is still true — but the sentence "neither image
      ships `sqlite3`" is wrong for this one.

      **The fix had a tail, and it is the more interesting half.** Setting the
      flag and restarting created six monitors; restarting again created six
      *more*. AutoKuma 2.x tracks what it has created in its own SQLite
      database at `/data/autokuma.db`, with no setting to move it, and nothing
      mounted `/data` — so every pod start read an empty database, concluded
      it had created nothing, and did it all again. Two Watchdog monitors then
      shared the one pinned push token, which means one of them receives every
      push and the other goes red on its own retry timer and notifies a phone
      that the dead-man's switch is dead. The tell had been in the log from
      the beginning and reads as routine: `Migrating database to version 1` on
      *every* start is not an upgrade, it is a fresh database being built.
      `/data` now has a 1Gi `longhorn-r2` PVC, and a restart after it landed
      created nothing — which is the actual proof.

      One consequence of the cleanup worth knowing before repeating it:
      deleting the monitor rows while Kuma was running left it inserting
      `stat_hourly` rows against its own pre-delete in-memory state, so every
      beat failed with `SQLITE_CONSTRAINT: UNIQUE constraint failed:
      stat_hourly.monitor_id, stat_hourly.timestamp` and the monitors wedged.
      A pod restart cleared it. Delete monitors with Kuma stopped, or restart
      it immediately afterwards.

      **Armed and verified 2026-08-24 20:34Z**: six monitors, no duplicates,
      Alertmanager logs `Notify success` for the `kuma-watchdog` receiver, and
      `alertmanager_notifications_failed_total{reason="clientError"}` has
      stopped climbing for the first time since the route existed.

      Still owed by a person, and in the manual list: **look at the phone.**
      Two notifications were delivered to Home Assistant at 18:05:09Z and
      18:10:09Z on 2026-08-24 — a `CRITICAL: AerieAlertPipelineTest` and its
      `RESOLVED:` twin. What is proved here is that the automation ran; that
      it reached a device is 8a.5's result, not this run's.

- [x] **13. A backup row on the Delivery dashboard** —
      `deploy/cluster/observability/config/dashboards/`.

      Small, and here rather than in Phase 9 because the alert above is the only
      thing in this phase that tells you backups stopped, and an alert is a
      report of a state change, not a way to look at the state. Four stat
      panels: age of the newest restic snapshot, age of the newest CNPG backup,
      age of the newest Longhorn backup, age of the last successful verify.
      Same `grafana_dashboard`-labelled ConfigMap shape as its five neighbours.

      *Exit:* the row renders four numbers, all of them hours rather than
      `No data`.

      **Done 2026-08-24.** A `Backups` row appended to
      [delivery.json](../../../deploy/cluster/observability/config/dashboards/delivery.json)
      beside `Now`, `CI`, `CD` and `Rollout` — the same ConfigMap, so nothing
      new to register. All four expressions return a number against the live
      Prometheus: 15.1h, 16.2h, 14.9h and 14.8h respectively, which is hours
      rather than `No data` and is the exit condition.

      Two of the four are not the panel this step described, and the
      descriptions in the file carry the reason rather than leaving it to a
      reader to notice:

      - **Longhorn is the stalest of the three volumes, not the newest.** The
        newest would stay green through exactly the gap 8b.10 found — one
        detached volume skipped with a warn log while the other two succeed.
        A single number that cannot show the failure is decoration.
      - **CNPG reads `aerie_cnpg_backup_stopped_at`**, 8b.11's
        CustomResourceState series, for the reason that step documents at
        length.

      Thresholds are not chosen twice: orange where a run has been missed
      (26h, 8d), red exactly where the matching rule in `backup.yaml` fires
      (36h, 10d). A panel that reddens before its alert teaches an operator to
      distrust the colour; one that reddens after teaches them to distrust the
      panel. And `noValue` says what an absent series means instead of
      printing `No data`, since absence is precisely what the `absent()`
      siblings exist to catch.

- [x] **14. Rewrite `docs/disaster-recovery.md`** —
      [`docs/disaster-recovery.md`](../../disaster-recovery.md).

      [7c.10](phase-7-cutover.md#phase-7c--the-old-server-docker-host--k3s-node)
      left it accurate but minimal, on purpose, and named this phase as what
      turns it back into a document someone can follow. It is currently the
      Phase 0 document with a correction pasted over it, and it is wrong in a
      way worth naming: **its "What's backed up, and how" table lists OpenSearch
      and Prometheus**, which `backup.sh` never backed up and
      `compose.backup.yml`'s own comment said it deliberately did not. A table
      that promises two restores that were never possible is the specific kind
      of document that gets read at 3am.

      What it must contain after this phase, and did not before:

      - the five-row table from [What this phase is](#what-this-phase-is),
        including the row that says nothing backs up Prometheus and OpenSearch
        **and why that is a decision**
      - four restore procedures, each written as commands: one database from
        restic, one database to a point in time from CNPG (4b.10's rehearsal,
        promoted from a phase-doc paragraph into a runbook), one Longhorn volume
        from a backup, and the whole parameter tree from 8b.7's export
      - the sentence that has been true since Phase 0 and is now the *only*
        thing standing between an intact backup and an unrecoverable one: the
        printed `RESTIC_PASSWORD` is the root of trust, GitHub Actions secrets
        are write-only, and 8b.7 means the export of the parameter store is
        sealed with it too
      - where 8a.2's local repo is, on which machine, and that it is not in AWS

      *Exit:* someone who was not in the room can restore the `aerie` database
      by following it, without reading a phase document. That is testable and
      8b.15 tests it.

      **Rewritten 2026-08-24**, from 184 lines that described one backup to 393
      that describe five and tell you how to get each one back. The wrong table
      is gone: it promised OpenSearch and Prometheus restores that were never
      possible, and the row that replaces it says nothing backs them up **and
      why that is a decision** — the two largest volumes in the cluster and the
      two whose absence nobody would act on.

      Three of the four restore procedures were run rather than written:

      - **The Longhorn one was rehearsed while writing it.** A new volume
        restored from the S3 backup target, a static PV and PVC bound to it, a
        throwaway pod mounting it — and Kuma's `kuma.db` came back at 286 KB
        with its `-wal` and `-shm` beside it, which is finding 1's whole
        argument arriving as evidence rather than as a citation. Every
        procedure step in the document is a command that ran, including the
        teardown order. Two things worth having found: the restore is **lazy**
        (the volume reports `detached` within seconds and the data arrives on
        attach, so an empty-looking volume is normal), and `status.url` on the
        `backups.longhorn.io` object is the restore URL verbatim — assembling
        it by hand from the bucket name is the way to get it wrong.
      - **The parameter read-back was run**: `restic dump latest
        /tmp/aerie-backup/parameters.json` out of the local repository returned
        21 parameters, which is exactly the count of `required: true` entries in
        `parameters.json`. The document shows how to list names and how to pull
        one value, with the warning that the second one prints a secret.
      - **The restic database restore** is what the weekly verify Job does, and
        it ran successfully at 03:24 on 2026-08-24 — restore, `pg_restore` into
        a scratch Postgres, 26 tables counted.

      The fourth, `restore-job.yaml` against the live databases, is written
      down as **not yet exercised** in the same voice as everything else,
      because it writes over production and 8b.15 is the cheaper place to prove
      it. The document also carries the root-of-trust paragraph this step asked
      for (the printed password opens the box that contains the copy of
      itself), where the three copies are and which one is not in AWS, the
      silence-by-alertname rule, and a Known Gaps section that names the Kuma
      watchdog rather than leaving it in a commit message.

- [x] **15. The first rehearsal, and the schedule for the rest**

      A quarterly DR rehearsal onto throwaway VMs, and **the first one happens
      in this phase** rather than being scheduled and deferred — the same
      argument [Phase 0's gate](phase-0-backup-and-dr.md) made for not starting
      Phase 1 until a restore had actually been performed. 7c.10 makes this
      phase's rehearsal unusually valuable: it is the first read of a document
      that was just rewritten, and the first rehearsal after a rewrite is the
      one that finds what the rewrite missed.

      The rehearsal is 8b.14's document, followed literally, by someone holding
      only the offline password and the repo. Scope it honestly: **restoring the
      whole cluster is not the exercise.** Restoring `aerie` from restic onto a
      scratch Postgres, restoring one Longhorn volume, and reading one secret
      back out of the parameter export are three things that either work or
      do not, and they cover every mechanism this phase built.

      Schedule the rest: a calendar entry is fine, and a `schedule:`-triggered
      workflow that opens an issue is better, because it survives the person.

      *Exit:* a rehearsal happened, and 8b.14 got at least one correction out of
      it. A rehearsal that produced no corrections was probably a re-read rather
      than a rehearsal.

      **Done 2026-08-24, and deliberately not the way this step imagined it.**
      The step asked for a person holding only the offline password, following
      8b.14's document literally, finding what it left out. What was built
      instead is [`Invoke-DrRehearsal.ps1`](../../../scripts/k3s/Invoke-DrRehearsal.ps1):
      the two data-recovery exercises as one command. The argument for the
      swap is the one that matters at 3am — **in a crisis nobody should be
      reading steps.** A restore path exercised by running a command is
      exercised identically every quarter by whoever is holding the pager; a
      restore path exercised by reading is exercised as well as the reader is
      rested. The document keeps all four hand procedures, because they are
      what is left when the cluster that would run the Job is itself what was
      lost, and it now says exactly that at the top of its restore section.

      One Job, built from the live `aerie-backup` CronJob's own pod template —
      so the image, the `restic` Secret, both repository strings and the SMB
      mount are whatever the nightly backup actually runs with, rather than
      restated. A rehearsal that restated any of them could pass against a
      repository nothing writes to any more, which is the exact failure it
      exists to catch. Per repository it restores the newest `daily` snapshot,
      stands up a Postgres inside the Job's own `/tmp` and tears it down,
      `pg_restore`s both dumps, counts tables **against the live database
      schema by schema**, lists the parameter export by name, and proves the
      copy of `RESTIC_PASSWORD` inside the snapshot is the credential that
      opened it — by SHA-256, so neither value is ever printed into a log that
      goes wherever `kubectl logs` goes. Read-only throughout: `snapshots` and
      `restore` take no exclusive lock, so a run at 03:10 exactly cannot
      collide with the nightly backup, and the only contact with production is
      one `SELECT count(*)` over `information_schema`.

      **First run: 17 checks, both repositories, 5.9 minutes.** `aerie` and
      `quartz` came back out of the local share copy (snapshot `49a784f5`) and
      out of S3 (`2d30c62b`), `pg_restore` clean both times; 21 parameters
      exported covering all 20 `required: true` entries; `param_mode=600`, so
      restic preserved the `umask 077` the export was written with; and
      `password_match=yes` against both — the root-of-trust claim in 8b.14,
      confirmed as an assertion rather than as a sentence.

      **And it found something, which is what makes it a rehearsal rather than
      a re-read.** The restored `aerie` had 26 tables against the live
      database's 27, and chasing that one-table gap turned up a much larger
      one behind it. The missing table was `CameraConnections`, from a
      migration that landed after the 03:10 snapshot — ordinary, and gone by
      the next morning. But listing the dump properly showed the backup also
      carries `game` and `gather` schemas, **and that both sanity checks in
      this design were counting `table_schema IN ('public','storage')** —
      correct when
      [`Storage`](../../../src/Aerie.Api/Modules/Storage/StorageContext.cs) was
      the only module context, and quietly wrong from the moment `gather`
      (2026-08-22) and `game` (2026-08-23) were added days before this phase
      closed. A dump that had lost both modules entirely would have counted 27
      tables and **passed the weekly verify Job**. That is the precise failure
      the comment above the line claimed to prevent, defeated by naming the
      schemas it knew about.

      Fixed at the source in both places —
      [`cluster-verify.sh`](../../../containers/backup/scripts/cluster-verify.sh)
      and
      [`restore.sh`](../../../deploy/cluster/data/schema/restore.sh) now count
      every non-system schema and print the per-schema breakdown — and the
      rehearsal asserts what neither of them can: **every schema the live
      database has is present in the restore**, failing on an absent one and
      only warning on a smaller count, because those two are different
      findings and a total cannot tell them apart. `cluster-verify.sh` still
      cannot make that assertion weekly, and the reason is deliberate:
      `verify-cronjob.yaml` gives it `RESTIC_PASSWORD` and the repository and
      no database credential at all. Widening the weekly Job to hold one is a
      decision about blast radius, not a fix, so it is written down here
      rather than taken.

      **Re-run after the fix: 19 checks, exit 0, 8.8 minutes.** Both
      repositories restore all four schemas — `game 3, gather 3, public 22,
      storage 4` — against a live database carrying `public 23`, and the one
      remaining warning names the difference in the terms that make it
      readable: `restored 32 vs live 33 (public 22 vs 23)`. That is the
      `CameraConnections` migration, and it will be gone by the next morning's
      snapshot. Two checks more than the first run because the schema
      assertion is new; the finding is what added them.

      **The lesson is the pattern, not the instance.** One schema per module
      ([`Modules/README.md`](../../../src/Aerie.Api/Modules/README.md)) means
      every new module is another chance for some list of names elsewhere to
      go stale silently. Any check that enumerates what the application
      contains will narrow itself the next time the application grows.

      Two things are deliberately outside it. **The Longhorn volume restore**
      (8b.14's procedure 3) restores infrastructure rather than data, is the
      one procedure already exercised end to end, and wants its own script.
      **Restoring over the live databases** stays
      [`restore-job.yaml`](../../../deploy/cluster/data/schema/restore-job.yaml),
      a suspended Job a person un-suspends deliberately: a rehearsal that
      could overwrite production by getting an argument wrong is a worse risk
      than the one it retires. Both remain named in 8b.14's Known Gaps.

      **The schedule half, which was already done, now points at the script.**
      [`dr-rehearsal.yml`](../../../.github/workflows/dr-rehearsal.yml) is the
      `schedule:`-triggered workflow this step says is better than a calendar
      entry: `0 9 1 1,4,7,10 *`, one issue per quarter, labelled
      `dr-rehearsal`, refusing to open a second while one is still open — a
      rehearsal that slipped a quarter should be one issue that is three
      months old, not four identical ones. Its checklist used to be three
      procedures read by hand; the first item is now the one command, and what
      is left by hand is what the script deliberately does not cover. One item
      is new, and it is the honest cost of scripting this: **read the document
      as if the cluster were gone.** The hand procedures are what remains when
      there is no cluster to run the Job on, which makes them the half that
      now rots unnoticed — the script cannot exercise them, so the issue has
      to ask a person to.

- [x] **16. Phase gate as a command** — `scripts/k3s/Test-Backup.ps1`,
      wrapped by `.github/workflows/verify-backup.yml`, in the exact shape
      3b.13, 4b.11, 5b.14, 6b.15 and 7c.11 established: read-only, **not**
      numbered into the Provision sequence, does not stop at the first failure,
      and a check it cannot evaluate is a failure rather than a skip.

      Assert, at minimum:

      - the `restic` and `longhorn-backup-target` `ExternalSecret`s report
        `SecretSynced`, with three keys and two respectively, and the expected
        set is read from
        [`parameters.json`](../../../scripts/secrets/parameters.json) rather
        than hardcoded — 4b.11's rule
      - the newest successful run of each of the three backup CronJobs is
        younger than its own alert threshold, which is the manual form of
        8b.11's rules and the thing that catches a rule that is silently
        `absent()`
      - **`cutover-final` is still present in both repos** — 8b.6, and the check
        that no retention change has quietly reached it
      - exactly three Volumes carry `recurring-job-group.longhorn.io/aerie-critical`,
        and every `longhorn-r3` PVC's Volume is one of them (8a.3's finding: a
        restored volume comes back unlabelled and silent)
      - the backup target URL and its credential Secret read back from the
        **`backuptargets.longhorn.io/default` CR**, and
        `freeze-filesystem-for-snapshot` from `settings.longhorn.io` — 8b.9's
        correction, which this bullet carried in the old
        three-settings-reads shape until 8b.16 was written. The setting is the
        input; the CR is what Longhorn resolved, and `status.available` on it
        is the only thing that distinguishes a configured target from a
        reachable one. Finding 1's whole argument rests on the freeze setting,
        and Longhorn swallows a bad value
      - the newest snapshot in each restic repo contains `aerie.dump`,
        `quartz.dump` and `parameters.json`
      - `aerie-pg-restore-restic` does **not** exist (8b.10's deletion, which is
        otherwise the kind of cleanup that gets half-done)
      - every rule in `backup.yaml` is loaded and `inactive`, not `unknown`
      - the [portability check](design.md#verification) over the new files: grep
        `deploy/cluster/data/backup/` and the Longhorn additions for the bucket
        names, the share host and the domain — each should appear only as a
        `${...}` substitution

      *Exit:* the workflow exits 0. Leave this box unticked until it has, for
      the same reason every gate before it stayed unticked: a gate that has
      never passed has proved nothing.

      **Written 2026-08-24, and it runs: 20 of 21 checks pass.** The box stays
      unticked, because the twenty-first is a real failure and this gate's
      whole value is that it says so.

      [`Test-Backup.ps1`](../../../scripts/k3s/Test-Backup.ps1), wrapped by
      [`verify-backup.yml`](../../../.github/workflows/verify-backup.yml), in
      the established shape — read-only, unnumbered, no early exit, and a
      check it cannot evaluate is a failure. Every assertion this step asked
      for is in it, plus three the writing produced: that the backup target is
      *reachable* (`status.available`), that the Longhorn recurring job has
      actually produced three completed `backups.longhorn.io` objects, and
      that the thresholds in `backup.yaml` still match the ones the gate
      compares against — a gate and an alert that disagree silently are worse
      than either alone.

      **The one deviation, and it is deliberate.** Two exit criteria —
      `cutover-final` in both repositories, and the newest snapshot holding
      all three files — cannot be answered by reading Kubernetes objects.
      They need `restic` against both repositories, and nothing long-lived in
      this cluster has the password, the S3 credential and the SMB mount at
      once. So the gate creates **one short-lived Job, built from the live
      `aerie-backup` CronJob's own pod template**, replaces its command with
      `snapshots`, `ls` and `dump`, reads the log and deletes it. Building it
      from the live CronJob rather than restating the image and credentials is
      what makes the check meaningful: a probe that named its own repository
      could pass against one the nightly backup no longer writes to.
      `-SkipResticProbe` refuses even that, and fails the two checks instead —
      which is the honest price rather than a way to make the gate quiet.

      **The twenty-first check passed later the same day, and the box is
      ticked: 21 of 21.** What it was waiting for was 8b.10's step 4, and that
      step turned out to be two different things wearing one sentence.
      Running `restore-job.yaml` literally would `pg_restore --clean` the
      03:10 snapshot over the live `aerie` and `quartz`, discarding every
      write since — seventeen hours of them, on the afternoon this was asked.
      What the step is actually defending is narrower: that the rewired
      `restic` Secret opens the **S3** repository and that what is in there
      restores.

      So that is what was proved, and with the Job's own parts rather than a
      substitute for them: a Job built from `aerie-pg-restore`'s live pod
      template — same image, same `restic` Secret, same
      `${RESTIC_S3_REPOSITORY}` — running the image's own
      [`cluster-verify.sh`](../../../containers/backup/scripts/cluster-verify.sh)
      with its repository pointed at S3 instead of the share, and with
      `PGHOST`/`PGUSER`/`PGPASSWORD` unset so nothing could reach `aerie-pg`
      even by accident. It restored 74.989 MiB from snapshot `2d30c62b` and
      `pg_restore`d **26 tables** into a scratch Postgres. Then
      `aerie-pg-restore-restic` was deleted, and the gate went green.

      The remaining difference between that and the literal step — that
      `restore.sh` can write into the live cluster — is what 8b.15's rehearsal
      is for, onto a scratch Postgres. Deleting the old Secret before proving
      *that* is safe for the reason 8b.10 was worried about in the first
      place: the credential, the repository and the artifact are the parts
      that could silently be wrong, and all three are now exercised.

      Three things the run found:

      - The `restic` Secret's three keys and `longhorn-backup-target`'s two
        both match what `parameters.json` declares, in both directions.
      - The parameter export holds **21** parameters, exactly the count of
        `required: true` entries in `parameters.json` — the floor 8b.7's
        `[]`-guard was written to defend, asserted rather than assumed.
      - A ``` `restic` ``` inside a double-quoted PowerShell string is a
        carriage return: `` `r `` ate its own `r` and the message read "the
        estic Secret". Found by reading the gate's own output, which is the
        only reason anyone would.

      One check failed on the first run and has passed on every run since:
      the `longhorn-backup-target` ExternalSecret's Ready condition, against
      an object `kubectl` reported as `SecretSynced` thirty seconds later.
      Either that one call returned empty and the probe substituted `{}`, or
      the object was mid-write. The check now distinguishes "no object" from
      "no Ready condition" so the next occurrence says which, rather than
      being re-diagnosed from scratch.

---

## Where these files live

```text
deploy/cluster/
  data/
    backup/                       # new tenant of the data layer
      local-repo-volume.yaml      # 8b.4, SMB PV/PVC for the local repo
      backup-cronjob.yaml         # 8b.5 + 8b.6 + 8b.7
      verify-cronjob.yaml         # 8b.8
      kustomization.yaml
    schema/
      restore-job.yaml            # 8b.10, 5 secretKeyRefs -> 3 + 1 token + 0
  infrastructure/
    controllers/
      longhorn.yaml               # 8b.9, one setting + defaultBackupStore
    config/
      longhorn-recurringjob.yaml  # 8b.10, + one line in kustomization.yaml
      external-secrets/           # 8b.1, regenerated - two new files
  observability/config/
    alerts/backup.yaml            # 8b.11
    dashboards/                   # 8b.13, one more ConfigMap

containers/backup/
  Dockerfile                      # 8b.3, + aws-cli, - supercronic ENTRYPOINT
  crontab                         # 8b.3, deleted - the CronJob is the schedule
  scripts/
    cluster-backup.sh             # 8b.3, was backup.sh
    cluster-verify.sh             # 8b.3, was verify-restore.sh
    export-parameters.sh          # 8b.7
    init-repos.sh                 # unchanged

scripts/
  secrets/parameters.json         # 8b.1, three flips + two additions
  k3s/cluster-config.json         # 8b.2, three keys
  k3s/Test-Backup.ps1             # 8b.16
.github/workflows/
  verify-backup.yml               # 8b.16

docs/disaster-recovery.md         # 8b.14, rewritten
```

One ordering rule, learned twice in this phase and belonging to every phase
after it. **A `cluster-config.json` key and its Provision 4 dispatch land
before the manifest that reads the token — never in the same push, never
after.** 4b.3 established seed-then-manifest for `parameters.json` because the
ExternalSecret generator refuses a target on an unseeded parameter; the same
shape applies here for a different mechanism and a worse blast radius. Flux
substitutes in strict mode, so a manifest carrying a token no ConfigMap has
does not degrade — it fails the whole Kustomization's build, and every
Kustomization that `dependsOn` it stalls behind it until someone notices. 8b.1
hit the `parameters.json` half of this and wrote down that the seed reads the
*remote*, not the working tree; 8b.4 then hit the `cluster-config.json` half by
committing its PV one dispatch ahead of `RESTIC_LOCAL_SUBPATH`, and `apps`
stopped reconciling for the twenty minutes it took to spot. Two mechanisms, one
rule: **the value reaches the cluster first.**

One structural note. **`data/backup/` is a third tenant of the data layer, not a
new Kustomization and not `observability/`.** It sits with the database it dumps
because it reads `aerie-pg-app` and `aerie-pg-rw` and because
[`schema/restore-job.yaml`](../../../deploy/cluster/data/schema/restore-job.yaml)
— its exact inverse — is already one directory over sharing the same Secret. The
Longhorn half of the phase is in `infrastructure/` for the reason that split
exists: `longhorn.yaml` is the controller, the `RecurringJob` is an instance of
what it enables, and layer 1's `wait: true` is what makes the second's assumption
true.

## What Phase 8 deliberately does not do

- **It does not back up Prometheus or OpenSearch.** Both rebootstrap from their
  own config, which is committed;
  [`compose.backup.yml`](phase-7-cutover.md#what-this-phase-deletes-and-what-survives)
  said so in Phase 0 and nothing since has changed it. What *is* new is that
  8b.14 writes it down as a decision, because the document currently claims the
  opposite.
- **It does not re-implement Postgres backup.** Phase 4 did that. This phase
  adds the second, logical, portable copy and the alert, and touches neither the
  `ObjectStore` nor the `ScheduledBackup`.
- **It does not put a Longhorn backup on every volume.** Finding 2 — the
  `default` group would, and the two volumes it would add are the two nobody
  wants. Three labels, asserted by the gate.
- **It does not build a metrics exporter.** Finding 8 — kube-state-metrics and
  CNPG's collector already carry every series the alerts need, which retires the
  open question [6b.8](phase-6-observability.md) left for this phase.
- **It does not move `RESTIC_PASSWORD` out of GitHub Actions secrets, or make
  the parameter store its home.** The printed offline copy stays the root of
  trust; 8b.7 makes that *more* load-bearing rather than less, which is the
  argument [docs/secrets-architecture.md](../../secrets-architecture.md#what-deliberately-stays-out)
  already makes and this phase completes rather than revisits.
- **It does not encrypt or seal anything new.** The export is protected by the
  repository password and by nothing else, deliberately: a second secret to
  recover the first is the failure this whole design is arranged against.
- **It does not automate the quarterly rehearsal.** A rehearsal a machine
  performs is a test, and the cluster already has one (8b.8). The thing being
  rehearsed is a **person** following a document, and automating that away
  removes the only failure mode the exercise exists to find.
- **It does not consolidate the two notification paths.** Kuma and Alertmanager
  both reach Home Assistant by different routes; 6b.8 bought that redundancy on
  purpose and Phase 9 owns the question.
- **It does not restore anything to production.** 8b.10 runs the restore Job
  once to prove the repointed Secret works — against the cluster's own data,
  with `--clean --if-exists`, which is destructive. Do that on a day when
  losing the interval since the last dump is acceptable, or against a throwaway
  Cluster, and know which you chose.

## Additions this phase makes to other phases

Recorded here, to be written into the phases that own them:

- **Phase 9 inherits the parameter export as a bootstrap input.**
  `scripts/restore.sh`, which Phase 9 already owes, now has an obvious first
  step it did not have: read 8b.7's `parameters.json` out of the repo and seed
  the tree from it, which is what makes "restore onto new hardware" a script
  rather than an operator retyping twenty values from paper. The offline
  `RESTIC_PASSWORD` remains the one thing that cannot come from anywhere.
- **Phase 9's `values.yaml` gains three keys.** `RESTIC_S3_REPOSITORY`,
  `RESTIC_LOCAL_SUBPATH` and `LONGHORN_BACKUP_BUCKET` are per-installation by
  construction, and the site-repo split is where they stop being repository
  variables.
- **Phase 9 owns whether the local repo should be somewhere other than the house
  share.** 8a.2 put it there because 7c.1 already had, and because the share is
  a different machine — but it is a machine with no backup of its own, and
  "two local copies" from [design.md](design.md#goals)'s rule-of-three is
  satisfied only in the sense that the cluster and the share are different
  boxes. A second operator with a NAS should be able to say so in one value.
- **Phase 9 owns the `docs/` consolidation this phase does not finish.**
  `disaster-recovery.md` is rewritten here because it is operationally
  load-bearing; `metrics-architecture.md` and
  `monitoring-alerting-architecture.md` remain Phase 9's, per Phase 6's
  assignment, and `cluster-architecture.md` should absorb the *architecture*
  half of what 8b.14 writes while leaving the runbook half where someone can
  find it at 3am.
- **The alerting flows are no longer a POC after 8b.12**, which retires a
  sentence in Phase 7's "deliberately does not do" list and makes the silencing
  discipline written there enforceable rather than advisory. The first
  maintenance window planned after this phase is the test of it.
