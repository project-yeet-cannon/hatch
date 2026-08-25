# Node storage — the volume etcd lives on, and the disk that is filling

**Status:** Phase 1 tooling built (1.2, 1.3); **one node migrated, 1130 ms ->
14 ms** (see 1.6). Two to go. Two
symptoms, one cause: every node's OS disk is a *dynamic* VHDX, on the *slow*
volume, sized from a template rather than from the workload. Three phases. Phase 1 stops the alerts and is the only one with a
maintenance window; Phase 2 stops the root filesystem from filling again; Phase 3
stops the next node built from reintroducing both.

Everything below was measured against the live cluster on **2026-08-24**. The
per-host numbers are observations of one installation, not facts about Aerie —
[`ethos.md`](../ethos.md) — so hosts appear here as **A**, **B** and **C**,
identified by what they measure rather than by name. Which physical host is which
belongs in the operator's own runbook, the same way
[`immich.md`](immich.md)'s bulk disk records its host.

## The brief, as decisions

| Question | Answer |
|---|---|
| What is actually wrong | **Disk latency, not etcd.** The DB is 64 MB and takes 4.6 commits/s |
| Fix or tune the alert | **Fix.** The thresholds are correct and the cluster has no margin |
| Where the OS disk goes | **The fastest volume with room, measured per host.** The boot volume on two of three; on the third that volume is too small, so its OS disk goes to a third volume instead |
| Dynamic or fixed VHDX | **Fixed** — *and* automatic checkpoints off. A fixed disk under an automatic checkpoint is a dynamic differencing disk again (finding 7) |
| Root disk size | **100 GB** on all three, up from the template's 32 GB. Capacity is not the constraint on any host once the destination is chosen correctly |
| Bigger disk alone | **No.** Without bounding image accumulation, a bigger disk is a bigger treadmill |
| Longhorn data disk | **Stays where it is.** 200 GB fixed, on the bulk volume. Nothing here touches it |
| Downtime | **One node at a time**, ~15–25 min each, less for the one that only converts. Quorum is 2 of 3 throughout |

## Findings

Seven, from the live cluster and from reading the tree. The first three decide
Phase 1; the next two decide Phase 2; the sixth decides the order; the seventh
was found by the tooling in 1.2 on its first run and partly reopens the first
three.

### 1. The alerts are a disk-latency alarm, and they are telling the truth

`etcdHighCommitDurations` and `etcdHighFsyncDurations` are kube-prometheus-stack's
own rules, not
[`cluster.yaml`](../../deploy/cluster/observability/config/alerts/cluster.yaml)'s.
They fire on the p99 of etcd's write-ahead-log `fsync` and backend commit, at
250 ms / 500 ms (warning) and 1 s (critical). etcd `fsync`s the raft log before
acknowledging any write, so every Kubernetes API write blocks on that number, and
past the raft election timeout it stops being slowness and becomes leader churn.

Against a healthy target of **under 10 ms**:

| node | wal fsync p99 | backend commit p99 | guest avg write latency |
|---|---|---|---|
| node on host A | 243 ms | 371 ms | 31 ms |
| node on host B | 204 ms | 244 ms | 18 ms |
| node on host C | **1130 ms** | **1526 ms** | **98 ms** |

Two things this is *not*. It is not load: the DB is 64 MB (26 MB in use) taking
4.6 commits/s, and 0 leader changes in the last hour. And it is not new — seven
days of history has hosts A and B at 130–460 ms continuously, sitting just under
the fsync warning line while the tighter commit rule tripped. The node on host C
jumped to a flat ~1000 ms the hour it was rebuilt and has never come back down.

The cost is already visible without the alerts: all three nodes log **95k–177k
`apply request took too long` lines per day**, which is etcd narrating this
finding into a journal that is itself on the same slow disk.

### 2. Every host already owns a volume 3–50× faster, and it is nearly idle

