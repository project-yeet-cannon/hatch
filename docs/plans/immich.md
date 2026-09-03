# Photos — Immich on Aerie

**Status:** Phases 1, 1b and 2 are done — the bulk disk exists and is proven,
the node OS disks are off the slow volumes, and `immich-pg` is running with its
extensions, its WAL archiving and a nightly base backup. Phase 3's tree is
written and validated; its two remaining steps are the operator's and need this
commit on `main` first. Phase 4 is the offsite archive.
Five phases to MVP, each independently useful and
independently revertible; two follow-ons named here so the MVP does not close
their doors — `v+1` sharing, still scaffolded, and `v+2` kiosk, now built; and one long-horizon pathway (the photography workflow) that
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
| Kiosk photo frame | **`v+2`** — built, 2026-08-25; see below |
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

> **Correction, 2026-08-25 (Phase 3).** The paragraph above is right about the
> design and wrong about the mechanism, and the difference cost a Pending pod
> to discover. Longhorn does **not** pull the consumer onto the disk's node;
> under `WaitForFirstConsumer` the causality runs the other way — the scheduler
> picks a node knowing nothing about Longhorn disk tags, and Longhorn is then
> told to put the single replica *there*. On a node with no `bulk` disk the
> volume reports `tags not fulfilled` and the pod waits forever. The fix keeps
> the ethos intact and is the same shape: **a node label, not a node name**,
> written by `Add-BulkDisk.ps1` from the same fact that produces the disk tag.
> The transcript and the reasoning are in [Phase 3](#phase-3--immich) below.

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
| Library storage | New disk, Longhorn **`longhorn-bulk`** class: 1 replica, `strict-local`, `diskSelector: bulk`, plus a `storage.aerie/bulk` node label the consumer selects on | Finding 2, as corrected by [Phase 3.0](#30--the-measurement-that-changed-the-design) — the disk tag places the replica, the label places the pod, and neither names a node |
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
        │  ^      └─── immich-machine-learning (model cache, 10Gi)    │
        │  └ nodeSelector storage.aerie/bulk  (Phase 3.0)             │
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
   CronJob photos-archive  ── nodeSelector storage.aerie/bulk ─────────┘
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

**Status:** done 2026-08-25. 1.1 was the operator's and has happened — the
disk is attached, and `storageMaximum` is **1,267,109,507,072 bytes (1180.09
GiB)**, which is the measured number every projection downstream of Phase 1 was
waiting on. 1.7 and 1.8 landed with Phase 3, and 1.8 is the step that falsified
Finding 2's mechanism; see that phase.

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

- [x] **1.7 — The PVC.** `immich-library`, `1Ti`, `ReadWriteOnce`,
      `storageClassName: longhorn-bulk`, in the `photos` layer (Phase 3). Sized
      *under* the disk deliberately: expansion is one field, and Longhorn cannot
      shrink. `1Ti` is a cap rather than an allocation — see the ceiling section
      above for the number that actually binds. Leaving headroom on the disk also
      leaves room for the `v+3` external library to become a second PVC on the
      same disk rather than a resize argument with a full volume.

- [x] **1.8 — Gate.** Passed, but only on the second attempt, and the first
      attempt is the more useful half — it is what turned Finding 2's mechanism
      from an assumption into a measurement. Both runs are written up under
      [Phase 3's 3.0](#30--the-measurement-that-changed-the-design).

      Short version: a throwaway pod with **no** `nodeSelector` landed on a node
      with no bulk disk, and the volume reported `tags not fulfilled` while the
      pod sat `Pending` — the opposite of what "strict-local forces the consumer
      pod there" predicts. With the node label in place the same pod scheduled on
      the tagged node, wrote and read its file, and
      `kubectl get replicas.longhorn.io` showed exactly one replica, on
      `/var/lib/longhorn-bulk`.

      `storageMaximum` for the disk: **1,267,109,507,072 bytes = 1180.09 GiB**,
      which is what the ceiling section above had projected.

---


## Phase 1b - node OS disks are off the slow volume

Done 2026-08-25. All three node OS disks were moved onto their hosts' measured-
fastest volumes as fixed VHDXs, which unblocks 1.1: the bulk disk no longer
shares a volume with an etcd write-ahead log. The constraint that remains is
the one 1.1 already carries — put the bulk disk on a volume that carries no OS
disk. See [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md)'s on-disk
layout.

## Phase 2 — the database

**Goal:** a Postgres Immich will accept, backed up the way `aerie-pg` already is,
on stock images.

**Gate:** `\dx` in the Immich database lists `vector`, `vchord`, `cube` and
`earthdistance`, and a base backup exists in the WAL bucket under the new
cluster's own server name.

**Status:** done 2026-08-25. Gate passed on first apply — the cluster reported
`Cluster in healthy state` 32 seconds after the Kustomization went Ready, all
four extensions came back `applied: true`, and the immediate base backup
completed in 32 seconds and landed under `immich-pg/base/` and `immich-pg/wals/`
in the WAL bucket, disjoint from `aerie-pg/`.

- [x] **2.1 — Verify the two prerequisites before writing any manifest.**
      Both pass, and neither needed the fallback Finding 3 rejects. CNPG is
      **1.30.0** and `kubectl explain cluster.spec.postgresql.extensions`
      returns the full schema. `ImageVolume` is live on `v1.35.7+k3s1`
      (containerd 2.2.5-k3s2) — verified with a throwaway pod rather than from
      the API schema alone, since the field existing and the kubelet honouring
      it are different claims. That pod mounted
      `ghcr.io/tensorchord/vchord-scratch:pg18-v1.1.1` and listed `vchord.so`
      under `/usr/lib/postgresql/18/lib` and `vchord.control` under
      `/usr/share/postgresql/18/extension`, which is what pinned the two search
      paths in 2.2 to values read off the image rather than copied from a doc.

- [x] **2.2 — [`deploy/cluster/photos/database/cluster.yaml`](../../deploy/cluster/photos/database/cluster.yaml).**
      `immich-pg` in `immich`, on `local-path`, with the operand pinned to
      `ghcr.io/cloudnative-pg/postgresql:18.4-standard-trixie` and `vchord`
      mounted as an extension image. Four notes on how it landed:
      - **`instances: ${IMMICH_POSTGRES_INSTANCES:=1}`**, with the key added to
        [`cluster-config.json`](../../scripts/k3s/cluster-config.json) as
        `required: false`. The `:=` default is the difference from
        `POSTGRES_INSTANCES`, and it is deliberate: an installation that never
        sets the variable must get a working cluster rather than a
        Kustomization that fails strict substitution. Same treatment
        `AERIE_REPO_OWNER` already gets.
      - **The operand flavour matters as much as the pin.** `-standard-trixie`,
        not the default minimal image the data tier runs — the declarative
        extension mechanism points `extension_control_path` at a Debian-layout
        tree, and the minimal image is not one. Live proof:
        `SHOW extension_control_path` on the running primary returns
        `$system:/extensions/vchord/usr/share/postgresql/18`.
      - **No `synchronous:` block**, and this is the omission worth naming.
        `aerie-pg` sets `dataDurability: required` with `number: 1`, which at
        one instance would block every write forever because nothing can ever
        acknowledge. Raising `IMMICH_POSTGRES_INSTANCES` means adding the block
        at the same time, and the file says so where it would be added.
      - **The `PodDisruptionBudget` is `enablePDB: true`, not a manifest.** CNPG
        owns the PDB for a Cluster and creates it from that flag — a
        hand-written one would be a second object fighting the controller over
        the same pods. What comes out is `immich-pg-primary`, `minAvailable: 1`,
        allowed disruptions 0, which is the same shape `aerie-pg-primary`
        already has and the same drain caveat, not a new one.

- [x] **2.3 — [`deploy/cluster/photos/database/database.yaml`](../../deploy/cluster/photos/database/database.yaml).**
      All four extensions `ensure: present`, and **`cube` is listed before
      `earthdistance` on purpose**: `earthdistance` is implemented in terms of
      `cube`'s type and `CREATE EXTENSION` fails outright without it, and CNPG
      applies the list in order — so the dependency is expressed by position and
      nothing else. Live: `vector` 0.8.6, `vchord` 1.1.1, `cube` 1.5,
      `earthdistance` 1.2, all in `public`.

- [x] **2.4 — WAL archiving into the existing bucket.**
      [`objectstore.yaml`](../../deploy/cluster/photos/database/objectstore.yaml),
      a copy of the data tier's including `retentionPolicy: "30d"` and the
      512Mi sidecar limit that file's own comment explains. One thing checked
      rather than assumed: [`aerie-cnpg.policy.json`](../../scripts/secrets/iam/aerie-cnpg.policy.json)
      is scoped to the whole bucket, not to an `aerie-pg/*` prefix, so the
      existing credential reaches the new server name with no IAM change.

- [x] **2.5 — The credential reaches a second namespace.** All three
      `/aerie/postgres/wal-s3-*` parameters in
      [`parameters.json`](../../scripts/secrets/parameters.json) now carry an
      array of two `kubernetes` blocks, and
      [`immich-cnpg-wal-s3.yaml`](../../deploy/cluster/infrastructure/config/external-secrets/immich-cnpg-wal-s3.yaml)
      is the generator's output, not a hand-written file. `SecretSynced` in
      `immich` 33 seconds after the reconcile.

- [x] **2.6 — [`ScheduledBackup`](../../deploy/cluster/photos/database/scheduledbackup.yaml).**
      `0 0 1 * * *` — 01:00, six fields. The time is chosen against what else
      already runs on these nodes: 02:00 is `aerie-pg`'s base backup, 03:10 the
      restic backup, 04:00 Sundays the restic verify. 01:00 leaves the whole
      04:00-and-later window free for Phase 4's `photos-archive`, which has to
      start after Immich's own nightly dump and then hold the house uplink for
      as long as it takes.

- [x] **2.7 — Gate.** `\dx` lists all four; `Cluster in healthy state`;
      `ContinuousArchiving=True` and `LastBackupSucceeded=True`; and the bucket
      holds `immich-pg/base/20260825T215306/` beside four WAL segments under
      `immich-pg/wals/`.

### The Flux layer arrived a phase early, and the path split is why

Phase 3.1 is where [`photos.yaml`](../../deploy/cluster/photos.yaml) was meant
to be written. It could not be: 2.7's gate asks for a *running* cluster, and
nothing under `deploy/` runs without a Kustomization pointing at it. So the
layer ships here, and the shape it ships in is the one 3.1 asked for — depends
on `infra-config` and on nothing else, `wait: true`, `prune: true`,
`postBuild.substituteFrom` the `aerie-cluster-config` ConfigMap — with two
differences worth knowing before Phase 3 opens the file:

- **It is named `photos-database`, not `photos`, and its path is
  `./deploy/cluster/photos/database`.** Phase 3's HelmRelease belongs in a
  sibling `photos` Kustomization over `./deploy/cluster/photos/app` with
  `dependsOn: photos-database`, for exactly the reason
  [`data.yaml`](../../deploy/cluster/data.yaml) splits `data-cluster` from
  `data-schema`: a HelmRelease applied in the same pass as the database it
  connects to starts against a primary that is not up. Doing the split now is
  what keeps Phase 3 from moving files.
- **The `Database` CR and the `ScheduledBackup` sit in the *database* layer
  anyway**, even though both assume the cluster answers. The data tier's split
  exists for a *Job*, which burns its `backoffLimit` failing to connect and
  then stays failed; these two are reconciled by the operator, which retries
  forever. There is no Job in this layer, so there is nothing for a second
  boundary to protect.

One thing Phase 3 inherits rather than discovers: `monitoring.enablePodMonitor`
is `false` on the Cluster, matching the data tier, because the live pattern is a
hand-written PodMonitor in the observability layer. The one in
[`scrape/cloudnative-pg.yaml`](../../deploy/cluster/observability/config/scrape/cloudnative-pg.yaml)
selects `cnpg.io/cluster: aerie-pg` in namespace `aerie` and therefore does not
see this cluster. Widening it belongs with 3.5 and 3.8's monitoring work; until
then `immich-pg` has no metrics in Grafana.

---

## Phase 3 — Immich

**Goal:** Immich reachable at `photos.${DOMAIN}` from the LAN and the tailnet,
monitored, with an admin account and nothing in it yet.

**Gate:** a phone on the tailnet installs the app, signs in, and backs up one
photo, which appears in the web UI.

**Status:** deployed 2026-08-25 and green. `photos` went Ready 3m16s after the
reconcile was triggered; Helm install succeeded at 2m16s. Everything 3.8 can
check without a phone in hand has been checked and passes — the list is under
3.8. What is left is genuinely the operator's: **3.7's accounts, and the phone
half of 3.8's gate.**

The phase also found a design defect in Phase 1 and fixed it at the source
rather than working around it, which is 3.0 below and is the part worth reading
even if the rest is skimmed.

### 3.0 — The measurement that changed the design

Finding 2 asserted that `dataLocality: strict-local` plus `diskSelector: bulk`
would pull Immich's server pod onto the node holding the bulk disk "through the
volume", so that no node name and no node selector needed to exist anywhere.
That assertion was tested before the HelmRelease was written, because it is the
one claim in the plan that nothing else in the tree would have caught.

**It is false.** A throwaway pod with no scheduling constraints, mounting a
fresh `longhorn-bulk` PVC:

```
1 Pending/aerie-node-1      # ... and still Pending at 8 checks
volume pvc-28e2...  nodeID: aerie-node-1   diskSelector: [bulk]
                    state: detached        message: tags not fulfilled
pv     pvc-28e2...  nodeAffinity: kubernetes.io/hostname In [aerie-node-1]
```

The causality runs the opposite way to the one Finding 2 described. Under
`WaitForFirstConsumer` the **scheduler chooses first** — and it chooses with no
knowledge of Longhorn disk tags, because Longhorn's CSI driver advertises only
`kubernetes.io/hostname` topology and sets `storageCapacity: false`, so there is
nothing for the scheduler to filter on. Longhorn is then *told* which node to
put the single replica on, `diskSelector` matches no disk there, and the volume
is stuck. Worse, the PV is stamped with a hard `nodeAffinity` to the wrong node
on the way past, so nothing self-heals; the PVC has to be deleted along with the
`Retain`-policy PV it bound to.

`volumeBindingMode: Immediate` is not the fix either — tested, on a throwaway
class. It produces a PV with **no** `nodeAffinity` at all and a Longhorn replica
with no node assigned, which defers exactly the same failure to attach time and
loses `WaitForFirstConsumer`'s only advantage on the way.

**The fix is a node label, and a label is not the thing `ethos.md` forbids.**
What that document rules out is *this installation's node is called `k3s-2`*
appearing in the repo. `storage.aerie/bulk: "true"` is the same class of
structural fact the Longhorn disk tag already is — "the bulk disk was attached
here" — and it is written by the same script, in the same stage, from the same
argument, so the two cannot drift apart. The repo still names no node, and a
second installation that attaches its disk somewhere else needs no edit.

So [`Add-BulkDisk.ps1`](../../scripts/k3s/Add-BulkDisk.ps1) now does two things
in its Longhorn stage instead of one, and verifies both by reading them back:
the `node.longhorn.io` disk patch it always did, and
`kubectl label node <node> storage.aerie/bulk=true --overwrite`. The label was
also applied by hand to the node that already carries the disk, so a re-dispatch
of `provision-7-bulk-disk.yml` is a no-op rather than a first run.

Re-run of 1.8's gate afterwards, which is what closes both this and Phase 1:

```
1 Running/aerie-node-2
aerie-bulk-gate-ok
pvc-2a8b...-r-050d465b   aerie-node-2   /var/lib/longhorn-bulk   running
```

One replica, on the tagged disk, on the node the pod was steered to.

- [x] **3.1 — The Flux layer.** A second Kustomization in
      [`photos.yaml`](../../deploy/cluster/photos.yaml), named `photos` over
      `./deploy/cluster/photos/app`, beside the `photos-database` that Phase 2
      shipped early. `dependsOn: photos-database` and nothing else — which
      transitively is `infra-config` and nothing else, so the whole photo domain
      hangs off one edge into the platform and none into the family's own site
      tier. `wait: true`, `prune: true`, `postBuild.substituteFrom` the
      `aerie-cluster-config` ConfigMap, `timeout: 15m` — longer than the
      HelmRelease's own 10m, for the reason [`apps.yaml`](../../deploy/cluster/apps.yaml)
      gives about its own: Flux's wait covers applying the HelmRelease *and*
      waiting for helm-controller to finish with it.

      The root [`kustomization.yaml`](../../deploy/cluster/kustomization.yaml)
      already pointed at `photos.yaml`; only its comment changed.

- [x] **3.2 — The chart source.**
      [`helmrepository.yaml`](../../deploy/cluster/photos/app/helmrepository.yaml),
      `type: oci` against `oci://ghcr.io/immich-app/immich-charts` — the first
      OCI Helm source in this tree, and not by preference: the HTTP repo was
      removed in chart 0.13.0. Two differences from its classic siblings are
      written into the file, because they are invisible otherwise: the `url` is
      the registry *path* and source-controller appends the chart name from the
      HelmRelease, and `interval` polls no index because there is none.

- [x] **3.3 — The HelmRelease.**
      [`helmrelease.yaml`](../../deploy/cluster/photos/app/helmrelease.yaml),
      chart `immich` pinned `0.13.1` (verified current), release name `immich`,
      in the `immich` namespace alongside its HelmRepository rather than in
      flux-system — matching every other HelmRelease that installs a *workload*
      into the namespace it lives in. Everything the plan asked for is there;
      six things are worth knowing that the plan did not anticipate:

      - **The release name is load-bearing.** The chart names Services
        `<release>-<component>` and wires the components together with that same
        expression, but Immich's *own* built-in default for `machineLearning.urls`
        is the literal `http://immich-machine-learning:3003` with no release name
        in it. At release name `immich` all of them agree. At any other name the
        chart follows and Immich's default does not, and the symptom is smart
        search and face detection silently doing nothing while every pod reports
        healthy.
      - **`strategy: Recreate` on the server, replacing the chart's hardcoded
        `RollingUpdate`.** This is a correctness fix, not a preference: Immich
        runs its schema migrations at container start, so a rolling upgrade runs
        the new version's migrations underneath the still-serving old one, and
        with `maxSurge` both can hold the job queue. One replica, one migration,
        one writer.
      - **`strategy: Recreate` on machine-learning too**, for a plainer reason —
        its model cache is a ReadWriteOnce volume and that pod is *not* pinned to
        a node, so a rolling update that starts the replacement elsewhere
        deadlocks on multi-attach until something kills the old pod by hand.
      - **The ML cache is `ReadWriteOnce`, not the `ReadWriteMany` the chart's
        comment suggests.** RWX on Longhorn means a share-manager pod and an NFS
        export per volume; there is exactly one consumer, and `Recreate` above is
        what makes RWO safe across an upgrade.
      - **`immich.configuration` makes Administration > Settings read-only.**
        That is the GitOps answer and it is the right one, but it is not what
        somebody expects when they try to change the transcode preset from the
        web UI, so it is written at the top of that block. `backup.database`'s
        three fields are upstream defaults spelled out on purpose: Phase 4's
        schedule is chosen to start after this dump finishes, and an ordering
        that depends on an upstream default can change without a commit here.
      - **The chart's `values.schema.json` `$ref`s an absolute https URL** at
        `raw.githubusercontent.com` for its `common` section, and Helm resolves
        remote refs at render time — verified enforced, not merely present, by
        feeding it an invented key and watching it get rejected by name. So
        rendering this release reaches the public internet for something that is
        neither an image nor a chart, and an outage there presents as a
        schema-load error rather than a network one.

      Resource requests and limits are on all three containers. The server's are
      the interesting ones: since Immich merged microservices into it, that one
      container is the API, the web UI *and* the job runner, so ffmpeg lives
      there. Requests describe a house whose photos are already imported; limits
      describe an import.

- [x] **3.4 — Ingress.**
      [`ingress.yaml`](../../deploy/cluster/photos/app/ingress.yaml),
      `photos.${DOMAIN}`, `ingressClassName: traefik`, no `tls:` block, no auth
      middleware. The chart's own ingress stays disabled. Its
      `nginx.ingress.kubernetes.io/proxy-body-size: "0"` annotation is the trap
      the plan predicted and the file now names explicitly: there is no
      ingress-nginx here, Traefik applies no request body limit by default, and
      the wrong "fix" is a Traefik middleware that *introduces* a limit which did
      not previously exist.

- [x] **3.5 — Uptime monitor.** `photos.toml` in
      [`static-monitors-configmap.yaml`](../../deploy/cluster/observability/controllers/static-monitors-configmap.yaml),
      the same shape as its five neighbours, probing
      `http://immich-server.immich.svc:2283/api/server/ping` — the same endpoint
      the chart's liveness, readiness and startup probes all use, so Kuma and
      Kubernetes agree about what "up" means. Deliberately the in-cluster Service
      rather than the public name: a monitor that also traverses Traefik and the
      wildcard certificate reports red for three different reasons with one
      colour.

      **Also, and this is the loose end Phase 2 left here on purpose:**
      [`scrape/cloudnative-pg.yaml`](../../deploy/cluster/observability/config/scrape/cloudnative-pg.yaml)
      selected `cnpg.io/cluster: aerie-pg` in namespace `aerie`, so `immich-pg`
      had no metrics at all. Widened to `cnpg.io/cluster` **Exists** across
      `aerie` and `immich`, rather than copied into a second file — the failure
      mode of a per-cluster PodMonitor is a database nobody notices is
      unmonitored. Still an explicit namespace list, because a bare `Exists`
      everywhere would also scrape whatever a future tenant installs. The two
      Postgres alerts are scoped `namespace="aerie"` and are unaffected.

      Immich's own metrics need no object: `immich.metrics.enabled: true` makes
      the chart emit a ServiceMonitor, and kube-prometheus-stack's five
      `*SelectorNilUsesHelmValues: false` settings mean a ServiceMonitor in any
      namespace is scraped with no further wiring.

- [x] **3.6 — The Alpine DNS caveat.** Checked, and it does not apply: all three
      nodes report `search .` in `/etc/resolv.conf` — systemd-resolved's spelling
      of *no search domains* — so there is nothing for musl's resolver to expand
      badly and no `dnsConfig` is needed. Recorded rather than dropped, because
      the symptom (occasional 502s, ML timeouts) does not look like DNS, and the
      answer to "did anyone check?" should be yes-and-here-is-when.

- [ ] **3.7 — Create the admin account and the family's accounts.** The
      operator's, on first run, after this commit reaches `main`. Turn **off**
      public registration afterward.

      Note before doing it: Immich's config is a ConfigMap now (3.3), so the
      admin UI's Settings page is read-only. Account creation is not affected —
      users are database rows, not configuration — but a setting that needs
      changing is a commit against `helmrelease.yaml`, not a click.

- [ ] **3.8 — Gate.** Everything but the phone passed on the first apply,
      2026-08-25:

      - `photos-database` and `photos` both **Ready** on
        `main@sha1:2ef08ea`; `Helm install succeeded for release immich/immich.v1
        with chart immich@0.13.1`.
      - **The node label did its job.** `immich-server` scheduled on the node
        holding the bulk disk without anything naming it; `immich-library` bound
        `1Ti` on `longhorn-bulk` and the Longhorn volume reports `attached`,
        `healthy`, `strict-local`. `valkey` landed on the same node by
        coincidence, `machine-learning` on another — which is correct, since only
        the server has the constraint.
      - `curl https://photos.${DOMAIN}/api/server/ping` through the ingress VIP
        returns **200 `{"res":"pong"}`** on the wildcard cert, with no `tls:`
        block anywhere in the layer.
      - **Prometheus: four CNPG instances scraped, not three.** The widened
        PodMonitor picked up `immich-pg-1` in `immich` while keeping all three
        `aerie-pg` pods — the check that matters, since the risk of widening a
        selector is losing what it already had. Both Immich ServiceMonitor
        endpoints (8081 api, 8082 microservices) are `up`.
      - AutoKuma: `Creating new http: photos`.
      - The measured image sizes were right: the ML pull reported
        451,974,162 bytes against the ~452 MB predicted.

      **Still outstanding, and it is the actual gate:** a phone on the tailnet
      installs the app, signs in, and backs up one photo, which appears in the
      web UI.

      What was verified without deploying, so that the gate is checking the
      things a dry run cannot: every directory under `deploy/` builds; the two
      new `${...}` tokens are declared in
      [`cluster-config.json`](../../scripts/k3s/cluster-config.json); the four
      objects of the app layer pass `kubectl apply --dry-run=server` against the
      live cluster; the chart renders from the HelmRelease's own values with the
      intended `nodeSelector`, strategies, resources, mounts (`/data` for the
      library, which is where Immich v2+ expects its media root) and secret
      references; and the storage-template string survives `kustomize build`
      unquoted-into-nonsense.

      Sizing note for whoever watches the first reconcile: measured from the
      registry at `v3.0.0`, `immich-server` is **~798 MB** compressed and
      `immich-machine-learning` **~452 MB**. An earlier draft of this plan had
      those the other way round. Both land on a cold node before any probe
      succeeds, which is what the 10m Helm timeout and the 15m Kustomization
      timeout are sized against.

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
      - **`nodeSelector: {storage.aerie/bulk: "true"}`** — the same one Phase
        3.0 put on the `immich-server` pod, and for the same reason. This is
        load-bearing and non-obvious: the PVC is `ReadWriteOnce` and
        `strict-local`, so the volume is attachable on exactly one node, and a
        CronJob scheduled anywhere else sits failing to attach forever. RWO
        permits multiple pods on the *same* node, so this and the server can
        both hold it.

        The plan originally called for `podAffinity` against the
        `immich-server` pod's labels here. The node label is strictly better
        now that it exists: it selects on the fact that actually matters (this
        node has the bulk disk) rather than on a proxy for it, it does not
        break if the server pod is temporarily absent when the CronJob fires,
        and it keeps the operator's node name out of the repo just as well.
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

**Built, 2026-08-25.** The photo frame: a `Photos` module in `Aerie.Api`, a
Photos page in the admin app, and a carousel on the kiosk dashboard directly
below the room cards.

Three of the four points scaffolded below survived contact; the first did not,
and the difference is worth keeping.

### What was built

1. **The selection is a checkbox in Aerie, not a tag in Immich.** The scaffold
   preferred an Immich tag `kiosk` because tags survive renames. What that
   actually asks is for the operator to configure the wall *in Immich* — a
   second place to look, a convention nothing enforces, and a `Refresh` that
   cannot tell "untagged" from "never tagged". A `photos.Albums` row per album
   with an `Included` flag is the same shape the family calendar already uses
   (`EfCalendar.Included`), and it settles the rename problem better than tags
   do: the row keys on Immich's album id, so a rename is a name change on a row
   that keeps its choice. Immich owns what an album is, Aerie owns what the wall
   does with it, and a refresh never crosses that line.
2. **An Immich API key with three permissions**, stored the way admin-entered
   credentials are stored —
   `SiteSettings` through `SecretProtector`, redacted on read, alongside
   `ImmichBaseUrl`. Not SSM, and this is the correction to the scaffold: SSM and
   ExternalSecrets are for credentials a *pod* needs at startup, and this is one
   an operator pastes into a form after the pod is running, the way the Home
   Assistant token and the Google client secret already are
   ([`secrets-architecture.md`](../secrets-architecture.md) is about the first
   kind; this is the second). Nothing about Immich reaches the repo either way.

   The permissions are `album.read`, `asset.read`, `asset.view`, and optionally
   `server.about`. `asset.view` rather than `asset.download` is the one worth
   noticing: the first reaches Immich's generated renditions and the second
   reaches originals, and a photo frame has no business holding a key that can
   pull a 40 MB raw file. `asset.read` is what lets Aerie ask *which* photos an
   album holds — Immich 3.0 took the assets off the album response, so that
   question is now a search (see the note under **What Immich 3 moved** below).
   The optional fourth is only a version string — the status check falls through
   to `GET /api/albums` when it is refused, so a key scoped to the three that
   matter reads as connected rather than as broken.
3. **A `Photos` module in `Aerie.Api`** — a folder under `Modules/`, one line in
   the registry, one table, no infrastructure. It proxies rather than exposes,
   exactly as scaffolded: the kiosk asks Aerie for a manifest of asset ids and
   then for those ids' bytes, so the key stays server-side and the dashboard
   needs no second origin, no second auth and no CORS story.
4. **A dashboard carousel** that cross-fades, captions with album, place and
   month, and names no color the circadian phase did not supply — so it dims
   with the wall and the veil passes over it, which is what
   [`kiosk_brightness.md`](kiosk_brightness.md) asks of anything that lights a
   dark hallway at 3am.

### The two decisions that were not obvious

**The photos are a cache, not a table.** A row per asset would be a second copy
of a library that already has a database, plus a job to keep the copy honest,
plus a class of bug where the two disagree about a photo. What the wall needs is
"which asset ids may I show", which is one Immich call per included album.
`PhotoLibrary` holds that in memory: the Immich fetch behind a 15-minute TTL,
the selection re-read from Postgres every 10 seconds so another replica's toggle
carries across without an invalidation message between processes. A failed
rebuild keeps the last good deck — a frame showing quarter-hour-old photos is
not a bug, a frame going black because Immich restarted is.

**That cache's asset-id set is also the authorization.**
`GET /api/photos/assets/{id}/image` proxies an id only if an included album
holds it. Without that check the endpoint is a hole straight through to every
photo in the house for anything that reaches the kiosk's origin, and asset ids
are exactly the kind of thing that leaks — into listings, backups, browser
history. Originals are unreachable by construction: the client knows two
rendition names (`preview`, `thumbnail`) and refuses anything else before making
a request, so no path through this module can pull a 40 MB raw file.

### What Immich 3 moved

The first version of this read an album's photos off `GET /api/albums/{id}`,
whose response embedded its assets. Immich 3.0 removed that property, and the
way it removed it is the part worth recording: the call still answers `200`, the
album still carries its name, its cover and its `assetCount` — there is simply
no `assets` array on it any more. So nothing failed. The admin page listed four
albums with the right counts and the right covers, ticking one saved and stuck,
and "On the kiosk" said *0 photos in the selection* with no error beside it,
because there had been no error: Immich answered, and the answer contained no
photos. The log line for it read `Photo library rebuilt: 0 photos from 1
albums`, which is exactly what happened and reads like a configuration mistake.

The photos now come from `POST /api/search/metadata` — `albumIds`, `type: IMAGE`
so Immich drops the videos rather than Aerie spending page budget on them, and
`withExif` for the city and country the carousel captions with. That is Immich's
own documented replacement, and it is also the *older* spelling of the question:
search predates the removal by years, so the single path serves an Immich 2 and
an Immich 3 alike, with no version sniffing anywhere in the client. It pages at
1000 where the embedded array had no page at all, so the read walks `nextPage`
and stops at whichever comes first — the last page, or the 2000-per-album
ceiling that keeps someone's 60,000-asset "All photos" from being pulled into
every replica's memory.

The cost of the move is one more permission on the API key: search is
`asset.read`, where listing albums is `album.read` and fetching a rendition is
`asset.view`. A key minted against the old instructions lists albums fine and
then cannot read one, which surfaces on the admin page as "Immich refused that
API key" over the carousel — so the setup text names all three.

### What it cost the MVP

Nothing structural, which was the point of scaffolding it. The proxy works
because Immich has a real API and the kiosk already talks to `Aerie.Api`; the
only MVP decision that could have broken it — putting Immich somewhere the API
cannot reach — is one this plan deliberately did not make.

The surface is in
[`dashboard-api-manifest.md`](../dashboard-api-manifest.md#photos), including
why `/api/photos` is the third documented exception to "read paths hit Postgres,
jobs talk to the outside world".

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

Six files outside the new `photos` tree are expected to change. Five by
addition only; the sixth is the exception this list exists to surface rather
than hide, and it is called out in its own row:

| File | Change |
|---|---|
| [`namespaces.yaml`](../../deploy/cluster/infrastructure/config/namespaces.yaml) | One `immich` Namespace (1.6) |
| [`longhorn-storageclasses.yaml`](../../deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml) | One `longhorn-bulk` class (1.5) |
| [`kustomization.yaml`](../../deploy/cluster/kustomization.yaml) | One `photos.yaml` entry (3.1) |
| [`parameters.json`](../../scripts/secrets/parameters.json) | Three new parameters, plus a second target on three existing ones (2.5, 4.3) |
| [`static-monitors-configmap.yaml`](../../deploy/cluster/observability/controllers/static-monitors-configmap.yaml) | One `photos.toml` monitor (3.5) |
| [`scrape/cloudnative-pg.yaml`](../../deploy/cluster/observability/config/scrape/cloudnative-pg.yaml) | **Not an addition — an edit.** Its selector was `cnpg.io/cluster: aerie-pg` in namespace `aerie`, which is a shape that cannot see a second CNPG cluster; widened to `Exists` across `aerie` and `immich` (3.5). Not a seam gap: the file was written when one Postgres existed, and the alternative — a second near-identical PodMonitor — is how a database ends up unmonitored because nobody copied the file. Phase 2 predicted this edit and deferred it here. |

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
