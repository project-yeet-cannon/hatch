# Photos — Immich on Aerie

**Status:** Not started. Five phases to MVP, each independently useful and
independently revertible; two scaffolded follow-ons (`v+1` sharing, `v+2` kiosk)
that are deliberately *not* built yet but are named here so the MVP does not
close their doors; and one long-horizon pathway (the photography workflow) that
gets no code at all and one design constraint.

The MVP is: **every family photo in one place, on our own hardware, reachable
only from the house LAN and the tailnet, with an offsite copy that survives the
house burning down — and three cloud subscriptions cancelled.**

## The brief, as decisions

Answers given at planning time, so a later reader knows which of these are
settled and which are still open:

| Question | Answer |
|---|---|
| Library size, all sources, pre-dedup | **Under 1 TB** |
| Where the bytes live | **A new dedicated disk on one node** |
| Offsite | **S3 Glacier Deep Archive** for originals; DB and config alongside |
| Ingest tooling | **`immich-go` plus a thin first-party wrapper** |
| Exposure | **Tailnet + LAN only.** No public ingress in the MVP |
| Sharing with extended family | **`v+1`** — scaffolded below, not built |
| Kiosk photo frame | **`v+2`** — scaffolded below, not built |
| DSLR / RAW workflow | **Not solutioned.** One constraint on this plan, no more |

## Findings

Eight, from reading the tree. The first three decide the architecture; the rest
decide the backup and the ingest.

### 1. Immich is the first app that costs infrastructure, and that is not a seam failure

[`family-apps-architecture.md`](../family-apps-architecture.md) makes a strong
claim — app #2 costs a folder and an afternoon, no container, no database, no
ingress, no backup entry. Immich does not get that property and cannot be made
to: it is a 4-container upstream product with its own schema, its own migrations,
its own release cadence, and a machine-learning model server. It is not a module
in `Aerie.Api`, and trying to make it one would be the worst possible reading of
that doc.

State it plainly here so nobody has to relitigate it later: **the zero-infra
property is a property of the family-apps suite, not of Aerie.** Immich is a
platform tenant like CloudNativePG or Longhorn is — it gets a namespace, a Flux
Kustomization, a storage class, an ingress, a backup path, and an uptime monitor,
and the thing to hold it to is that it inherits every one of those *shapes* from
what already exists rather than inventing new ones.

