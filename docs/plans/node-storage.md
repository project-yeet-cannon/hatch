# Node storage — the volume etcd lives on, and the disk that is filling

**Status:** Phase 1 tooling built (1.2, 1.3); no node migrated yet. Two
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
| Where the OS disk goes | **Each host's boot volume** — measured 3–50× faster than the volume it is on now |
| Dynamic or fixed VHDX | **Fixed.** Removes the allocation penalty *and* the "grows into the host volume" hazard |
| Root disk size | **100 GB**, up from the template's 32 GB. Capacity is not the constraint; the fast volume's free space on one host is |
| Bigger disk alone | **No.** Without bounding image accumulation, a bigger disk is a bigger treadmill |
| Longhorn data disk | **Stays where it is.** 200 GB fixed, on the bulk volume. Nothing here touches it |
| Downtime | **One node at a time**, ~15–25 min each. Quorum is 2 of 3 throughout |

## Findings

Six, from the live cluster and from reading the tree. The first three decide
Phase 1; the next two decide Phase 2; the last one decides the order.

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
| C | **34.5 ms / 35.9 ms** | 0.12 ms / 0.40 ms | ~106 GB |

Host C's VM volume is a spinning disk and reads like one. Hosts A and B's are
not — 2–3 ms reads rule that out — but at 5.7–6.8 ms per write under etcd's
flush-heavy pattern they are still 30–60× their own boot volumes, which take
3–4 writes/s and are otherwise doing nothing. Host C also has a third large
volume measuring 2.0 ms with ~822 GB free, which is a fallback rather than the
answer: 2 ms would put that node in the same band the other two are in *today*,
and today is what this plan exists to fix.

The claim to hold this to is not "SSDs are faster". It is that **the fastest
storage in each of these three machines is idle, and the one workload in the
house that is latency-bound is not on it.**

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

### 6. The constrained host is the old Compose host, and its constraint is probably reclaimable

Host C is the odd one in every table above: the smallest boot volume, the only
genuinely spinning VM volume, the worst etcd latency, and — per the cutover — the
machine that used to run everything as Docker Compose before it became a node.
131 GB of its 237 GB boot volume is in use on a machine whose only job now is to
run one VM, which strongly suggests the residue of that former life: old images,
old volumes, a WSL backing file.

That makes the ordering decision. **Do not start with the worst node.** Prove the
procedure on a host with 880 GB of slack where the only thing that can go wrong is
the procedure itself, then bring the constrained host up to the same shape once
its boot volume has been audited. If the audit frees less than ~140 GB, that host
takes a 64 GB disk instead of 100 GB — still twice its projected steady state —
and the plan records it as the host to put a second SSD into next.

## Phase 1 — the OS disk moves to the fast volume, fixed, at 100 GB

One node at a time. Quorum is 2 of 3, so a single node down is survivable; it is
also long enough to trip [`cluster.yaml`](../../deploy/cluster/observability/config/alerts/cluster.yaml)'s
`EtcdMemberDown` at `for: 15m`, which is expected and is what the silence in 1.1
is for.

- [ ] **1.1 — Window and silence.** Silence `alertname=~"etcd.*"` and
      `alertname="EtcdMemberDown"` for the duration, by alertname and never by
      receiver — the discipline
      [`kube-prometheus-stack.yaml`](../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml)
      already documents. Record the pre-change fsync p99 per node so 1.6 has
      something to compare against.

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

- [ ] **1.5 — Second node, other roomy host.** Only after 1.4's node has been
      `Ready` and serving for long enough to trust.

- [ ] **1.6 — Gate: the number moved.** p99 wal fsync on both migrated nodes
      **under 10 ms**, sustained over an hour, off the live Prometheus. If it
      lands in the tens of ms rather than single digits, stop and find out why
      before touching the third node — the whole plan rests on the boot volume
      being as fast under etcd's flush pattern as it measures under the host's.

- [ ] **1.7 — Reclaim host C's boot volume.** Audit what 131 GB of a
      hypervisor-only machine's boot volume is holding — Compose-era images and
      volumes, WSL backing files, old installers — and free it. Target: enough
      headroom for a 100 GB fixed disk plus 40 GB. Record the result; if it
      falls short, that host takes `-SizeGB 64` and gets written into the
      operator's runbook as the next hardware to buy for.

- [ ] **1.8 — Third node.** The worst one, last, now that the procedure is proven
      and the destination has room. Expect the largest single improvement here:
      1130 ms to single digits.

- [ ] **1.9 — Soak, then delete the sources.** Leave the original
      `os-disk.vhdx` files in place for a week as the rollback path — repointing
      `Set-VMHardDiskDrive` back is a one-line undo for as long as they exist.
      Delete them after, and only then; this is the step that is easy to skip
      and expensive to skip in the other direction.

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

- [ ] **3.4 — Document the rule, once.** In
      [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md)'s on-disk
      layout section: **the OS disk goes on the host's fastest volume and is
      fixed; the Longhorn data disk goes on its largest and is fixed; nothing
      Aerie creates on a host is dynamic.** One sentence, in the place someone
      reads before building a node.

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