The VMs live under `D:\aerie\VMs\` ([`scripts/hyperv/README.md`](../../scripts/hyperv/README.md)),
on a volume that is not the host's boot volume. Per-physical-disk latency from
windows_exporter, averaged over 7 days:

| host | VM volume (write / read) | boot volume (write / read) | boot volume free |
|---|---|---|---|
| A | 6.8 ms / 2.1 ms | 1.80 ms / 0.45 ms | ~880 GB |
| B | 5.7 ms / 3.1 ms | 0.10 ms / 0.78 ms | ~885 GB |
| C | **49 ms** (its VM is here); a third volume measures **1.81 ms**, ~822 GB free | 0.11 ms | ~106 GB |

Hosts A and B's VM volumes are not spinning disks — 2–3 ms reads rule that out
— but at 5.7–6.8 ms per write under etcd's flush-heavy pattern they are still
30–60× their own boot volumes, which take 3–4 writes/s and are otherwise doing
nothing.

**Host C is a different shape, and this row went through two wrong readings
before it settled.** It carries three volumes, not two: a 237 GB boot volume at
0.11 ms with only ~106 GB free, a 931 GB volume at **49 ms** with ~705 GB free,
and a 931 GB volume at **1.81 ms** with ~822 GB free. Its VM lives on the 49 ms
one. The boot volume is out on size — 100 GB fixed plus the 40 GB margin does
not fit in 106 GB — so host C's destination is its third volume, and it is the
only one of the three hosts whose OS disk does not end up on C:.

Both wrong readings are worth recording, because the same two mistakes are
available to the next person. The first assumed the documented `D:\aerie\VMs`
default without checking, and measured a volume the VM was not on. The second
took "it is already on an SSD" at face value and concluded no move was needed.

What settles it is a comparison rather than a datasheet. All three hosts' VM
volumes carry the same thing right now — a dynamic parent under a differencing
`.avhdx` under an etcd node, per finding 7 — and two of them measure 7.7 and
10.3 ms. Host C's measures 49. Same workload shape, five times the latency,
which is a statement about the media and not about the VHDX.

It is not proof: host C's node is the freshest rebuild and so does the most
block allocation per write, which is finding 3's mechanism and would push the
same number up on its own. But the cost of being wrong is asymmetric — a wrong
"convert in place" leaves the node on the slow volume and buys a second
maintenance window on an etcd member — and the 1.81 ms volume has 822 GB free
and sits in the same band as host A's boot volume at 2.20 ms. There is nothing
to trade away by choosing it.

The claim to hold this to is not "SSDs are faster". It is that **the fastest
storage in each of these three machines is idle, and the one workload in the
house that is latency-bound is not on it** — and that which volume that is has
to be measured per host, not inferred from a default path or from a label.

### 3. The OS disk is dynamic, which is both a second penalty and a standing hazard

[`Get-GoldenImage.ps1`](../../scripts/hyperv/Get-GoldenImage.ps1) builds the
template with `qemu-img convert -o subformat=dynamic`, and
[`New-AerieVM.ps1`](../../scripts/hyperv/New-AerieVM.ps1) copies it whole as the
OS disk — so `/`, and with it `/var/lib/rancher/k3s/server/db`, is a dynamic
VHDX. Note the asymmetry the same script already got right: the Longhorn data
disk beside it is created `-Fixed`.

Dynamic costs twice. Every write to a not-yet-allocated block makes Hyper-V
expand the file before the guest's write completes, which is why the *freshest*
node — 47% of its root filesystem allocated, against 82–84% on its siblings — is
the one at 1130 ms rather than the one with the most data. And a dynamic disk can
grow into whatever volume it sits on, which is exactly the hazard
[`immich.md`](immich.md) already names for the bulk disk. Moving the OS disk onto
a host's boot volume without also making it fixed would move that hazard onto the
one volume where it takes Windows down with it.

### 4. The root filesystem is not "nearly full" — it is filling, at 4–5 points a day

This is the finding that reorders the plan's urgency. Root filesystem used, daily:

| node | 08/20 | 08/21 | 08/22 | 08/23 | 08/24 |
|---|---|---|---|---|---|
| host A | 59% | 68% | 76% | 79% | **83%** |
| host B | 69% | 73% | 77% | 82% | **84%** |
| host C | — | — | — | 20% | **42%** (rebuilt 08/23) |

kubelet's image GC high threshold is 85% and its hard eviction threshold is 10%
free. At the observed slope, the two older nodes reach the first **within a day**
and the second inside two. `DiskPressure` is `False` right now, which is the last
moment at which this is a planned change rather than an incident.

What happens at 85% is not a crash — GC evicts images and holds the line. But the
steady state it holds is "permanently at the GC threshold, re-pulling evicted
images over the slow disk", which is a worse place to live than it sounds and
turns every deploy into a race.

### 5. What is filling it is image accumulation, so capacity alone is not the fix

Of 25 GB used, containerd is **20–21 GB** (15 GB of overlayfs snapshots, 5.6 GB of
content), journald is **1.3–1.9 GB** uncapped, and everything else — the OS,
`/usr`, k3s itself, etcd — is under 2 GB combined. There are **124 images** on a
node, and the mechanism is visible in them: one first-party image carries **five
tags of the same 174 MB layer set**, one per CI build, none ever removed.

So there are two independent leaks, and a 100 GB disk fixes neither — it buys
about three weeks. The disk needs to be bigger *and* the growth needs a ceiling
that is not "85% of whatever the disk happens to be".

The ceiling wants to be kubelet's own, moved down rather than reinvented: with
GC at 70/55 on a 100 GB disk, the node trims itself at 70 GB — twice the projected
steady state — routinely and with no eviction risk, instead of emergency-trimming
at 85% of 32 GB. `/etc/rancher/k3s/config.yaml` is already a file
[`Install-K3sNode.ps1`](../../scripts/k3s/Install-K3sNode.ps1) writes and
reconciles, with an established "apply, restart k3s only if the value changed"
path from the `etcd-expose-metrics` work. `kubelet-arg` goes in beside it; nothing
new is invented.

**Sizing the disk.** Today's steady state is ~25 GB. Immich adds a server image,
a machine-learning image and its model cache — call it 5 GB — for ~32 GB
projected. 100 GB is that at 3×, and it is a modest ask against ~880 GB free on
two of the three boot volumes. The binding constraint is not capacity in general;
it is **host C's boot volume, with 106 GB free**, and that is finding 6.

### 6. The constrained host's constraint does not bind, because it is not in the way

Host C is the odd one in every table above: the smallest boot volume, the worst
etcd latency, and — per the cutover — the machine that used to run everything as
Docker Compose before it became a node. 131 GB of its 237 GB boot volume is in
use on a machine whose only job now is to run one VM, which looks like the
residue of that former life.

**It does not matter, and auditing it would have been wasted work.** That boot
volume is 237 GB in total; a 100 GB fixed disk plus the 40 GB margin needs 140,
and freeing 34 GB of Compose-era images to fit 140 GB into 237 GB leaves a
Windows boot volume with almost nothing spare. It is the wrong destination on
size grounds before it is the wrong destination on any other.

So host C's node is a move like the other two, just to its third volume rather
than to its boot volume, per finding 2. Every node in Phase 1 runs the same
procedure to a different address — a simpler plan than this section originally
described, and one that does not need the audit it originally called for.

What is left of the ordering question is disk pressure, not risk. Host C's node
is at 48% used against 83–84% on the other two, which makes it both the safest
node to drain *onto* and, once it has a 100 GB disk, the biggest sink for the
two drains that follow it. Doing it first lowers the peak root-filesystem usage
on the two nodes that are one point from kubelet's image-GC threshold; doing it
last, as this plan originally said, raises it.

### 7. There is a third dynamic layer, and it is a Hyper-V default

Found by [`Move-NodeOsDisk.ps1`](../../scripts/hyperv/Move-NodeOsDisk.ps1)'s
preflight on its first run, refusing to touch a node it did not understand.
`aerie-node-1` carries an **automatic checkpoint taken 2026-08-09** — over two
weeks before this plan was written — so its OS disk is not `os-disk.vhdx` at
all. It is `os-disk_99EC2787-….avhdx`, a *dynamic differencing disk*, and every
guest write since 9 August has gone through it.

Automatic checkpoints are a Hyper-V default, not something anyone chose here.
One is created when a VM starts and removed when it shuts down cleanly, so any
node with a long uptime accumulates exactly this. What it costs is finding 3
again, one layer up and worse: a differencing disk copies on write at block
granularity, so a write to a block not yet in the `.avhdx` is a read from the
parent, an allocation, and a write — while the parent is itself dynamic and may
need to expand too.

Two consequences, and the second is the one that would have ruined this plan
quietly.

**Findings 1 and 3's numbers are measured through this layer.** How much of
243/204/1130 ms is the volume, how much is the dynamic parent, and how much is
the differencing child is not separable from the data collected on 08/24. The
direction of every conclusion here survives — all three mechanisms are
allocation-on-write, and one change removes all three — but the attribution in
finding 3 does not, and neither does its explanation that the *freshest* node
is worst *because* it is freshest. Node 0 was rebuilt on 08/23 and would have
taken its own automatic checkpoint at that boot.

**A fixed VHDX underneath an automatic checkpoint is a dynamic differencing
disk again.** Had the setting been left on, every node would have come back
from Phase 1, started, immediately reacquired an `.avhdx`, and then been
measured against 1.6's gate through it. The plan would have been executed
correctly and delivered a fraction of what it claims, with nothing in the
output to say so. That is why the script disables the setting on every VM it
touches rather than reporting it: it is a precondition, not a tidy-up.

### 7a. Postscript: merging a chain against a live node takes the node down

The first version of `Move-NodeOsDisk.ps1` merged the checkpoint chain in
preflight, against a running node, before the drain. That was wrong, and the
second node's migration is how it was found out.

Merging rewrites every block the `.avhdx` holds. On these hosts the VM volume
backs *both* the node's OS disk and its 200 GB Longhorn data disk, so a merge
saturates the volume the guest is living on. What followed, in order: the guest
went to 53% iowait with ext4 journal threads blocked in `D` state, kubelet
stalled, pods hung in `Terminating`, Longhorn's instance-manager was killed,
its replicas stopped, the node was marked down, and Longhorn began rebuilding
the stale replicas — onto the same saturated disk. Then the load shifted and
the *other* unmigrated node did the same thing, and briefly reported
`Ready=Unknown`, taking the cluster to bare etcd quorum.

Nothing was lost. Every Longhorn volume in this cluster is in `observability`,
and the one that reached `faulted` was Prometheus' own history; the application
database is on `local-path` and kept 2 of 3 instances throughout. But the
cluster was one node away from a read-only API server, on a maintenance
operation that was supposed to be routine.

Three things this establishes, beyond the ordering fix:

**The merge belongs after `Stop-VM`.** With the VM off there is no guest to
starve, the node is already down for the conversion that follows, and the merge
is faster for having no concurrent writes. This costs nothing and was available
from the start.

**The two unmigrated nodes have no headroom for *any* extra I/O.** At 82-85%
root on a dynamic VHDX under a differencing disk, they do not absorb a
disturbance — they amplify it into a cluster-wide event. That is finding 4 and
finding 5 arriving as an incident rather than as a graph, and it raises the
priority of Phase 2 relative to the rest of Phase 1.

**The Longhorn gate in 1.2 is what stopped this being worse.** It refused to
drain into a degraded cluster, which is the only reason the second node was not
also drained and powered off in the middle of its own storm.

## Phase 1 — the OS disk moves to the fast volume, fixed, at 100 GB

One node at a time, the same operation on each — move to the fastest volume
with room on that host, and convert to fixed — but to a *different volume per
host*, and on one of the three that is not the boot volume (findings 2 and 6).
The script reports which volume it read and which it is writing before it
drains anything; the operator's only job is to name the right one.

Quorum is 2 of 3, so a single node down is survivable; it is
also long enough to trip [`cluster.yaml`](../../deploy/cluster/observability/config/alerts/cluster.yaml)'s
`EtcdMemberDown` at `for: 15m`, which is expected and is what the silence in 1.1
is for.

- [ ] **1.1 — Window and silence.** Silence `alertname=~"etcd.*"` and
      `alertname="EtcdMemberDown"` for the duration, by alertname and never by
      receiver — the discipline
      [`kube-prometheus-stack.yaml`](../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml)
      already documents. Record the pre-change fsync p99 per node so 1.6 has
      something to compare against.

      Also sweep all three hosts for finding 7 before starting, because it
      changes what 1.6's numbers mean:

      ```powershell
      Get-VM | Select-Object Name, State, Uptime, AutomaticCheckpointsEnabled
      Get-VM | Get-VMSnapshot | Select-Object VMName, Name, SnapshotType, CreationTime
      Get-VM | Get-VMHardDiskDrive | Select-Object VMName, Path
      ```

      An `.avhdx` in that last list is a node running on a differencing disk.
      `provision-8`'s `preflight_only` reports the same per node without
      changing anything.

- [x] **1.2 — `scripts/hyperv/Move-NodeOsDisk.ps1`.** Modelled on
      [`Initialize-NodeStorage.ps1`](../../scripts/k3s/Initialize-NodeStorage.ps1)
      — same SSH mechanics, same refuse-on-surprise posture. Parameters:
      `-VMName`, `-IPAddress`, `-DestinationPath`, `-SizeGB` (default 100),
      `-Username`. What it does, in order:

      1. `kubectl drain` the node (`--ignore-daemonsets --delete-emptydir-data`),
         then `Stop-VM` and wait for `Off`.
      2. `Resize-VHD -SizeBytes` on the existing dynamic file — instant, and
         cheap to undo.
      3. `Convert-VHD -DestinationPath <fast volume> -VHDType Fixed` — one pass
         that both converts and relocates. This is the long step: it writes
         `-SizeGB` of zeroes plus the data.
      4. `Set-VMHardDiskDrive` to repoint the VM at the new file.
      5. `Start-VM`, wait for SSH, then `growpart /dev/sda 1` and
         `resize2fs /dev/sda1`. The partition table cooperates: `sda1` is the
         last partition by LBA on every node, with the BIOS-boot and ESP
         partitions ahead of it, so this is an online grow with no repartition.
      6. Assert the new size, assert etcd is healthy and has rejoined, then
         `kubectl uncordon`.

      **Hard refusals**, in the spirit of the storage scripts already in the
      tree: never run against a VM whose node is the only healthy etcd member;
      never delete the source VHDX (1.9 does that, after a soak); and never
      target a destination volume with less than `-SizeGB` + 40 GB free, so a
      fixed disk cannot be the reason a Windows boot volume fills.

      **As built**, three things the sketch above did not have. The etcd
      refusal is stated as *quorum* rather than as "the only member" — it asks
      every member's own `/health` with k3s's client cert and refuses unless a
      majority of the membership would still stand with this node off, which
      is the condition that actually matters and is not waived by `-Force`.
      The drain, the readiness wait and the uncordon are issued from a *peer*
      node discovered from the target's own `kubectl get nodes`, since the
      target cannot answer for itself while it is off. And the disk is
      identified twice independently before anything is converted: by the
      VM's firmware boot order on the host, and by the guest's root disk being
      the size that VHDX says — a node's 200 GB Longhorn disk fails the second
      test.

      It also converts in place — same volume, no move — when the destination
      it is given is the directory the disk is already in, reported as such in
      preflight. Phase 1 does not end up using that path on any of the three
      nodes, but it is what makes "name the volume you want" a complete
      instruction rather than one with an unstated exception.

      Finding 7 is its work: it refuses a checkpoint chain rather than
      converting one, merges it on `-MergeCheckpoints` and waits for the
      merge to finish, and disables automatic checkpoints on any VM it
      touches. That last one is unconditional and is not a tidy-up — see
      finding 7 for what leaving it on would have cost.

      It also carries 1.9 and its undo as modes rather than as runbook lines,
      because both act on a file nothing else on the host now references:
      `-RemoveSourceDisk` and `-Rollback` both read an `os-disk-migration.json`
      written beside the new disk *before* the first boot off it, which names
      the source, the controller slot, and the pre-change fsync p99 from 1.1.

- [x] **1.3 — The workflow.**
      [`provision-8-move-os-disk.yml`](../../.github/workflows/provision-8-move-os-disk.yml)
      — 6 and 7 went to the backup and bulk-disk steps while this was being
      written — `runs-on: [self-hosted, '${{ inputs.host }}']` with the same
      `hyperv-host-*` choice list every other provision workflow carries.
      Inputs: `vm_name`, `ip_address`, `mode`, `destination_path`, `size_gb`
      (default `100`), `force`, `preflight_only`. The manual path stays as the
      fallback, as in
      [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md)'s Flow A.

      Its `concurrency` group is keyed on the *workflow*, not on the VM, which
      is the one place it differs from Provision 5 and 7. Two runs against
      different nodes are exactly as dangerous as two against the same one:
      each takes an etcd member down, and three members survive one.

- [ ] **1.4 — First node, on a roomy host.** Run it against one of the two hosts
      with ~880 GB free. This is the proving run: nothing about it is
      space-constrained, so a failure here is a failure of the procedure and
      nothing else.

      Pass `merge_checkpoints` where 1.1's sweep found one. **The merge runs
      after the drain, with the VM off** — see finding 7's postscript for why
      that ordering is not a detail.

      **Not the first node run.** Host C's node went first, for finding 6's
      reasons: roomiest destination (~822 GB free), least full node at 48% and
      so the safest to drain onto, and once migrated a 100 GB sink for the two
      drains that follow. So the order executed was 1.8, then these two. The
      step numbers name nodes, not sequence.

- [ ] **1.5 — Second node, other roomy host.** Only after 1.4's node has been
      `Ready` and serving for long enough to trust.

- [ ] **1.6 — Gate: the number moved.** *One of two nodes measured; see the
      result below.* p99 wal fsync on both migrated nodes
      **under 10 ms**, sustained over an hour, off the live Prometheus. If it
      lands in the tens of ms rather than single digits, stop and find out why
      before touching the third node — the whole plan rests on the boot volume
      being as fast under etcd's flush pattern as it measures under the host's.

      **Result on the first node: 1130 ms -> 14.1 ms p99, and 1.7 ms p50.**
      Backend commit 1526 ms -> 13.3 ms. Slow-apply warnings 0.32/s against
      1.05-1.21/s on the two unmigrated nodes, and zero leader changes. Stable
      to within 0.2 ms across 5-, 15- and 30-minute windows, so this is the
      steady state and not post-boot catch-up.

      **It misses the number and passes the question.** The gate asked whether
      the destination volume is as fast under etcd's flush pattern as it
      measures under the host's, and p50 of 1.7 ms against that volume's
      measured 1.81 ms says it is, exactly. There is no overhead left to
      remove; a p99 at eight times the median is ordinary filesystem tail. The
      10 ms figure was written against destinations measuring 0.1-2.2 ms and
      this node's is the slowest of the three at 1.81 ms, with no better option
      on that host — its boot volume cannot hold the disk (finding 6).

      What is left open is narrow and the next node closes it for free: if a
      node landing on a 0.11 ms volume comes in under 10 ms, this node is
      media-limited and the procedure is sound. If it also lands near 14 ms,
      something common is in the way and *that* is the stop this gate is for.

      For scale: the alerts that started this fire at 250 ms fsync, 500 ms
      commit, 1 s critical. Nothing is near them at 14 ms.

- [x] **1.7 — ~~Reclaim host C's boot volume.~~ Dropped, per finding 6.** The
      audit was scoped against a destination host C should not use: 140 GB into
      a 237 GB Windows boot volume is the wrong answer however much of it is
      Compose-era residue. Its node stays on the 1.85 ms volume it is already
      on, which has ~822 GB free, and no space needs reclaiming for this plan.
      Whether 131 GB of a hypervisor-only machine's boot volume is worth
      cleaning up on its own merits is a separate and much smaller question.

- [x] **1.8 — Third node, run first.** The same move the other two get, to
      host C's third volume rather than to its boot volume — 1.81 ms, ~822 GB free, in the
      same band as host A's boot volume. Finding 2 for why not C:, and why not
      the volume it is on now.

      **Done. 1130 ms -> 14.1 ms p99, 1.7 ms p50** — the largest single
      improvement in the plan, and the whole of it in one 15-minute window.
      Root filesystem went 32 GB at 48% to 99 GB at 16%. Full reading in 1.6.

      Because this run both moved and converted, it cannot apportion the win
      between findings 2 and 3. The experiment that would have separated them
      was an in-place conversion on the volume this node was already on; it was
      given up deliberately, because it would have risked a second maintenance
      window on an etcd member to learn something the plan does not act on.

- [ ] **1.9 — Soak, then delete the sources.** Leave the original
      `os-disk.vhdx` files in place for a week as the rollback path — repointing
      `Set-VMHardDiskDrive` back is a one-line undo for as long as they exist.
      Delete them after, and only then; this is the step that is easy to skip
      and expensive to skip in the other direction.

      Both halves are `Move-NodeOsDisk.ps1` modes rather than runbook lines:
      `-Rollback` for the undo, `-RemoveSourceDisk` for the deletion, and the
      latter refuses inside the soak, refuses while the node is not Ready, and
      refuses while any etcd member is unhealthy. Both read the
      `os-disk-migration.json` written beside the new disk *before* its first
      boot, which is the only thing on the host that still names the source.

      On the node converted in place, the surviving disk is called
      `os-disk-fixed.vhdx` rather than `os-disk.vhdx` — `Convert-VHD` cannot
      write the file it reads, so the two had to have different names in one
      directory. The sidecar records both. Renaming it back would mean another
      window for no benefit.

## Phase 2 — bound what grows, so the bigger disk stays big

Independent of Phase 1 and safe to land first. Neither step needs a window.

- [ ] **2.1 — kubelet image GC, moved down.** Add to
      `/etc/rancher/k3s/config.yaml` via
      [`Install-K3sNode.ps1`](../../scripts/k3s/Install-K3sNode.ps1)'s existing
      config-reconcile path, reusing its "restart k3s only if the value changed"
      behaviour:

      ```yaml
      kubelet-arg:
        - image-gc-high-threshold=70
        - image-gc-low-threshold=55
      ```

      On a 100 GB disk that trims at 70 GB against a ~32 GB projected steady
      state — routine housekeeping with a wide margin, rather than the current
      arrangement where the first GC of a node's life happens under pressure.
      The values are structural (a ratio, not an operator's number); the disk
      size they are chosen against is 1.2's `-SizeGB`.

- [ ] **2.2 — Cap journald.** `SystemMaxUse=512M` in a
      `/etc/systemd/journald.conf.d/` drop-in, placed by
      [`cloud-init/user-data.tmpl.yaml`](../../scripts/hyperv/cloud-init/user-data.tmpl.yaml)
      for new nodes and applied in place on the existing three. 1.3–1.9 GB
      uncapped is not itself a crisis, but it is unbounded, and most of what is
      in it right now is finding 1's slow-apply warnings — which is to say the
      cap and Phase 1 fix the same 1.5 GB from opposite ends.

- [ ] **2.3 — Gate.** A week after 2.1, root filesystem used is flat rather than
      climbing, and no `ImageGCFailed` or `EvictionThresholdMet` events on any
      node. Flat is the whole point: the slope in finding 4, not the absolute
      number, is what this phase is against.

## Phase 3 — stop provisioning it wrong

Without this, the next node built reintroduces every finding above, which is
precisely what happened to the node rebuilt on 08/23.

- [ ] **3.1 — Golden image: fixed, and bigger.**
      [`Get-GoldenImage.ps1`](../../scripts/hyperv/Get-GoldenImage.ps1)'s
      `-SizeGB` default moves 32 → 100. The `subformat=dynamic` on the
      `qemu-img convert` can stay — the template is a file to be copied, and
      conversion to fixed belongs at VM-create time where the destination volume
      is known — but the provenance JSON it writes should record which it is, so
      a later reader can tell without opening the file.

- [ ] **3.2 — `New-AerieVM.ps1` creates the OS disk fixed, and separately
      placed.** Two changes, and the second is the interface change: today a
      single `-VMStoragePath` decides where *everything* goes, and this plan's
      whole point is that the OS disk and the Longhorn data disk now want
      different volumes. Split it into `-OsDiskPath` and `-DataDiskPath`, with
      `-VMStoragePath` retained as the default for both so no existing caller
      breaks. Then make the golden-image copy a `Convert-VHD -VHDType Fixed`
      rather than a `Copy-Item`.

- [ ] **3.3 — Provision 0 exposes both.** `os_disk_path`, `os_disk_gb` (default
      `100`) and `data_disk_path` alongside the existing `vm_storage_path` and
      `data_disk_gb` in
      [`provision-0-new-node.yml`](../../.github/workflows/provision-0-new-node.yml).

- [ ] **3.4 — Automatic checkpoints off at VM creation.**
      [`New-AerieVM.ps1`](../../scripts/hyperv/New-AerieVM.ps1) gains
      `Set-VM -AutomaticCheckpointsEnabled $false` beside the
      `Disable-VMIntegrationService` line it already carries for the same kind
      of reason — a Hyper-V default that fights what the VM is for.

      Finding 7 is per-VM and Hyper-V's default is on, so
      [`Move-NodeOsDisk.ps1`](../../scripts/hyperv/Move-NodeOsDisk.ps1) turning
      it off fixes the three nodes that exist and nothing else. Without this
      step the next node built takes a checkpoint at its first boot and holds
      it until its first clean shutdown, which on these hosts means until the
      next Windows Update reboot cycle — the 08/09 window that produced the
      chains on nodes 1 and 2.

      This is the cheapest step in Phase 3 and the one whose absence is
      hardest to notice: a node built without it looks identical in every
      dashboard and is quietly paying finding 7's cost.

- [ ] **3.5 — Document the rule, once.** In
      [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md)'s on-disk
      layout section: **the OS disk goes on the host's fastest volume and is
      fixed; the Longhorn data disk goes on its largest and is fixed; nothing
      Aerie creates on a host is dynamic, and no Aerie VM has automatic
      checkpoints.** One sentence, in the place someone reads before building
      a node. The last clause is 3.4's, and belongs in the same sentence
      because an automatic checkpoint makes the first three untrue again.

## What this plan does not cover

- **The Longhorn data disk.** 200 GB fixed on the bulk volume is correct as it
  stands and no finding here touches it.
- **The bulk disk for photos.** [`immich.md`](immich.md) step 1.1 places a
  1200 GiB dynamic VHDX on `D:\Aerie\` — the same volume this plan is moving
  etcd *off*. That step is still unchecked and no bulk disk is attached to any
  node yet, so it is catchable. The constraint it inherits from here: put it on
  a volume that carries no OS disk, or land it only after Phase 1 has emptied
  that volume of them. Cross-referenced rather than fixed here, because it is
  that plan's step to change.
- **Alert thresholds.** Not raised, not relaxed. They were right.
- **New hardware.** Nothing here needs any. 1.7 may conclude that host C wants a
  second SSD, and if so that is a finding to record, not a prerequisite.