The check on this phase is not "did app #N stay cheap". It is: **did anything on
[what must stay untouched](../family-apps-architecture.md#what-must-stay-untouched)
get edited?** It should not. Immich touches none of it.

### 2. The library does not fit on Longhorn, and fixing that with replicas would be the wrong fix

Each node's Longhorn data disk is **200 GB**
([`Initialize-NodeStorage.ps1`](../../scripts/k3s/Initialize-NodeStorage.ps1),
`-DataDiskSizeGB` default 200), and
[`longhorn-storageclasses.yaml`](../../deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml)
splits that capacity into `longhorn-r3` for tiny critical volumes and
`longhorn-r2` for bulk-where-loss-is-tolerable. A ~1 TB photo library fits
neither class and would not fit the disks even at one replica.

The tempting fix — bigger data disks, `longhorn-r2`, done — costs **2× the raw
capacity** to protect data whose actual protection is the offsite copy. A photo
library is not Prometheus: losing a replica does not mean losing reconstructible
data, it means the restore is the offsite one. Paying twice for local redundancy
on the largest volume in the house, on top of an offsite copy, is the expensive
half of a belt-and-braces that the braces alone already cover.

So: **a second, dedicated disk on one node, one Longhorn replica, disk-tagged so
nothing else can land on it.** The mechanism that makes this clean rather than
grubby is `dataLocality: strict-local`, which Longhorn 1.11.3 supports and which
does exactly the thing this design needs: with one replica, strict-local ties the
volume to the node holding that replica and forces the consumer pod there
*through the volume*, rather than through a node name written into a manifest.

That last part is the ethos point, and it is worth being explicit about.
[`ethos.md`](../ethos.md) forbids "this installation's node is called `k3s-2`"
from reaching the repo. Node affinity written by hand would do exactly that, and
would need a `PHOTOS_NODE` variable that has to agree with a disk that was
physically attached somewhere. With strict-local there is no such variable: the
disk tag is structural, the tagged disk exists on exactly one node because that
is where the operator attached it, and Kubernetes derives the placement.

### 3. Immich needs a Postgres that `aerie-pg` cannot be — but it needs no custom image

Immich requires the **VectorChord** extension (`vchord`), plus `vector`, `cube`
and `earthdistance`. `vchord` must be in `shared_preload_libraries`, which is a
**cluster-wide** postgres setting, not a per-database one. On top of that, Immich
owns its own schema and runs its own migrations on every release, so its database
lifecycle is coupled to the app's version rather than to Aerie's.

Three reasons, any one of them sufficient: **Immich gets its own CNPG `Cluster`,
in its own namespace.** Not a database inside `aerie-pg`, and not — importantly —
its own bespoke Postgres deployment either. It inherits the operator, the
`ObjectStore`/barman-cloud plugin shape from
[4b.5](swarm/phase-4-data-tier.md), the WAL bucket, the ServiceMonitor, and the
`local-path`-not-Longhorn rule from [the storage split](swarm/design.md#storage-split).

The historically ugly part of this — Immich on CNPG used to require a
community-built operand image carrying the vector extension, and the
pgvecto.rs→VectorChord migration made that worse — **is gone, and the reason it
is gone is worth knowing.** PostgreSQL 18 added the `extension_control_path` GUC,
and CloudNativePG 1.27+ builds on it with declarative `spec.postgresql.extensions`
that mount an extension as a **Kubernetes image volume** onto a stock operand
image. Immich's own chart repo ships exactly this shape, and it is what this plan
adopts:

```yaml
imageName: ghcr.io/cloudnative-pg/postgresql:18-standard-trixie
postgresql:
  shared_preload_libraries: ["vchord.so"]
  extensions:
    - name: vchord
      image:
        reference: ghcr.io/tensorchord/vchord-scratch:pg18-v1.1.1
      dynamic_library_path: ["/usr/lib/postgresql/18/lib"]
      extension_control_path: ["/usr/share/postgresql/18/"]
```

That keeps us on a **first-party CNPG operand image**, patched by the same
project that patches the one `aerie-pg` already runs
([`cluster.yaml`](../../deploy/cluster/data/cluster/cluster.yaml) pins
`ghcr.io/cloudnative-pg/postgresql:18.4`), with a third-party image carrying
nothing but the extension's `.so`. The alternative — a community image that
*is* the whole database — puts a lightly-maintained fork on the critical path of
every security patch. Do not take it.

Two prerequisites gate this, and 2.1 below checks both: CNPG operator **≥ 1.27**
(chart 0.29.0 is well past it, but verify the CRD actually has the field), and
the Kubernetes **`ImageVolume`** feature available in the cluster (beta and
on-by-default from 1.33; the pin is `v1.35.7+k3s1`, so this should be free —
verify rather than assume).

### 4. Derivatives are 10–20% of the library, and they are the part not worth a byte of offsite

Immich's upload root holds `library/` and `upload/` (originals — immutable once
written), `profile/`, `backups/` (Immich's own logical DB dumps), and
`thumbs/` + `encoded-video/` (**derived**, regenerable from the originals by a
job Immich already ships).

This one fact drives the entire backup design:

- **Originals are immutable.** They are written once and never modified. That
  makes a plain incremental copy correct — there is no such thing as a torn
  read of a file nothing rewrites — and it makes an archive tier viable, since
  nothing ever needs to be read back except in a disaster.
- **Derivatives are regenerable.** Backing up `thumbs/` and `encoded-video/`
  would add 10–20% to every byte of storage and every transfer, forever, to
  protect data a job rebuilds for free.
- **The database is small and is the part that actually needs point-in-time
  recovery** — albums, faces, people, sharing, edit history. It gets both WAL
  archiving (continuous, CNPG) *and* a nightly logical dump (portable, and the
  copy that goes to the archive tier next to the photos it describes).

### 5. This is the one backup in the house that should not be restic — say so before someone "fixes" it

[Phase 8](swarm/phase-8-backup-v2.md) makes restic the cluster's backup tool, and
a plan that quietly does something else invites a future cleanup commit that
breaks it. So, on the record:

restic is a **content-addressed repository** — it packs files into blobs, and
reading anything back (including `prune` and `check`) requires reading pack files
and indexes. Glacier Deep Archive objects **cannot be read without a restore
first**, which makes an unattended restic repo on Deep Archive either broken or
degraded into a write-only pile nobody can verify. Two further mismatches:
Immich's originals are already deduplicated content (restic's best feature buys
nothing here), and re-packing 250,000 photos into restic blobs makes "restore the
one album we lost" a repository operation instead of a file copy.

**`rclone copy` of the tree, preserving Immich's own layout, straight to Deep
Archive.** A restore is `rclone copy` in the other direction — or, in the small
case, three clicks in the S3 console. The bytes offsite are *photos*, in a
directory structure a human can read, which is the property you want most of the
copy you will only ever touch on the worst day.

The DB dump and config are small and hot-ish, and go to the same bucket at
Standard-IA rather than Deep Archive, so a database restore never waits 12 hours.

**`copy`, not `sync`.** `sync` propagates deletions, which means a mistake — or
malware, or a bad ingest run — replicates itself offsite within the day.
Never-delete costs ~$1/TB/month for photos that no longer exist locally, which at
this library size is single-digit dollars a year for a ransomware-proof archive.
5.4 adds a deliberate, human-triggered reconcile for the rare case where deleting
offsite is actually wanted.

### 6. Immich dedups exact bytes; the sources will not hand us exact bytes

Immich computes a checksum on upload and refuses a duplicate, so re-uploading the
same file is free and idempotent — **the same drive can be ingested twice with no
harm**, which is what makes the whole ingest safe to retry.

What it will not catch: Google Photos and iCloud hand back **re-encoded or
metadata-stripped** copies of the same photograph. Same image, different bytes,
different checksum, two assets. That is the actual duplicate problem, and there
are exactly two levers on it:

1. **Ingest order decides which copy wins.** Whichever source is imported first
   becomes the asset; later exact-duplicates are rejected and later near-
   duplicates land beside it. So import in **descending fidelity**: local and
   NAS drives (original camera files) → iCloud (originals if downloaded as such)
   → Dropbox → Google Photos (most likely re-encoded, and the richest sidecar
   metadata, which `immich-go` merges anyway).
2. **Immich's own duplicate detection** runs over CLIP embeddings and surfaces
   near-duplicate *clusters* in the UI for review, keeping one and trashing the
   rest. That is a human review step, and it is the correct place for the
   judgment call — no automated near-dupe deletion touches a family photo
   archive in this plan.

The wrapper's job is therefore narrower and more useful than "dedup": eliminate
**exact** duplicates across sources *before* upload (so we do not push hundreds
of GB of known-identical bytes at a server that will only reject them), enforce
the fidelity order, tag each import with its provenance so a bad run can be
rolled back, and write a manifest that says what came from where.

### 7. The wall does not go in front of Immich

[`auth-architecture.md`](../auth-architecture.md)'s `AUTH_MODE=full` puts a
Traefik forwardAuth in front of `home` and `kiosk`. Putting it in front of Immich
would break the thing most likely to make the family actually use it: **the
Immich mobile apps authenticate with a bearer token**, and a forwardAuth that 302s
an unenrolled client into a browser sign-in shell gives the app an HTML page where
it expected JSON. Automatic phone backup — the feature that stops the next three
years of photos from re-scattering across three clouds — dies quietly.

Immich has its own accounts, sessions and API keys. The tailnet/LAN boundary is
the outer wall, which is exactly what the brief asked for. `v+1` is where a
public boundary gets designed, and it should be designed for Immich's *own*
sharing model rather than by wrapping the whole app in ours.

### 8. The ingest runs where the archives land, not in the cluster

A Google Takeout for one account arrives as a pile of 50 GB zips, on whatever
machine downloaded them; iCloud and Dropbox exports are the same shape; the
"hard drives on Aerie servers" and "network accessible hard drives" sources are
physically attached to Windows hosts. All of it needs hundreds of GB of scratch
space and a human unzipping things.

Building that as a cluster Job would mean shipping every byte into the cluster
*twice* — once to a staging volume, once into Immich. The wrapper runs on the
machine holding the archives and talks to the Immich API over the tailnet. It is
still committed, parameterized tooling per [`ethos.md`](../ethos.md) — a script
with named parameters and a manifest, not a shell history — it just does not run
in a pod.

## Decisions

| Question | Decision | Because |
|---|---|---|
| Deployment shape | Upstream **Immich Helm chart** (OCI), pinned | Finding 1 — it is a platform tenant, and vendoring a 4-component upstream product into `charts/aerie` buys nothing |
| Namespace / layer | New `immich` namespace, new Flux Kustomization `photos` | Its failures must not gate the app tier's `wait: true` |
| Layer name | `photos`, not `immich` | The capability outlives the vendor; `v+1`/`v+2` are photo-domain, not Immich-domain |
| Database | **Own CNPG `Cluster`**, stock operand + `vchord` image volume | Finding 3 |
| DB storage | `local-path`, anti-affinity | [Storage split](swarm/design.md#storage-split) — unchanged, and Immich's docs say the same thing louder |
| Library storage | New disk, Longhorn **`longhorn-bulk`** class: 1 replica, `strict-local`, `diskSelector: bulk` | Finding 2 |
| Offsite | **`rclone copy` → S3 Glacier Deep Archive**, originals only | Findings 4 and 5 |
| DB offsite | CNPG WAL → existing bucket **and** nightly logical dump → archive bucket (Standard-IA) | Finding 4 |
| Exposure | `photos.${DOMAIN}`, wildcard TLS, no forwardAuth | Finding 7 |
| ML | **Enabled**, CPU, model cache on a PVC | Faces and search are most of Immich's value; a GPU is not available and CPU is adequate at this library size |
| Valkey | Chart's own, **persistent** PVC not `emptyDir` | The job queue survives a restart; a lost queue mid-import is a re-run of an ingest, not a data loss, but there is no reason to accept it for 1 Gi |
| Ingest | `immich-go` + `scripts/photos/` wrapper, run off-cluster | Findings 6 and 8 |
| HA | **None.** One node holds the library; one Postgres instance is acceptable | The recovery story is the offsite copy, and no family photo has an SLA |

## Cost

The brief asked for offsite cost over time. Numbers are **AWS `us-east-1` list,
August 2026**, and B2 for comparison; assume **800 GB after dedup, ~250,000
objects, growing 75 GB/year**.

| Option | $/TB/mo | Year 1 | Year 5 | 10-year total | Full-restore cost / time |
|---|---:|---:|---:|---:|---|
| **S3 Glacier Deep Archive** | $1.01 | **$12** | $14 | **~$155** | ~$65, 48h bulk (or ~$79, 12h) |
| S3 Glacier Instant Retrieval | $4.00 | $40 | $53 | ~$550 | ~$87, minutes |
| Backblaze B2 | $6.00 | $58 | $79 | ~$820 | $0 egress, hours |
| S3 Standard | $23.00 | $221 | $304 | ~$3,150 | ~$63 egress, minutes |
| Rotating encrypted drives | — | ~$160 capital | +$160 at yr 5 | ~$320 | $0, however long the drive takes to fetch |

Deep Archive footnotes that the headline rate hides, all of them small here but
all of them real:

- **Per-object costs matter more than per-byte at this object count.** Uploads
  are `$0.05` per 1,000 PUTs — the initial 250,000-object push is a **one-time
  ~$13**, and each object carries a 32 KB metadata surcharge billed at Standard
  (~$2/year for the whole archive).
- **180-day minimum storage duration.** Deleting or re-tiering an object sooner
  bills the remainder. Irrelevant for immutable originals; relevant if anyone
  later wants a `sync` that deletes.
- **Egress, not retrieval, is what a restore costs.** Bulk retrieval of 800 GB is
  ~$2; pulling it out of AWS is ~$63. A restore *into an EC2 instance* in the
  same region costs the $2 and nothing else — worth knowing on the bad day.
- The row that competes is **B2**, and it competes on a different axis: a second
  vendor, so "the AWS account is gone" does not take the copy with it, and free
  egress so restores are not a budget decision. If a third copy ever gets added,
  B2 is the one to add — 4.7 leaves the hook.

**What it replaces.** Google One 2 TB, iCloud+ 2 TB (×2 accounts each), and
Dropbox Plus run **$30–45/month, $360–540/year**, rising annually. The bulk disk
is a one-time ~$70–90; the archive is ~$12/year. **Payback is under three
months**, and the ten-year comparison is ~$475 of hardware and storage against
~$4,000+ of subscriptions.

Do not cancel anything until Phase 5's gate passes.

## Target architecture

```text
                          photos.${DOMAIN}  (wildcard TLS, no forwardAuth)
                                   |
                              Traefik ingress
                                   |
  namespace: immich  ──────────────┴──────────────────────────────────┐
                                                                      │
   immich-server ──── valkey (1Gi PVC, longhorn-r2)                   │
        │        └─── immich-machine-learning (model cache, 10Gi)     │
        │                                                             │
        ├── PVC immich-library ── StorageClass longhorn-bulk          │
        │      1 replica · strict-local · diskSelector: bulk          │
        │      → the dedicated disk on one node                       │
        │        library/ upload/ profile/ backups/   ← backed up     │
        │        thumbs/ encoded-video/               ← regenerable   │
        │                                                             │
        └── CNPG Cluster immich-pg (local-path)                       │
               operand: cloudnative-pg/postgresql:18-standard         │
               extension image volume: vchord                         │
               ObjectStore → s3://${WAL_BUCKET}/  (existing bucket)   │
                                                                      │
   CronJob photos-archive  ── podAffinity → immich-server's node ─────┘
        rclone copy → s3://${PHOTOS_ARCHIVE_BUCKET}/
              library/ upload/ profile/   → DEEP_ARCHIVE
              backups/ (nightly pg dump)  → STANDARD_IA

  off-cluster:  scripts/photos/Import-PhotoSource.ps1 ── immich-go ──> API
```

---

## Phase 1 — the bulk disk

**Goal:** a PVC of the right size, on a disk nothing else can touch, on one
node, with no node name anywhere in the repo.

**Gate:** a test pod writes and reads a file on the PVC, and `kubectl get volume
-n longhorn-system` shows one replica, on the tagged disk.

**Status:** 1.2–1.6 are written and in the tree. 1.1 is the operator's, 1.7
belongs to Phase 3, and 1.8 cannot run until 1.1 has.

### What the hardware actually turned out to be

The plan above assumed one 2 TB disk. No host in this cluster has 2 TB of
contiguous free space — the largest free chunk anywhere is about 1.5 TB, and it
shares its physical drive with that node's 200 GB Longhorn data disk. Two things
change because of it, and neither is a compromise worth apologising for.

**The disk is 1200 GiB, dynamic, not 2 TB fixed.** Sized *under* the free space
rather than at it: a dynamic VHDX that grows to its maximum must not be able to
fill the volume it shares with a node's Longhorn disk. Which host holds it is in
the operator's runbook, not here.

**The filesystem is on LVM.** Its sibling's 200 GB disk is a fixed VHDX that
will never change size, so `Initialize-NodeStorage.ps1` formats the bare device
and is right to. This one is sized against whatever free space a particular host
happened to have, which makes "grow it later" a certainty rather than a
possibility — and growing it must not mean copying a terabyte of replica data.
With `vg_bulk` holding one physical volume today, the second chunk of free space
on the same host is one `vgextend`, `lvextend`, `resize2fs` away, online, with
the volume never detaching. One indirection, bought once, for a growth path that
never takes the library offline.

### The ceiling is 75% of the disk, not 100%, and that is worth knowing now

Longhorn's `storage-minimal-available-percentage` is **25** on this cluster
(verified, alongside `storage-over-provisioning-percentage` at 100). A disk
whose available space falls below a quarter of its capacity stops being
schedulable. On a shared disk that guard is doing real work; on a dedicated,
tagged, single-volume disk it strands a quarter of the capacity — and it is a
*global* setting, so lowering it for this disk would weaken it on the 200 GB
disks where it matters.

So the arithmetic that governs Phase 1 is:

| | |
|---|---|
| Disk | 1200 GiB |
| Filesystem after ext4 metadata (`-m 0`) | ~1180 GiB |
| **Comfortable ceiling before Longhorn calls the disk unschedulable** | **~885 GiB** |
| Library after dedup, ~800 GB, plus 10–20% derivatives | ~820–894 GiB |

That is close enough to the line to say out loud rather than discover: the MVP
import fits, and the years of growth the 2 TB assumption was buying do not.
**Crossing ~885 GiB is not an emergency, it is the trigger for
`-ExtendVolumeGroup`** — the second free chunk on the same host raises the
ceiling to roughly 1600 GiB, and the PVC follows with an expansion because
`allowVolumeExpansion` is on. That path is the whole reason for the LVM layer,
and it is now a planned step rather than a rescue.

Two things this does *not* change. The PVC in 1.7 stays `1Ti`: a Longhorn volume
is sparse, so its size is a cap rather than an allocation, and 1 TiB passes the
over-provisioning check against a 1180 GiB disk on the day it is created. And
nothing about the offsite design moves — Phase 4 is what protects this data, and
it protects 800 GB exactly as well as it protects 1.5 TB.

- [ ] **1.1 — Size and attach the disk.** A **1200 GiB dynamic** VHDX on the
      chosen host, attached to that host's k3s VM, leaving the rest of that
      drive's free space unallocated as the `-ExtendVolumeGroup` target:

      ```powershell
      New-VHD -Path D:\Aerie\aerie-node-N-bulk.vhdx -SizeBytes 1288490188800 -Dynamic
      Add-VMHardDiskDrive -VMName aerie-node-N -Path D:\Aerie\aerie-node-N-bulk.vhdx -ControllerType SCSI
      ```

      Note which host in the operator's own runbook — **not in this repo**.

- [x] **1.2 — `scripts/k3s/Add-BulkDisk.ps1`.** Written, modelled directly on
      [`Initialize-NodeStorage.ps1`](../../scripts/k3s/Initialize-NodeStorage.ps1):
      same SSH mechanics, same identify-the-disk-by-shape trick (an unpartitioned
      disk of about `-BulkDiskSizeGB`, refusing anything carrying a partition
      table without `-Force`), same two hard rules that no in-use disk and no
      root disk is ever a candidate — plus a third, that an existing LVM physical
      volume is never a candidate even under `-Force`. Creates `vg_bulk`/`lv_bulk`,
      formats ext4, mounts at `/var/lib/longhorn-bulk` with an fstab entry keyed
      by UUID, and asserts the capacity. Parameters: `-VMName`, `-IPAddress`,
      `-BulkDiskSizeGB` (default 1200), `-SizeTolerancePercent`,
      `-ExtendVolumeGroup`, `-Force`, `-PreflightOnly`. Idempotent.

      It also refuses to run on a node whose `/var/lib/longhorn` is not a mount,
      which is the node that skipped Provision 5 — adding a second disk to it
      would paper over that rather than fix it.

      The five parsing helpers it shares with its sibling moved to
      [`scripts/k3s/lib/AerieNodeDisk.ps1`](../../scripts/k3s/lib/AerieNodeDisk.ps1)
      rather than being copied, so a fix to either script's reading of `lsblk` is
      a fix to both.

- [x] **1.3 — Register the disk with Longhorn, tagged.** The script's Longhorn
      stage patches the node's `node.longhorn.io` CR to add the disk with
      `tags: ["bulk"]`, `allowScheduling: true`, and `storageReserved: 0`, then
      **waits for Longhorn to report it `Ready` and `Schedulable`** rather than
      stopping at "the API server accepted the patch". Those are different
      claims, and only the second is a gate.

      **The tag is the entire safety mechanism**: without it, Longhorn treats a
      large empty disk as general capacity and will place Prometheus and
      OpenSearch replicas on it.

      A **JSON merge patch**, sent on standard input rather than in the SSH
      command — the SSH helper refuses a command containing a double quote,
      because Windows PowerShell 5.1 does not escape one when it builds
      `ssh.exe`'s command line and `ssh.exe`'s parser then strips it. A strategic
      merge patch is not an option either: custom resources have no patch
      strategy, and a replace would drop the chart's own `default-disk-<hex>`
      entry and every replica on the node with it.

- [x] **1.4 — `.github/workflows/provision-7-bulk-disk.yml`.** The Actions
      wrapper for 1.2, matching `provision-5-node-storage.yml`'s shape:
      `workflow_dispatch` with the node, size and mode as inputs, the SSH key
      from secrets, the same runner setup, `runs-on: self-hosted`. Per the
      automation-first rule this ships *with* the script, not after it.

      **Provision 7, not the 6 this plan first wrote** — `provision-6` is
      already the backup AWS resources workflow.

      Inline `run:` blocks are pure ASCII, checked: the Windows runner reads a
      BOM-less temp `.ps1` as CP1252 and an em dash becomes a string-ending
      smart quote.

- [x] **1.5 — The `longhorn-bulk` StorageClass.** Appended to
      [`longhorn-storageclasses.yaml`](../../deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml),
      alongside `longhorn-r3` and `longhorn-r2`, carrying that file's existing
      note about immutability into the new comment:

      ```yaml
      numberOfReplicas: "1"
      dataLocality: "strict-local"
      diskSelector: "bulk"
      staleReplicaTimeout: "30"
      fsType: "ext4"
      dataEngine: "v1"
      disableRevisionCounter: "true"
      ```

      with `reclaimPolicy: Retain` and `allowVolumeExpansion: true` like its
      siblings, and `volumeBindingMode: WaitForFirstConsumer` — **unlike** its
      siblings, and for a reason written into the file: those two are
      `Immediate` because a Longhorn volume attaches over iSCSI from any node so
      binding early costs nothing. Under `strict-local` that stops being true —
      the volume's node is the pod's node, so binding before a consumer exists is
      binding before the constraint is known.

      The class name says what it is on the axis its siblings' names use:
      `r3`/`r2` are replica counts, and one-replica-pinned-to-a-tagged-disk is a
      third thing that "r1" would understate.

- [x] **1.6 — Namespace.** `immich` added to
      [`namespaces.yaml`](../../deploy/cluster/infrastructure/config/namespaces.yaml),
      with `app.kubernetes.io/part-of: aerie` like its siblings. Here rather than
      in the `photos` Kustomization for the reason that file's third bullet
      gives: Immich's HelmRelease must not create it, and one owner is the rule
      that file exists to keep.

- [ ] **1.7 — The PVC.** `immich-library`, `1Ti`, `ReadWriteOnce`,
      `storageClassName: longhorn-bulk`, in the `photos` layer (Phase 3). Sized
      *under* the disk deliberately: expansion is one field, and Longhorn cannot
      shrink. `1Ti` is a cap rather than an allocation — see the ceiling section
      above for the number that actually binds. Leaving headroom on the disk also
      leaves room for the `v+3` external library to become a second PVC on the
      same disk rather than a resize argument with a full volume.

- [ ] **1.8 — Gate.** Schedule a throwaway pod with the PVC, write a file, read
      it back, delete the pod, confirm the Longhorn volume shows exactly one
      replica on the `bulk` disk and that a pod forced to another node with a
      `nodeSelector` stays `Pending` rather than attaching remotely. That last
      check is what proves `strict-local` is doing the work, and it is the one
      that fails silently if the class was created without it.

      Record the `storageMaximum` Longhorn reports for the disk while here. Every
      size in this plan downstream of Phase 1 is a projection until that number
      is measured.

---

## Phase 2 — the database

**Goal:** a Postgres Immich will accept, backed up the way `aerie-pg` already is,
on stock images.

**Gate:** `\dx` in the Immich database lists `vector`, `vchord`, `cube` and
`earthdistance`, and a base backup exists in the WAL bucket under the new
cluster's own server name.

- [ ] **2.1 — Verify the two prerequisites before writing any manifest.**
      `kubectl explain cluster.spec.postgresql.extensions` must return a schema
      (CNPG ≥ 1.27), and `kubectl get --raw /api/v1 | grep -i imagevolume` /
      a trivial pod with an `image` volume must work (Kubernetes `ImageVolume`,
      beta from 1.33; the pin is `v1.35.7+k3s1`). **If either fails, stop.** The
      fallback is a community operand image carrying the extension, which
      Finding 3 rejects — reopen that decision explicitly rather than drifting
      into it.

- [ ] **2.2 — `deploy/cluster/photos/database/cluster.yaml`.** A CNPG `Cluster`
      named `immich-pg` in `immich`, modeled on
      [`data/cluster/cluster.yaml`](../../deploy/cluster/data/cluster/cluster.yaml):
      `local-path` storage, resource requests and limits (goal 2 of
      [design.md](swarm/design.md) — no workload without requests), a
      `PodDisruptionBudget`, and the operand/extension block from Finding 3.
      Differences from `aerie-pg`, each deliberate:
      - **`instances: 1`.** Immich is not on the HA list, and a second instance
        doubles the local-path footprint to protect data that has WAL archiving
        and a nightly dump. Make it a variable (`IMMICH_POSTGRES_INSTANCES`,
        default `1`) rather than a literal, so raising it is a variable change.
      - **Pin the operand to a patch version** — `18.4-standard-trixie`, not
        `18-standard-trixie` — matching the reasoning already in `cluster.yaml`
        about unpinned major-version images. Same for the extension image, which
        upstream already pins.
      - **`shared_preload_libraries: ["vchord.so"]`**, which `aerie-pg` must not
        get.

- [ ] **2.3 — `deploy/cluster/photos/database/database.yaml`.** The CNPG
      `Database` CR creating the extensions declaratively — `vector`, `vchord`,
      `cube`, `earthdistance`, all `ensure: present`. Declaring them here rather
      than in `postInitSQL` means an extension added by a later Immich release is
      a manifest line rather than a bootstrap-only edit that a running cluster
      would ignore.

- [ ] **2.4 — WAL archiving into the existing bucket.** An `ObjectStore` named
      `immich-pg-wal` in `immich`, copying
      [`objectstore.yaml`](../../deploy/cluster/data/cluster/objectstore.yaml)
      exactly, including its `retentionPolicy: "30d"` and sidecar resources. Same
      `${WAL_BUCKET}`, same IAM user: barman namespaces by the cluster's server
      name, so two clusters in one bucket do not collide, and a second bucket
      would be a second thing to create, scope and pay for with no isolation
      benefit — both clusters' backups are already reachable by one credential's
      blast radius.

- [ ] **2.5 — The credential reaches a second namespace.** The three
      `/aerie/postgres/wal-s3-*` parameters in
      [`parameters.json`](../../scripts/secrets/parameters.json) currently carry
      one `kubernetes` block each, targeting `aerie`. The generator **already
      accepts an array of blocks** (`New-ExternalSecrets.ps1` header, "may be a
      single block or an array of them"), so this is a data edit: add the
      `immich`/`cnpg-wal-s3` target to each of the three, then
      `pwsh ./scripts/secrets/New-ExternalSecrets.ps1` and commit the generated
      files. **Do not hand-write the ExternalSecret** — `ci.yml` runs the
      generator with `-Check` and will fail the build.

- [ ] **2.6 — `ScheduledBackup`.** Nightly base backup, modeled on
      [`scheduledbackup.yaml`](../../deploy/cluster/data/schema/scheduledbackup.yaml),
      at a time that does not collide with either the existing 03:10 window or
      Phase 4's archive run.

- [ ] **2.7 — Gate.** `kubectl cnpg psql immich-pg -n immich -- -c '\dx'` lists
      all four extensions; the cluster reports `Cluster in healthy state`; a
      first base backup completes and is visible in the bucket under the
      `immich-pg` server name.

---

## Phase 3 — Immich

**Goal:** Immich reachable at `photos.${DOMAIN}` from the LAN and the tailnet,
monitored, with an admin account and nothing in it yet.

**Gate:** a phone on the tailnet installs the app, signs in, and backs up one
photo, which appears in the web UI.

- [ ] **3.1 — The Flux layer.** `deploy/cluster/photos.yaml`, a Kustomization
      named `photos` over `./deploy/cluster/photos`, modeled on
      [`apps.yaml`](../../deploy/cluster/apps.yaml): `dependsOn` on
      `infra-config` only — **not** on `data-schema` or `apps`, which is the
      whole point of it being its own layer. `wait: true`, `prune: true`,
      `postBuild.substituteFrom` the `aerie-cluster-config` ConfigMap. Add it to
      [`deploy/cluster/kustomization.yaml`](../../deploy/cluster/kustomization.yaml).

- [ ] **3.2 — The chart source.** An **OCI** `HelmRepository` — the first in this
      tree, because the HTTP repo at `immich-app.github.io/immich-charts` has been
      retired and no longer receives updates:

      ```yaml
      apiVersion: source.toolkit.fluxcd.io/v1
      kind: HelmRepository
      metadata: { name: immich, namespace: immich }
      spec:
        type: oci
        url: oci://ghcr.io/immich-app/immich-charts
        interval: 1h
      ```

- [ ] **3.3 — The HelmRelease.** Chart `immich`, pinned `0.13.1`, into the
      `immich` namespace, values covering:
      - `immich.persistence.library.existingClaim: immich-library` (1.7). The
        chart does **not** create this volume and says so.
      - DB env: `DB_HOSTNAME: immich-pg-rw`, `DB_USERNAME`/`DB_DATABASE_NAME` per
        the CNPG cluster's app user, `DB_PASSWORD` from the CNPG-generated
        `immich-pg-app` secret via `secretKeyRef`. **CNPG generates this
        credential; it never touches SSM or the repo** — it is not an operator
        secret, it is a cluster-internal one, and that distinction is worth a
        comment in the file.
      - `valkey.enabled: true`, with `persistence.data` switched from `emptyDir`
        to a 1 Gi `persistentVolumeClaim` on `longhorn-r2`.
      - `machine-learning` enabled, `persistence.cache` switched from `emptyDir`
        to a 10 Gi PVC on `longhorn-r2` — without it the model set is
        re-downloaded on every pod restart.
      - `immich.metrics.enabled: true` — kube-prometheus-stack is already
        installed, so this is free ServiceMonitors.
      - `immich.configuration`: `storageTemplate` enabled with a
        `{{y}}/{{y}}-{{MM}}-{{dd}}/{{filename}}` layout, `trash` enabled at 30
        days, and **`backup.database` enabled** — that nightly dump is what
        Phase 4 carries offsite.
      - **Resource requests and limits on every container.** Not optional here:
        [design.md](swarm/design.md) makes them the mechanism by which the
        scheduler can place anything at all, and the ML container is the largest
        single memory consumer this cluster will have run.

- [ ] **3.4 — Ingress.** `photos.${DOMAIN}`, `ingressClassName: traefik`, **no
      `tls:` block** (the wildcard `TLSStore` default terminates at the
      entrypoint) and **no auth middleware** (Finding 7). The chart's own ingress
      block carries nginx annotations for body size; **do not enable it** — write
      the Ingress in this layer instead. Traefik applies no request body limit by
      default, so the nginx `proxy-body-size` annotation has no analogue and needs
      none, which is exactly the sort of thing that gets "fixed" wrongly later.

      DNS needs nothing: pfSense already resolves every `*.${DOMAIN}` name to the
      ingress VIP ([file-share.md](../file-share.md)).

- [ ] **3.5 — Uptime monitor.** One entry in
      [`static-monitors-configmap.yaml`](../../deploy/cluster/observability/controllers/static-monitors-configmap.yaml):

      ```toml
      type = "http"
      name = "Photos"
      url = "http://immich-server.immich.svc:2283/api/server/ping"
      ```

      matching the `api.toml`/`share.toml` shape and interval already there.

- [ ] **3.6 — The Alpine DNS caveat.** Immich's docs flag a DNS resolution bug in
      Alpine-based images on clusters whose nodes carry `search` domains in
      `/etc/resolv.conf`. Check the nodes; if search domains are present and the
      pods show intermittent resolution failures, the fix is a `dnsConfig` with
      `ndots: 1` on the affected Deployments. Written down here because the
      symptom (occasional 502s, ML timeouts) does not look like DNS.

- [ ] **3.7 — Create the admin account and the family's accounts.** First run
      only. Turn **off** public registration afterward.

- [ ] **3.8 — Gate.** Phone app on the tailnet signs in and completes a backup of
      one photo; the web UI at `photos.${DOMAIN}` shows it; Kuma is green;
      Grafana shows the Immich ServiceMonitor's metrics.

---

## Phase 4 — the offsite archive

**Goal:** every original has a second copy in a different building, on a schedule,
with an alert when the schedule stops being kept, and a rehearsed restore.

**Gate:** 4.8 — files restored from Deep Archive, checksums matched, and a
database restored into a scratch cluster. **This gate blocks Phase 5's
cancellations.**

- [ ] **4.1 — Bucket and lifecycle.** `${PHOTOS_ARCHIVE_BUCKET}`, versioning
      **on**, public access blocked, default encryption on, and a lifecycle rule
      transitioning `library/`, `upload/`, `profile/` to `DEEP_ARCHIVE` and
      expiring noncurrent versions after 90 days. `backups/` (the DB dumps) stays
      on `STANDARD_IA` so a database restore never waits on a Glacier retrieval.
      Uploading with `--s3-storage-class DEEP_ARCHIVE` directly is also possible;
      the lifecycle rule is the belt, and it is what catches an object written by
      a future job that forgets the flag.

- [ ] **4.2 — IAM.** A dedicated `aerie-photos-archive` user scoped to that bucket
      alone, policy committed to
      [`scripts/secrets/iam/`](../../scripts/secrets/iam/) next to
      `aerie-cnpg.policy.json`, granting `PutObject`, `GetObject`, `ListBucket`,
      and `RestoreObject` — **and not `DeleteObject`**. Finding 5 says the job
      never deletes; the IAM policy is what makes that structural rather than a
      property of a flag someone can change. 4.9's reconcile is a human with a
      different credential.

- [ ] **4.3 — Parameters.** Three entries in
      [`parameters.json`](../../scripts/secrets/parameters.json) —
      `/aerie/photos/archive-access-key-id`, `-secret-access-key`, `-region` —
      targeting Secret `photos-archive-s3` in `immich`; then regenerate the
      ExternalSecrets. The bucket name is an **operator value, not a secret**:
      it goes in [`cluster-config.json`](../../scripts/k3s/cluster-config.json)
      as `PHOTOS_ARCHIVE_BUCKET`, exactly as `WAL_BUCKET` already does.

- [ ] **4.4 — The CronJob.** `photos-archive`, nightly, one container running
      `rclone`:
      - Mounts `immich-library` **read-only** (`readOnly: true` on the mount).
      - **`podAffinity`** — `requiredDuringSchedulingIgnoredDuringExecution`,
        `topologyKey: kubernetes.io/hostname`, matching the `immich-server`
        pod's labels. This is load-bearing and non-obvious: the PVC is
        `ReadWriteOnce` and `strict-local`, so the volume is attachable on
        exactly one node, and a CronJob scheduled anywhere else sits failing to
        attach forever. Co-locating by pod affinity rather than by node name
        keeps the operator's node name out of the repo (Finding 2), and RWO
        permits multiple pods on the *same* node.
      - `rclone copy` — never `sync` — of `library/`, `upload/`, `profile/`,
        `backups/`. **Excludes `thumbs/` and `encoded-video/` explicitly**, with
        the reason in a comment, because "back up everything" is the default
        instinct and here it is 20% wasted forever.
      - `--transfers`, `--checkers` and `--bwlimit` set conservatively: this runs
        on the same node as the library and shares the house uplink.
      - Resource requests and limits, and `concurrencyPolicy: Forbid` — the first
        run will take days at a residential uplink, and a second one starting on
        top of it would fight for the same bandwidth.

- [ ] **4.5 — The first run is a special case.** 800 GB at a typical residential
      upload rate is **days to weeks**, and the nightly job will simply keep
      going. Either let it (with `--bwlimit` scheduled to open up overnight), or
      seed the bucket once from a machine on a faster link. Decide before starting
      the ingest, because Phase 5 will multiply the volume.

- [ ] **4.6 — The alert.** kube-state-metrics is already installed, so the
      backup-age alert needs no pushgateway:

      ```promql
      time() - kube_cronjob_status_last_successful_time{cronjob="photos-archive"} > 36h * 60 * 60
      ```

      Add it to
      [`observability/config/alerts/`](../../deploy/cluster/observability/config/alerts/)
      alongside the cluster and flux rules. **[Phase 8](swarm/phase-8-backup-v2.md)
      names the fact that the alerting flows have no teeth yet** — an alert into a
      receiver nobody has tested is paperwork. Fire this one deliberately once
      (suspend the CronJob for two days, or edit the threshold) and confirm it
      arrives somewhere a human reads.

- [ ] **4.7 — The second-copy hook.** Note in the CronJob's comment that a second
      remote is `rclone copy` to another `remote:` and nothing else — the reason
      the job is written against rclone remotes rather than S3 SDK calls. Do not
      build it now; the [rule of three](swarm/design.md#goals) is satisfied by
      local + archive + the fact that the sources still exist during Phase 5, and
      it stops being satisfied the day the last subscription is cancelled. **Add
      the second remote at that point**, and 5.9 says so.

- [ ] **4.8 — Rehearse the restore. This is the gate.** Not a walkthrough — an
      actual restore:
      1. Pick ~20 originals spanning several years. Note their SHA-256.
      2. `rclone backend restore` them from Deep Archive (bulk tier), wait out
         the ~48h, download, and **compare checksums**.
      3. Restore the latest logical DB dump from `backups/` into a scratch CNPG
         cluster and confirm the asset rows match the files.
      4. Write down what it actually cost and how long it took, and put it in
         [`disaster-recovery.md`](../disaster-recovery.md) — which
         [7c.10](swarm/phase-7-cutover.md) deliberately left minimal and which
         this is the first phase with something concrete to add to.

- [ ] **4.9 — Document the reconcile.** A short runbook section: how to remove
      offsite objects for photos deliberately deleted locally, run by a human with
      a credential that has `DeleteObject`, never by the CronJob. Include the
      180-day minimum-duration billing note from the cost section.

---

## Phase 5 — ingest, dedup, and cancelling the subscriptions

**Goal:** every photo from every source is in Immich, once, with the best
available copy as the keeper — and the subscriptions are gone.

**Gate:** 5.8 — per-source counts reconcile, spot-checks pass, the archive has
caught up, and only then does anything get cancelled.

- [ ] **5.1 — Export everything first, and keep the exports.** Google Takeout per
      account (request the 50 GB split), iCloud via Apple's Data and Privacy
      export, Dropbox via `rclone`, plus the local and network drives. Land it all
      in one staging tree with **one directory per source account**:

      ```text
      staging/
        google-<label>/     icloud-<label>/     dropbox-<label>/
        drive-<label>/      nas-<label>/
      ```

      `<label>` is the operator's own; nothing about it reaches the repo. **Keep
      every export until the Phase 5 gate passes** — they are the third copy while
      the archive is still filling.

- [ ] **5.2 — `scripts/photos/Build-PhotoIndex.ps1`.** Walks the staging tree and
      writes a manifest: path, size, SHA-256, EXIF `DateTimeOriginal` where
      present, and the source directory. One pass, one file, resumable — this is
      the artifact everything else reads, and it is worth more than the tooling
      around it. Output as JSON Lines so a partial run is still usable.

- [ ] **5.3 — `scripts/photos/Resolve-Duplicates.ps1`.** Groups the manifest by
      SHA-256 and elects a keeper per group by the fidelity order from Finding 6
      (local/NAS → iCloud → Dropbox → Google), tie-breaking on file size and on
      the presence of a sidecar. Emits a per-source **include list** for the
      import and a report: how many files, how many exact duplicates, how many
      bytes the dedup saved, which sources contributed keepers. **It moves and
      deletes nothing** — it only decides what gets uploaded, so a bad heuristic
      costs a re-run rather than a photo.

- [ ] **5.4 — `scripts/photos/Import-PhotoSource.ps1`.** The thin wrapper: takes
      `-Source`, `-ServerUrl`, `-ApiKey`, `-IncludeList`, `-DryRun`, and shells
      out to `immich-go` with the right flags per source kind — `--google-photos`
      and album creation for Takeout, plain folder upload with
      `--album` for the rest. Tags every asset with its source label so a bad run
      is `immich-go` + a filter away from being undone. Writes a run manifest
      next to the index.

      Pin `immich-go` in [`versions.json`](../../scripts/versions.json) alongside
      the other tool pins — **v0.32.0 or later**, which is the release that adds
      Immich v3 compatibility. A moving `latest` here would silently change what
      an import means.

- [ ] **5.5 — Import in fidelity order, one source at a time**, with `-DryRun`
      first on each. Between sources, let Immich's jobs drain: thumbnails,
      metadata extraction, and ML. Do **not** run two sources concurrently — the
      keeper election depends on order, and the queue depth is what makes the
      order actually happen.

- [ ] **5.6 — Run Immich's duplicate detection and review it by hand.** Near-
      duplicates (the re-encodes) surface as clusters in the UI. Review them.
      **Nothing automated deletes a photo in this plan** — Finding 6, and it is
      the one place a bad heuristic is unrecoverable rather than a re-run.

- [ ] **5.7 — Turn on phone backup for every family device.** This is the step
      that makes the migration permanent rather than a snapshot: if phones keep
      backing up to the old clouds, the problem regrows.

- [ ] **5.8 — Reconcile, then cancel. The gate.** For each source: the count of
      files in the index, minus the exact duplicates the resolver elected away,
      should equal the assets tagged with that source. Investigate every gap —
      unsupported formats, `.MP` motion-photo sidecars and Live Photo pairing are
      the usual answers. Spot-check 20 memorable photos per account by hand
      (oldest, a wedding, a funeral, a first day of school — the ones whose
      absence would matter). Confirm Phase 4's archive has caught up on
      everything imported. **Then** cancel one subscription, wait a full billing
      cycle, and cancel the rest.

- [ ] **5.9 — Add the second offsite remote (4.7).** The exports and the cloud
      accounts were the third copy. The day they are gone, they need replacing —
      B2 is the row that competes, for a different vendor and free egress.

---

## v+1 — sharing with people outside the network

**Not built. Scaffolded so the MVP does not close the door.** The MVP closes
nothing: Immich's sharing model is entirely internal to Immich until something
publishes it, and no MVP decision picks the publisher.

Immich's own mechanism is **shared links** — a per-album URL with an optional
password and expiry, and its own controller under `/share/`. The design question
is only how a request from outside reaches it, and there are four answers with
genuinely different properties:

| Option | Shape | Cost | Cost to reverse |
|---|---|---|---|
| **Cloudflare Tunnel** | Outbound-only tunnel from a pod; `photos-share.${DOMAIN}` public, path-restricted to `/share/` and its assets | A Cloudflare account and a second dependency; free tier covers it | One Deployment and a DNS record |
| **Tailscale Funnel** | Existing tailnet, no new vendor | Public URL is a `*.ts.net` name, not ours; harder to hand to a grandparent | Trivial |
| **Public ingress + WAF** | The VIP gets a public DNS name and a hardened path allowlist | Opens the house's real ingress to the internet — a much larger step than the others, and one this plan should not take casually | Real work |
| **Export and send** | Generate an album zip or a static gallery, hand it over out-of-band | Nothing to secure, nothing to run | Nothing |

**The recommendation to evaluate first is Cloudflare Tunnel**, restricted to
`/share/*`, on a hostname that is not the one the family uses. The reasoning: it
is the only option that exposes a *path* rather than a host, it requires no
inbound firewall change, and per [`ethos.md`](../ethos.md) it needs a documented
self-hosted alternative before this ever ships publicly — which "export and send"
already is.

Two things the MVP should therefore be careful about, and both are already true
of the plan above: **do not** put a forwardAuth in front of Immich (Finding 7 —
it would also intercept `/share/`), and **do** keep the ingress in the `photos`
layer rather than in `charts/aerie`, so adding a second, differently-scoped
ingress is a file in a directory rather than a chart change.

---

## v+2 — Immich albums on the kiosk

**Not built. Scaffolded.** The kiosk dashboard occasionally scrolling through
family photos is the feature most likely to make the whole thing feel worth it,
and it is small — but it needs one decision made now so the MVP does not make it
harder.

The shape:

1. **A tag or album-name convention marks what the kiosk may show** — say, an
   Immich tag `kiosk`. Album-name prefixes are the alternative; tags survive
   renames, so prefer tags.
2. **An Immich API key, scoped and stored in SSM**, reaching the cluster the same
   way every other credential does — `parameters.json`, ExternalSecret, no bytes
   in git.
3. **A `Photos` module in `Aerie.Api`** — and *this* one genuinely is a family-apps
   module in the sense of [`family-apps-architecture.md`](../family-apps-architecture.md):
   a folder under `Modules/`, one line in the registry, no infrastructure. It
   proxies Immich rather than exposing it: the kiosk asks Aerie for "the next
   photo", Aerie asks Immich, caches the thumbnail, and returns it. That keeps
   the Immich API key off the tablets entirely, and it means the dashboard never
   needs a second origin, a second auth, or a CORS story.
4. **A dashboard view** that fades between photos, respecting the existing
   circadian theme and the standby behavior from
   [`kiosk_brightness.md`](kiosk_brightness.md) — a photo frame that lights up a
   dark hallway at 3am is the same bug that plan exists to fix.

**What the MVP owes it:** nothing structural, and that is the point of writing it
down. The proxy is possible because Immich has a real API and the kiosk talks to
`Aerie.Api` already. The only thing that would break it is putting Immich
somewhere the API cannot reach it — which is exactly what a public-only or
separately-networked deployment would have done.

---

## Keeping the photography pathway open

The brief is explicit that this is a **goal, not a phase**: a serious hobbyist
workflow for dumping large batches of RAW files, culling, editing, and publishing
the keepers — and that the reason for it is that file management killed the
hobby. No solution here. What this plan owes it is that no MVP decision has to be
undone to get there.

Four decisions above are the ones that matter, and all four are already made the
right way:

1. **Immich supports read-only External Libraries** — a filesystem path Immich
   indexes without owning or moving the files. That is the entire integration
   pathway: an editing workflow writes to a directory, Immich shows it. Nothing
   in the MVP prevents it.
2. **The library PVC is sized under the disk** (1.7), so an `photos-external` PVC
   can be created on the same tagged disk later without an argument with a full
   volume. Two PVCs beat one PVC with `subPath` mounts here: they expand
   independently, they back up independently, and the RAW pile's retention rules
   will not be the family album's.
3. **The archive job copies a tree, not a repository** (Finding 5), so adding
   `external/` to it is one line, and the archived RAW files stay readable by
   anything that can read S3.
4. **The catalog is Postgres, not a proprietary sidecar database.** Whatever the
   editing tool turns out to be — darktable, Lightroom with an export step, or
   something that does not exist yet — the metadata it writes as XMP sidecars is
   a file next to the RAW, which an external library indexes.

The one thing to **not** do before that workflow is designed: do not let the
storage template or the external-library layout encode assumptions about JPEG
derivatives. A RAW plus its sidecar plus its exported JPEG is three files that
are one photograph, and that is a modeling decision worth making deliberately
rather than inheriting.

The motivation behind this — that the technical moat is what stopped the hobby —
is bigger than photography and is written up separately in
[`purpose.md`](../purpose.md).

---

## What must stay untouched

The check on Finding 1. None of this should be edited by any phase above:

`charts/aerie/**` (every template, including the ingress —
[3.4](#phase-3--immich) writes Immich's in the `photos` layer),
`src/Aerie.Api/**`, `src/Aerie.Web/**`, `deploy/cluster/apps.yaml` and
`deploy/cluster/apps/**`, `deploy/cluster/data/**` (Immich's database is a new
tree, not an edit to that one), `Program.cs`, `ci.yml`, the `Makefile`.

Five files outside the new `photos` tree are expected to change, each by
addition only:

| File | Change |
|---|---|
| [`namespaces.yaml`](../../deploy/cluster/infrastructure/config/namespaces.yaml) | One `immich` Namespace (1.6) |
| [`longhorn-storageclasses.yaml`](../../deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml) | One `longhorn-bulk` class (1.5) |
| [`kustomization.yaml`](../../deploy/cluster/kustomization.yaml) | One `photos.yaml` entry (3.1) |
| [`parameters.json`](../../scripts/secrets/parameters.json) | Three new parameters, plus a second target on three existing ones (2.5, 4.3) |
| [`static-monitors-configmap.yaml`](../../deploy/cluster/observability/controllers/static-monitors-configmap.yaml) | One `photos.toml` monitor (3.5) |

Plus the new files: `scripts/k3s/Add-BulkDisk.ps1`,
`scripts/k3s/lib/AerieNodeDisk.ps1`,
`.github/workflows/provision-7-bulk-disk.yml`, `scripts/photos/*`,
`scripts/secrets/iam/aerie-photos-archive.policy.json`, and
`deploy/cluster/photos/**`.

If a phase needs to edit something not on either list, the seam has a gap — fix
the seam.

## Risks

- **The first archive upload is measured in days.** It overlaps Phase 5, which
  multiplies the volume. 4.5 is the decision point, and getting it wrong means
  the Phase 5 gate cannot pass on schedule.
- **One node holds the library.** That node's loss is an outage until it is
  rebuilt, and a restore from Deep Archive is measured in days. Accepted
  deliberately (Finding 2); revisit if the library ever holds something with an
  SLA.
- **Immich ships breaking changes.** It is a fast-moving product with a real
  history of migrations that must be run in order. Pin the chart, read release
  notes before bumping, and never let image automation move this tag — the
  automation in [`apps/automation/`](../../deploy/cluster/apps/automation/)
  targets first-party images and must not learn about this one.
- **The `vchord` extension image is third-party** (`tensorchord`). It carries
  only the extension, not the database (Finding 3), which bounds the exposure —
  but it is on the critical path of the database starting, and a tag that
  disappears upstream is an outage. Mirror it to the same registry the
  first-party images use if that becomes a concern.
- **Nothing in this plan protects against a bad human decision inside Immich** —
  an album deleted, a person merged wrongly. The 30-day trash and the DB's PITR
  window are what cover it, and both are shorter than "I noticed a year later".

## When this plan closes out

Per [the plans README](README.md), a finished plan is dissipated, not archived.
What survives, and where:

- **`docs/photos-architecture.md`** — the storage substrate, the database shape,
  the backup design, and the reasoning in Findings 2 through 5.
- **[`disaster-recovery.md`](../disaster-recovery.md)** — the restore procedure
  and the rehearsal record from 4.8.
- **[`docs/purpose.md`](../purpose.md)** — already written, and independent of
  this plan.
- **`scripts/photos/README.md`** — how to run an ingest, for the next drive found
  in a closet.
- The `v+1` and `v+2` sections move to their own plans when they start. The
  photography section moves into `photos-architecture.md` as a constraints
  section — it is a design boundary, not a plan.
