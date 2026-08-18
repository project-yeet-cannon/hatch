[← Phase 3](phase-3-platform-services.md) · [Design & decisions](design.md) · [Phase 5 →](phase-5-app-tier.md)

---

# Phase 4 — Data tier

**Status: Not started**

> Re-scoped, the same way Phase 3 was. The six bullets this file used to hold
> were one sentence each; working them through against the repo and against
> CloudNativePG 1.30 — the version [`controllers/cloudnative-pg.yaml`](../../../deploy/cluster/infrastructure/controllers/cloudnative-pg.yaml)
> actually pins — turned up two APIs that have moved out from under the plan,
> one setting that would take the site down for writes every Patch Tuesday, and
> a data-cutover step that no phase owns.
>
> - **`minSyncReplicas` is the deprecated API, and the live one has a default
>   that fights Phase 1.** CNPG replaced `minSyncReplicas`/`maxSyncReplicas`
>   with `.spec.postgresql.synchronous` (`method`, `number`, `dataDurability`);
>   the two are mutually exclusive. `dataDurability` defaults to `required`,
>   which **pauses all writes** whenever the synchronous standby is unavailable
>   — and [Phase 1's staggered Windows Update reboots](phase-1-node-substrate.md)
>   make one node unavailable on a schedule. On a two-node cluster that is a
>   write outage per reboot window, arriving as "the site is down" with nothing
>   in Postgres's logs but a wait (4b.6).
> - **`barmanObjectStore` is deprecated and the S3 path is now a plugin.** WAL
>   archiving in-tree has been deprecated since operator 1.26 in favour of the
>   Barman Cloud Plugin. It still works in 1.30 and is still the default, so a
>   plan written from older docs *runs* — and then Phase 8, whose whole subject
>   is backup, inherits a migration. The plugin's one hard prerequisite is
>   cert-manager, which Phase 3b.7 already installed, and its chart lives in the
>   HelmRepository [`cloudnative-pg.yaml`](../../../deploy/cluster/infrastructure/controllers/cloudnative-pg.yaml)
>   already declares. It costs one file (4b.4).
> - **The restore is not a one-off, and Phase 7 has a hole where the data
>   cutover should be.** Phase 7 rebuilds the old prod box as the third k3s
>   server — and that box is the one holding the live database. Nothing in any
>   phase says "sync the data one last time before you wipe it". A restore run
>   once by hand in Phase 4 is stale by then, so the restore is built here as a
>   *repeatable* Job that Phase 7 re-runs against a final dump (4b.9).
> - **`pg_dumpall` is the wrong artifact to restore into CNPG.** Phase 0's
>   backup emits one `pg_dumpall` file, which carries `CREATE ROLE user` and
>   `CREATE DATABASE aerie` — both of which collide with what CNPG's `initdb`
>   bootstrap and the `Database` CRD have already created, and neither of which
>   is wanted: the whole point of Finding 2 is that role `user` with password
>   `password` stops existing. One small addition to Phase 0's script — per
>   database `pg_dump -Fc` alongside the dumpall — turns the restore into
>   `pg_restore --no-owner` into an empty database and removes the conflict
>   entirely (4b.1).
> - **The Quartz DDL Job and the restore are two paths to the same database, and
>   only one of them runs.** Finding 3 flags that the DDL has no
>   `IF NOT EXISTS` and so is not re-runnable. Guarding it does more than make
>   it safe to retry: it makes the DDL Job a no-op after a restore and the whole
>   thing on a fresh install, so one ordering serves both a migrating operator
>   and a second operator with no dump at all (4b.8).
> - **`imageName` left unset means the PostgreSQL major version is whatever the
>   operator defaults to that release.** Prod is `postgres:18.4`
>   ([Dockerfile.db](../../../containers/aerie-db/Dockerfile.db)); an unpinned image is a
>   major-version change arriving inside a routine chart bump, and `pg_restore`
>   only goes forwards (4a.5).
> - **`s3Credentials.region` is a Secret reference, not a string.** It is a
>   `SecretKeySelector` in the barman-cloud API, so `${AWS_REGION}` cannot be
>   substituted into it the way every other operator value in `deploy/` is. It
>   needs a third key on the ESO-synced Secret, which means a third entry in
>   [`parameters.json`](../../../scripts/secrets/parameters.json) (4b.3).

---

## Phase 4a — Manual prerequisites

*Six one-time steps, none of them code. Do these first, in order, and the whole
of 4b runs from commits and workflow dispatches.*

**[x] 1. Confirm Phase 3 actually landed.** Not "the boxes are ticked" — dispatch
*Verify: Cluster platform* ([`verify-cluster-platform.yml`](../../../.github/workflows/verify-cluster-platform.yml))
and get a green run. Phase 4 is the first phase that creates objects the
operator will keep reconciling forever, and every one of them is downstream of
something 3b.13 asserts. Specifically confirm in that output:

- `CRD clusters.postgresql.cnpg.io` — 3b.12
- every `ExternalSecret` is `SecretSynced` — 3b.6, the machinery 4b.3 extends
- the wildcard is issued by the **production** issuer — the plugin in 4b.4
  mints its own serving certificates through cert-manager, so a cert-manager
  that is not actually healthy surfaces here as a plugin that never starts

**[x] 2. Create the S3 bucket for WAL and base backups.** A **new, dedicated
bucket** — not the restic bucket Phase 0 created. Two reasons, and the second
is the one that matters: barman-cloud owns its prefix layout (`base/`, `wals/`)
and expects to be the only writer, and Phase 8's "AWS account is gone" story is
strictly better with the two copies under separate buckets and separate
credentials. Enable **versioning** and set a lifecycle rule for
noncurrent-version expiry; leave object retention alone — barman's own
`retentionPolicy` (4b.5) is what prunes, and a bucket policy that deletes
underneath it produces a WAL gap that only shows up during a restore.

Same region as the rest (`AWS_REGION`). Cross-region here buys nothing and
costs egress on every WAL segment.

Bucket name: `landis-family-aerie-cnpg-wal`

**[x] 3. Create the `aerie-cnpg` IAM user.** Its own user, matching the isolation
discipline `aerie-restic` and `aerie-eso` already follow — the credential the
database holds forever must not be able to read the backup repository, and vice
versa. Create the user in the console (as with the others: nothing in this repo
mints a credential, because a script that did would have to print it — see
[ethos](../../ethos.md)), then apply its policy with the script 4b.2 extends.

**[x] 4. Add the repository secret and variables.** `CNPG_AWS_ACCESS_KEY_ID` and
`CNPG_AWS_REGION` as **variables**, `CNPG_AWS_SECRET_ACCESS_KEY` as a
**secret** — the split [`parameters.json`](../../../scripts/secrets/parameters.json)
records as `githubKind`, and getting it backwards resolves to an empty string
rather than erroring. The first two names are already wired into
[`provision-2-seed-secrets.yml`](../../../.github/workflows/provision-2-seed-secrets.yml#L139-L140)
and unset; the region is new (4b.3).

**[x] 5. Decide and record the PostgreSQL major version.** Read what prod runs —
`postgres:18.4` today — and pick the CNPG operand image at that major or newer.
`pg_restore` restores forwards across majors and never backwards, so this is a
floor, not a preference. Pin it explicitly in 4b.6; do not leave `imageName`
unset.

**6. Take a fresh backup and keep the old host running.** 4b.9 restores from a
Phase 0 snapshot, and 4b.1 changes what those snapshots contain — so the
restore needs a snapshot taken *after* 4b.1 lands. Nothing in Phase 4 touches
the old host's traffic: it keeps serving throughout, and Phase 7 is what moves
DNS. Do not decommission anything.

---

## Phase 4b — Scriptable, in this order

The order is not arbitrary. Steps 1–3 are repo-and-AWS work with no cluster
involvement; 4–5 put the WAL destination in place *before* the database that
writes to it, so no `Cluster` ever exists with archiving switched off; 6–8 build
the database and its schema; 9–10 load real data and prove the backup loop
closes; 11 is the gate.

- [x] **1. Per-database dumps in the Phase 0 backup** —
      [`containers/backup/scripts/backup.sh`](../../../containers/backup/scripts/backup.sh).
      Add, alongside the existing `pg_dumpall` (which stays — it is the globals
      and roles carrier and the DR artifact
      [`verify-restore.sh`](../../../containers/backup/scripts/verify-restore.sh)
      exercises):

      ```sh
      pg_dump -Fc -h db -U "$POSTGRES_USER" aerie  > "$SCRATCH/aerie.dump"
      pg_dump -Fc -h db -U "$POSTGRES_USER" quartz > "$SCRATCH/quartz.dump"
      ```

      and both files into the `restic backup` argument list.
      Custom format (`-Fc`), not plain SQL, so 4b.9 can use `pg_restore` with
      `--no-owner --no-privileges` — which is what lets the dump land on
      whatever role CNPG generated instead of requiring the old `user` role to
      exist in the new cluster. Finding 2 is that `user`/`password` stops
      existing; a plain-SQL restore would quietly recreate it.
      Leave the retention flags and `verify-restore.sh` alone. The dumpall is
      still the file that script finds, so its assertion is unchanged.
      *Exit:* one daily backup cycle completes on the old host and
      `restic -r "$RESTIC_REPOSITORY_LOCAL" ls latest` lists `aerie.dump` and
      `quartz.dump`.

- [x] **2. The `aerie-cnpg` IAM policy, committed and applied** —
      `scripts/secrets/iam/aerie-cnpg.policy.json`, plus a
      `-CnpgUserName` parameter on
      [`Set-AerieSecretsIam.ps1`](../../../scripts/secrets/Set-AerieSecretsIam.ps1).
      The policy scopes to the 4a.2 bucket and nothing else:
      `s3:PutObject`, `s3:GetObject`, `s3:DeleteObject`, `s3:ListBucket`,
      `s3:AbortMultipartUpload` on `arn:aws:s3:::<bucket>` **and**
      `arn:aws:s3:::<bucket>/*` — both ARNs, for the same reason
      [`aerie-eso.policy.json`](../../../scripts/secrets/iam/aerie-eso.policy.json)
      carries both: `ListBucket` authorizes against the bucket, every object
      action against the objects, and getting only one of them is a policy that
      reads correct and fails at the first base backup.
      `AbortMultipartUpload` is not optional — barman-cloud uploads base backups
      multipart, and without it a failed upload leaves parts that accrue storage
      charges forever and that nothing can clean up.
      Placeholder the bucket name (`<WAL_BUCKET>`) the way the existing two
      placeholder region and account, and add a `-WalBucket` parameter. Keep
      `-Render` working with no AWS CLI present: that is the path the operator
      uses when the identity that can write IAM is not the one on this machine.
      *Exit:* `.\Set-AerieSecretsIam.ps1 -CnpgUserName aerie-cnpg -WalBucket <bucket>`
      completes, and `aws s3 ls s3://<bucket>` succeeds under that user's keys
      and fails against the restic bucket.

- [x] **3. `parameters.json`: three entries, then regenerate** —
      [`scripts/secrets/parameters.json`](../../../scripts/secrets/parameters.json).
      This is the step 3b.6's machinery was built for, and doing it in the wrong
      order poisons the phase gate, so the order inside this bullet is part of
      the step:

      1. Add `postgres/wal-s3-region` — `env: CNPG_AWS_REGION`,
         `githubSource: AWS_REGION`, `githubKind: variable`, `required: true`.
         **`env` must not be `AWS_REGION`**: the file's own header says why, and
         the AWS CLI reading it is exactly the failure the Route53 pair already
         renames around. Its existence is the finding above — `s3Credentials.region`
         is a `SecretKeySelector`, so the region has to arrive as a key on a
         Secret rather than as a `${...}` substitution, even though it is not
         secret. (Precedent: an access key *id* is already carried here as a
         non-secret `variable`.)
      2. Add the matching line to
         [`provision-2-seed-secrets.yml`](../../../.github/workflows/provision-2-seed-secrets.yml):
         `CNPG_AWS_REGION: ${{ vars.AWS_REGION }}`, beside the two CNPG entries
         already there, and move all three out of the "unset until their phase
         lands" comment block.
      3. **Dispatch Provision 2 and confirm all three parameters seed.** Only
         then:
      4. Flip `postgres/wal-s3-access-key-id` and
         `postgres/wal-s3-secret-access-key` to `required: true`, and give all
         three a `kubernetes` block — namespace `aerie`, secretName
         `cnpg-wal-s3`, secretKeys `access-key-id` / `secret-access-key` /
         `region`, with a `consumedBy` naming 4b.5.
      5. `pwsh ./scripts/secrets/New-ExternalSecrets.ps1` and commit the
         generated file.

      Steps 3 and 4 are in that order because the generator **refuses** a
      `kubernetes` block on a `required: false` entry, and because an
      `ExternalSecret` pointing at an unseeded parameter sits in
      `SecretSyncError` forever, which takes `infra-config`'s Ready condition
      and the Phase 3 gate down with it. One Secret with three keys, not three
      Secrets: the generator groups by (namespace, secretName), and a credential
      pair that can half-rotate is the failure that grouping exists to prevent.
      *Exit:* `pwsh ./scripts/secrets/New-ExternalSecrets.ps1 -Check` exits 0
      (ci.yml runs this), and after the next reconciliation
      `kubectl -n aerie get externalsecret cnpg-wal-s3` reports `SecretSynced`
      with three keys in the Secret.

- [x] **4. The Barman Cloud Plugin** —
      `deploy/cluster/infrastructure/controllers/plugin-barman-cloud.yaml`, and
      one line in that directory's
      [`kustomization.yaml`](../../../deploy/cluster/infrastructure/controllers/kustomization.yaml).
      Layer 1, because it registers a CRD (`objectstores.barmancloud.cnpg.io`)
      that 4b.5 is an instance of — the same boundary
      [`infrastructure.yaml`](../../../deploy/cluster/infrastructure.yaml) draws for
      every other component.
      Chart `plugin-barman-cloud`, pinned **0.7.1** (appVersion v0.14.0), from
      the `cloudnative-pg` HelmRepository that already exists in `cnpg-system` —
      no second `HelmRepository` object, and the release goes in **`cnpg-system`
      too**: the plugin must run in the operator's own namespace, and installing
      it anywhere else produces a plugin that starts cleanly and that no
      `Cluster` can reach.
      Leave `certificate.createIssuer`, `createClientCertificate` and
      `createServerCertificate` at their defaults (`true`): the chart mints a
      self-signed `Issuer` and two `Certificate`s through cert-manager for the
      gRPC channel between operator and plugin. That is the one thing in this
      tree that genuinely depends on 3b.7 having worked, and it is why 4a.1 asks
      for a green gate rather than a glance.
      Pin both `image.tag` and `sidecarImage.tag` explicitly rather than
      inheriting the chart appVersion — the sidecar is injected into every
      Postgres pod, so an implicit tag makes the database's pod spec change
      whenever the chart does.
      `install.remediation.retries: 1` and `upgrade.remediation.cleanupOnFail: true`,
      matching the neighbouring files; there is no data behind this release, so
      the uninstall-and-retry blast radius is one Deployment.
      *Exit:* `kubectl get crd objectstores.barmancloud.cnpg.io` and
      `kubectl -n cnpg-system rollout status deployment plugin-barman-cloud`.
      — *Two traps. The chart's `values.schema.json`, like the operator's, sets
      no `additionalProperties: false`, so a misspelled key is accepted and
      silently unread. And `service.name` is documented as unchangeable because
      the certificate SANs are generated from it — leave the whole `service`
      block alone.*

- [x] **5. The `ObjectStore`** — `deploy/cluster/data/objectstore.yaml`.
      Namespace **`aerie`**, alongside the `Cluster` that references it: the
      `barmanObjectName` parameter in 4b.6 is a bare name that resolves in the
      `Cluster`'s own namespace, and the credential it names is the Secret ESO
      syncs there in 4b.3.

      ```yaml
      apiVersion: barmancloud.cnpg.io/v1
      kind: ObjectStore
      metadata:
        name: aerie-pg-wal
        namespace: aerie
      spec:
        configuration:
          destinationPath: s3://${WAL_BUCKET}/
          s3Credentials:
            accessKeyId:     { name: cnpg-wal-s3, key: access-key-id }
            secretAccessKey: { name: cnpg-wal-s3, key: secret-access-key }
            region:          { name: cnpg-wal-s3, key: region }
          wal:
            compression: gzip
          data:
            compression: gzip
          retentionPolicy: "30d"
      ```

      **`serverName` must be left empty** — it exists only for API compatibility
      with the in-tree `barmanObjectStore` and is set through the plugin
      parameters in 4b.6 instead.
      `${WAL_BUCKET}` is a new key in
      [`cluster-config.json`](../../../scripts/k3s/cluster-config.json) (see 4b.7 for the
      pattern and for the other key this phase adds) — a bucket name is
      per-installation, so [ethos](../../ethos.md) keeps it out of the tree, and
      ci.yml's "every substitution token is a declared key" check fails the
      build if it is added here and not there.
      `retentionPolicy` is barman's own pruning and is the only thing that
      deletes from this bucket; 4a.2's warning about bucket-side lifecycle rules
      is the other half of that sentence.
      *Exit:* `kubectl -n aerie get objectstore aerie-pg-wal` exists. It has no
      meaningful status of its own until a `Cluster` uses it — 4b.6's WAL
      archiving is what proves the credential.

- [x] **6. The `Cluster`** — `deploy/cluster/data/cluster.yaml`, in namespace
      `aerie`. The centre of the phase. Field by field, with the reasoning for
      the ones that are not obvious:

      - `instances: ${POSTGRES_INSTANCES}` — **2 during the build window**, a
        new `cluster-config.json` key (4b.7), exactly parallel to
        `LONGHORN_REPLICA_COUNT`. Phase 7 raises it to 3 by changing a
        repository variable and re-dispatching Provision 4, with **no commit** —
        which is the point of the ConfigMap, and which is what the Phase 7
        bullet "and CNPG to 3 instances" already anticipates. Note the
        substitution is textual and happens before YAML parsing, so
        `instances: ${POSTGRES_INSTANCES}` renders as `instances: 2` and parses
        as an integer; quoting it would produce the type error the operator's
        schema *does* catch.
      - `imageName:` pinned to an explicit `ghcr.io/cloudnative-pg/postgresql`
        tag at the 4a.5 major or newer. Not optional — see the re-scope note.
      - `storage.storageClass: local-path`, `storage.size:` sized from the
        current `pgdata` volume with room to grow. The
        [storage split](design.md#storage-split) is emphatic that Postgres does not
        go on Longhorn: replicating Postgres's own replication is wasteful and
        slow.
      - `walStorage:` — a **separate** PVC, same class. WAL and data on one
        volume means a WAL burst can fill the data disk and stop the primary; it
        also makes the two independently sizable, which matters more once
        archiving is on and a network problem backs WAL up locally.
      - `postgresql.synchronous:` — the live API, replacing the deprecated
        `minSyncReplicas` the old bullet named:

        ```yaml
        postgresql:
          synchronous:
            method: any
            number: 1
            dataDurability: preferred
        ```

        **`preferred`, not the `required` default**, and this is the single most
        consequential line in the file. `required` gives RPO=0 and pauses every
        write when the synchronous standby is unavailable; Phase 1 reboots one
        node on a schedule, so on two nodes that is a recurring, self-inflicted
        write outage that presents as a hung site. `preferred` degrades to
        asynchronous while the standby is away and re-arms when it returns.
        Worth revisiting in Phase 7: with three instances and `number: 1`, a
        single node reboot no longer costs the quorum, and `required` becomes
        affordable. Record that as the reason rather than leaving `preferred` to
        look permanent.
      - `affinity.enablePodAntiAffinity: true` with
        `affinity.podAntiAffinityType: required`. `preferred` (the default) will
        happily co-locate two instances on one node under pressure, which
        silently converts an HA database into a single point of failure — the
        failure being invisible is the argument for `required`. The original
        bullet's caveat is real but is answered by `instances: 2`: three
        instances with required anti-affinity across two nodes leaves one pod
        permanently Pending, and a Pending pod is noise that trains you to
        ignore alarms.
      - `resources.requests` **and** `limits`, on both the instances and — via
        the plugin's `instanceSidecarConfiguration` on the ObjectStore — the
        sidecar. [5a.6](phase-5-app-tier.md) measured the old host's
        Postgres at 52–70 MiB across idle and load; do **not** transplant that
        number. It is a nearly-idle database whose whole working set fits in
        cgroup page cache, and CNPG derives `shared_buffers` from the memory
        limit — so the limit is a tuning input here, not only a kill threshold.
        Live values are `512Mi`/`1Gi` per instance
        ([cluster.yaml](../../../deploy/cluster/data/cluster/cluster.yaml#L78-L84))
        and `128Mi`/`512Mi` for the sidecar
        ([objectstore.yaml](../../../deploy/cluster/data/cluster/objectstore.yaml#L65-L72)),
        the latter raised from 128Mi after `barman-cloud-backup` was OOMKilled
        mid-upload on the first real base backup — the sidecar buffers a
        multipart upload in memory, so it is sized by backup size and not by
        WAL segment size.

        **The instances carry a `cpu: "2"` limit, which 5a.6's rule says they
        should not.** A CPU limit throttles at the CFS quota period, and the
        workload most likely to hit it is the base backup that already proved
        it can exhaust its budget. Left as-is for now because it has not been
        observed throttling; revisit it with the same reading Phase 6 gives
        every other workload, and delete it rather than raise it. [Goal 2 does not work without requests](design.md#how-goal-2-actually-works):
        the scheduler cannot place or rebalance what it cannot size. Phase 5's
        "requests and limits on every workload" bullet does not cover this
        workload, because this workload is created here.
      - `bootstrap.initdb.database: aerie`, `owner: aerie`. CNPG generates the
        password and publishes it as the `aerie-pg-app` Secret — which is what
        Phase 5's connection strings read, and what finally kills
        [appsettings.Docker.json:10-11](../../../src/Aerie.Api/appsettings.Docker.json#L10-L11)'s
        `user`/`password` (Finding 2).
      - `plugins:` — the WAL archiver:

        ```yaml
        plugins:
          - name: barman-cloud.cloudnative-pg.io
            isWALArchiver: true
            parameters:
              barmanObjectName: aerie-pg-wal
        ```

        This one block enables both WAL archiving and base backups. Archiving
        starts on the primary immediately, which is why 4b.5 comes first: a
        `Cluster` that exists before its destination does spends its first
        minutes failing to archive, and `pg_wal` grows while it does.
      - `primaryUpdateStrategy: unsupervised` — let the operator do the
        switchover on an image or config change rather than waiting for a human
        `kubectl cnpg promote`. With `preferred` durability and a healthy
        standby, a supervised strategy mostly means a rollout that silently
        never finishes.
      - `enableSuperuserAccess:` left at its default (`false`). 4b.8 and 4b.9
        are both designed to run as the `aerie` owner role; needing a superuser
        would be a sign one of them is doing something it should not.

      *Exit:* `kubectl -n aerie get cluster aerie-pg` reports as many ready
      instances as `${POSTGRES_INSTANCES}`, the pods sit on different nodes, and
      `kubectl cnpg status aerie-pg` shows continuous archiving with a
      successful last WAL. That last clause is the one that proves 4b.2, 4b.3,
      4b.4 and 4b.5 all worked, and it is the only thing that does.

- [x] **7. Two new `cluster-config.json` keys, and a Provision 4 re-dispatch** —
      [`scripts/k3s/cluster-config.json`](../../../scripts/k3s/cluster-config.json).
      `POSTGRES_INSTANCES` (pattern `^[1-3]$`, hint: never more than the number
      of nodes; `consumedBy` 4b.6) and `WAL_BUCKET` (an S3 bucket-name pattern —
      lowercase, 3–63 characters, no underscores, no consecutive dots;
      `consumedBy` 4b.5). Add the matching `vars.*` lines to
      [`provision-4-cluster-config.yml`](../../../.github/workflows/provision-4-cluster-config.yml),
      set both repository variables, and **dispatch Provision 4 before the 4b.5
      and 4b.6 commits reach the cluster**.
      Order matters and the failure is silent: Flux expands an undefined token
      to the **empty string**, so a `Cluster` committed ahead of the ConfigMap
      is not a failed reconciliation — it is `instances:` with no value and
      `destinationPath: s3:///`. ci.yml catches the *spelling* of a token
      against this file; nothing catches a correctly-spelled key the cluster has
      not been given yet.
      This is listed seventh because it reads as repo work, but its dispatch
      belongs between the commits — say so in the commit message.
      *Exit:* `kubectl -n flux-system get configmap aerie-cluster-config -o yaml`
      shows both keys with values.

- [x] **8. `quartz`: the `Database` CRD, then the DDL Job** —
      `deploy/cluster/data/quartz-database.yaml` and
      `deploy/cluster/data/quartz-ddl-job.yaml`.

      The database is declarative and needs no Job:

      ```yaml
      apiVersion: postgresql.cnpg.io/v1
      kind: Database
      metadata:
        name: aerie-pg-quartz
        namespace: aerie
      spec:
        cluster: { name: aerie-pg }
        name: quartz
        owner: aerie
        ensure: present
        databaseReclaimPolicy: retain
      ```

      `retain` (the default, written out) because `delete` would let a mistaken
      commit and `prune: true` drop the Quartz store; `spec.cluster` is
      immutable, so getting the name wrong means deleting and recreating the
      object.

      The DDL is the part Finding 3 is about. Three things, in order:

      1. **Split the file.** [`containers/aerie-db/pginit.sql`](../../../containers/aerie-db/pginit.sql)
         is two `CREATE DATABASE` statements, a `\c quartz` psql meta-command,
         and ~190 lines of Quartz DDL. CNPG executes SQL, not psql scripts. Lift
         the DDL alone into a new file — `deploy/cluster/data/quartz-ddl.sql`,
         loaded as a ConfigMap via `configMapGenerator` — and leave
         `pginit.sql` in place: the old host is still serving from it until
         Phase 7, and it is deleted with `compose.prod.yml`, not here.
      2. **Add `IF NOT EXISTS` to every `CREATE TABLE` and `CREATE INDEX`.** Do
         it in **both** copies, so a rebuild of the old host during the
         transition behaves the same. This is what makes the Job idempotent, and
         idempotence is not a nicety here — it is what lets the same Job serve
         the fresh-install path and be a harmless no-op after 4b.9's restore has
         already brought the tables in. One ordering, two install paths.
      3. **The Job** — image the same `ghcr.io/cloudnative-pg/postgresql` tag as
         4b.6 (so `psql`'s version matches the server), `psql -v ON_ERROR_STOP=1
         -f /ddl/quartz-ddl.sql`, credentials from the `aerie-pg-app` Secret
         with `PGDATABASE=quartz`, `backoffLimit: 6`, and
         `ttlSecondsAfterFinished` set so completed Jobs do not accumulate.
         `ON_ERROR_STOP=1` is deliberate: with the guards in place there is no
         error this Job should survive, and "ignore errors" is a Job that cannot
         be gated on.

      *Exit:* `kubectl -n aerie get database aerie-pg-quartz` reports ready, the
      Job reports `Complete`, and
      `psql -d quartz -c "\dt"` lists eleven `qrtz_*` tables.
      — *A note for whoever revisits [Finding 3's simplification](design.md#3-pginitsql-doesnt-survive-cnpg-as-is):
      folding the `qrtz_*` tables into the `aerie` database under a table prefix
      would delete this entire step and the `Database` CRD with it. It is a
      behaviour change and a data migration, so it is not the default — but it
      is a smaller change now than it will be after Phase 5 ships connection
      strings for two databases.*

- [x] **9. The restore, as a re-runnable Job** —
      `deploy/cluster/data/restore-job.yaml`, **suspended by default**.
      This is the step the phase exists to make safe, and it is built to run
      more than once: Phase 7 needs a final sync before the old prod box is
      wiped and rebuilt as node 3, and nothing in Phase 7 currently says so.
      Write it as a Job that is dispatched deliberately rather than one that
      reconciles — `suspend: true` in the committed manifest, run with
      `kubectl -n aerie create job --from=job/aerie-pg-restore <name>` — so that
      a Flux reconciliation can never re-run a data load by itself.

      What it does, in order:

      1. `restic restore latest --target /restore` from the **local** repo
         (S3 is the fallback; the local repo is faster and is what
         `verify-restore.sh` already reads), with the restic credentials from
         the `backup/*` parameters. Those have no `ExternalSecret` yet —
         [`parameters.json`](../../../scripts/secrets/parameters.json) defers them to
         Phase 8 with a `kubernetesDeferred` note. **Do not flip them here.**
         Give this Job the values through a Secret created for it, or run the
         restic half outside the cluster and hand the Job a dump on a PVC. The
         deferral is deliberate: Phase 8 owns the namespace those land in.
      2. `pg_restore --no-owner --no-privileges -d aerie /restore/aerie.dump`
         and the same for `quartz`, as the `aerie` role from the
         `aerie-pg-app` Secret. `--no-owner` is what lets the dump land without
         the old `user` role existing — the point of Finding 2.
      3. A sanity query, in the Job itself: row counts on two known tables plus
         `SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('public','storage')`.
         An unverified restore is not a restore, and the `storage` schema is in
         that list because [the module contexts](../../../src/Aerie.Api/Modules/README.md)
         put tables outside `public` — a check that only counts `public` (as
         [`verify-restore.sh`](../../../containers/backup/scripts/verify-restore.sh) does
         today) would pass against a half-restored database.

      Run it against the 4a.6 snapshot, then validate against real data: query
      recent channel history and confirm the row counts and the newest
      timestamps match what the old host reports for the same query. That
      comparison — not the Job's exit code — is this step's actual deliverable.
      *Exit:* the Job reports `Complete`, its sanity query passes, and a
      hand-run comparison against the old host matches.
      — *Add a Phase 7 bullet while this is fresh: "re-run the restore Job
      against a final dump taken with the old host's API stopped, before
      rebuilding it as node 3." The dump is only consistent if nothing is
      writing, which means a short deliberate outage — and that is the cutover,
      so it belongs in the cutover phase.*

- [x] **10. `ScheduledBackup`, and prove PITR** —
      `deploy/cluster/data/scheduledbackup.yaml`:

      ```yaml
      apiVersion: postgresql.cnpg.io/v1
      kind: ScheduledBackup
      spec:
        cluster: { name: aerie-pg }
        method: plugin
        pluginConfiguration: { name: barman-cloud.cloudnative-pg.io }
        schedule: "0 0 2 * * *"
        backupOwnerReference: self
        immediate: true
      ```

      Note the **six-field** cron — CNPG's schedule includes seconds, and a
      five-field expression is accepted and fires an hour off. `immediate: true`
      so the first base backup is taken on apply rather than at 2am, which is
      what makes this step verifiable in the same sitting.
      Then the part that is not a manifest: **prove point-in-time recovery
      actually works.** Note a timestamp, make a marker change in the restored
      data, then bootstrap a *second, throwaway* `Cluster` with
      `bootstrap.recovery` and an `externalClusters` entry pointing at
      `aerie-pg-wal` with `serverName: aerie-pg` and a
      `recoveryTarget.targetTime` before the marker. Confirm the marker is
      absent, then delete the throwaway cluster. Recovery is never in-place — it
      always bootstraps a new cluster — which is exactly why this rehearsal is
      cheap and why not having done it means PITR is a claim rather than a
      capability.
      *Exit:* `kubectl -n aerie get backup` shows a `completed` base backup, the
      bucket holds `base/` and `wals/` prefixes, and a PITR clone came up at a
      target time and was verified to be missing the marker.

- [x] **11. Phase gate as a command** — `scripts/k3s/Test-DataTier.ps1`,
      wrapped by `.github/workflows/verify-data-tier.yml`, in the exact shape
      3b.13 established: read-only, **not** numbered into the Provision
      sequence, does not stop at the first failure, and a check it cannot
      evaluate is a failure rather than a skip.
      Take its expectations from the cluster and the committed maps, not from
      parameters — `POSTGRES_INSTANCES` from `aerie-cluster-config`, the
      expected `ExternalSecret` set from
      [`parameters.json`](../../../scripts/secrets/parameters.json) — so it keeps
      checking the right things after a variable moves.
      Assert, at minimum: the `Cluster` is ready with the configured instance
      count; **the instance pods are on distinct nodes** (a `required`
      anti-affinity rule that silently did not apply looks identical to one that
      did until a node dies); `synchronous.dataDurability` is what the manifest
      says; continuous archiving is working and the last archived WAL is recent;
      the newest `Backup` is `completed` and **younger than the schedule
      interval** (backup age is the alert Phase 8 calls the most valuable one
      that does not exist — this is its first, manual form); the `quartz`
      `Database` is ready and its eleven tables exist; the plugin Deployment is
      rolled out and both of its certificates are Ready; and `cnpg-wal-s3` is
      `SecretSynced` with three keys.
      Run the [portability check](design.md#verification) over the new tree before
      ticking: grep `deploy/cluster/data/` for the bucket name, the domain, and
      any address — each should appear only as a `${...}` substitution.
      *Exit:* the workflow exits 0. Leave this box unticked until it has,
      for the same reason 3b.13 stayed unticked: a gate that has never passed
      has proved nothing.

---

## Where these files live

Phase 4's manifests are **not** `infrastructure/`. They are instances of a
database, in the application's namespace, and they need to be ordered against
each other in a way one Kustomization cannot express — so they get their own
layer, in the pattern
[`infrastructure.yaml`](../../../deploy/cluster/infrastructure.yaml) already
established:

```text
deploy/cluster/
  kustomization.yaml        # + data.yaml
  infrastructure.yaml       # infra-controllers → infra-config   (Phase 3)
  data.yaml                 # data-cluster → data-schema         (Phase 4)
  data/
    cluster/                 # data-cluster's own path, wait: true
      objectstore.yaml        # 4b.5
      cluster.yaml             # 4b.6
      kustomization.yaml
    schema/                  # data-schema's own path, dependsOn: data-cluster
      quartz-database.yaml    # 4b.8
      quartz-ddl-job.yaml     # 4b.8
      quartz-ddl.sql          # 4b.8, ConfigMap-generated
      restore.sh               # 4b.9, ConfigMap-generated
      restore-job.yaml        # 4b.9, suspended
      scheduledbackup.yaml    # 4b.10
      kustomization.yaml
```

Two directories, not one flat `data/` — this is a deliberate departure from
this section's original single-directory sketch, made while actually wiring
`data.yaml`'s two Flux `Kustomization`s. `dependsOn` and `wait` are properties
of a *Kustomization*, and a Kustomization's contents are exactly what
`kubectl kustomize <path>` builds from its one `path` — there is no mechanism
to point two Kustomizations at the same path and have each apply a different
subset. So the split promised below needs two `path`s to be real rather than
aspirational.

`data-cluster` carries `dependsOn: infra-config` and `wait: true`, path
`./deploy/cluster/data/cluster`; `data-schema` carries `dependsOn:
data-cluster`, path `./deploy/cluster/data/schema`. The split is not
decoration — a Job applied in the same pass as the `Cluster` starts against a
database that does not answer yet, and burns its `backoffLimit` before the
primary is up. `wait: true` makes a Kustomization wait for its objects to be
*healthy*, but it does not order objects *within* one Kustomization's single
apply pass, which is the same distinction `infra-controllers`/`infra-config`
exists to draw — and the reason `objectstore.yaml` is still listed ahead of
`cluster.yaml` in `data/cluster/kustomization.yaml`, even though both land in
the one data-cluster pass: it is not a hard guarantee, but CNPG's WAL archiver
retries continuously regardless, so the cost of the wrong order is a few
extra archive attempts, not a stuck `Cluster`.

`quartz-ddl.sql` and `restore.sh` are generated into ConfigMaps by
`data/schema/kustomization.yaml`'s `configMapGenerator`, with
`disableNameSuffixHash: true` — the two Jobs that mount them have immutable
pod templates once created, so the usual content-hash suffix would mean
kustomize rewriting a Job's volume reference on an object it can no longer
apply over. A script edit is picked up by deleting the completed/suspended Job
and letting Flux recreate it against the (already updated) ConfigMap.

Both Kustomizations need `postBuild.substituteFrom` the `aerie-cluster-config`
ConfigMap — that is the only way `${POSTGRES_INSTANCES}` and `${WAL_BUCKET}`
resolve, and `optional` stays at its default of `false` so a missing ConfigMap
fails loudly instead of substituting empty strings into a database spec.

**One thing to verify early:** Flux's `wait` uses kstatus, which reads
`status.conditions` for a `Ready` condition on custom resources. CNPG's
`Cluster` publishes one, so this should work — but confirm it on the first
reconciliation rather than assuming, and fall back to an explicit `healthChecks`
entry naming the `Cluster` if `data-cluster` goes Ready too early.

## What Phase 4 deliberately does not do

- **No app changes.** Finding 1 (migrations into a Helm hook Job) and the
  connection strings that read `aerie-pg-app` are Phase 5. Nothing in this phase
  touches [Program.cs](../../../src/Aerie.Api/Program.cs) or
  [appsettings.Docker.json](../../../src/Aerie.Api/appsettings.Docker.json), and the old
  host keeps serving from its own Postgres throughout.
- **No `backup/*` `ExternalSecret`.** Deferred to Phase 8 by an explicit
  `kubernetesDeferred` note; 4b.9 works around it rather than pre-empting it.
- **No Longhorn anywhere near this.** See the
  [storage split](design.md#storage-split).
- **No `PodMonitor`.** `monitoring.podMonitorEnabled` stays `false` until Phase 6
  registers the CRD; the ~480 lines of Postgres exporter queries the operator
  already ships are waiting there for it.

## Additions this phase makes to other phases

Two gaps found while digesting this one. Record them where they belong rather
than here:

- **Phase 7 has no data cutover.** Add: stop the old host's API, take a final
  backup, re-run 4b.9's restore Job against it, *then* rebuild the box as the
  third k3s server. The current bullet list rebuilds the machine holding the
  live database without ever moving its contents.
- **Phase 7's replica-count bullet should name `POSTGRES_INSTANCES`
  explicitly**, beside `LONGHORN_REPLICA_COUNT` — both are repository variables
  and a Provision 4 re-dispatch, neither is a commit — and should add
  "reconsider `dataDurability: required` now that three instances make a single
  reboot survivable."
