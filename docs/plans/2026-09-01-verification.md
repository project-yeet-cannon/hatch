# 2026-09-01 — the two waiting periods close

**Status:** not started. Run on or after **2026-09-01**; nothing brings either
half forward.

This is a dated checklist, not a plan. The node-storage work finished on
2026-08-25 and left two things behind that were *dates* rather than steps: a
seven-day soak before three replaced OS disks may be deleted, and a week-long
observation window before a disk-growth ceiling can be said to hold. Both are
written here with the numbers they have to be compared against, because the
plan that produced those numbers has been dissipated into the repo and this
file is the only thing that still carries them.

Two of the three parts are quick. Part 2 is the one that needs reading rather
than running, and its easy reading is wrong — see the caution there.

Per [`ethos.md`](../ethos.md), nodes appear here as the node on **host A**, **B**
and **C**. Which physical machine is which, and which `hyperv-host-N` runner
label goes with it, is in the operator's own runbook — and is also recorded on
each host in the migration record named in Part 1.

---

## Before you start

- **Dispatch rights** on *Provision 8: Move node OS disk*, and the ability to
  reach each Hyper-V host's runner.
- **Cluster access** for the read-only checks: `k3s kubectl` on any server
  node over SSH.
- **A whole cluster.** Both parts assume all three nodes are `Ready` and every
  etcd member healthy. If that is not true today, this is not the day — none
  of it is urgent, and Part 1's refusals will say so anyway.

Read the whole file before dispatching anything in Part 1. It is the
irreversible half.

---

## Part 0 — Confirm Phase 1 still holds, before deleting its undo

*Not in the original plan, and worth the five minutes.* The three disks Part 1
deletes are the rollback path for the migration whose gate was the etcd fsync
distribution. Deleting the undo without re-reading the thing it undoes is the
one ordering mistake available here.

Read the fsync distribution at exact bucket boundaries — **not** as a
percentile, for the reason in [Reading a fsync
number](#reading-a-fsync-number-the-percentile-lies) below:

```promql
sum(rate(etcd_disk_wal_fsync_duration_seconds_bucket{le="0.008"}[30m])) by (instance)
  / sum(rate(etcd_disk_wal_fsync_duration_seconds_count[30m])) by (instance)
```

Repeat with `le="0.016"` and `le="0.032"`.

**Expected**, from 2026-08-25, and what Part 1 is safe against:

| | ≤ 8 ms | ≤ 16 ms | > 32 ms |
|---|---|---|---|
| node on host A | 93.27% | 99.85% | 0.01% |
| node on host B | 93.09% | 99.97% | 0.00% |
| node on host C | 88.45% | 99.76% | 0.00% |

**The gate, as restated after all three migrations:** over 85% of fsyncs at or
under 8 ms, over 99% at or under 16 ms, and nothing above 32 ms.

If all three nodes still pass, continue. If a node has drifted badly, **stop
and keep its source disk** — that disk is a one-dispatch rollback for as long
as it exists, and after Part 1 it is a restore instead.

---

## Part 1 — Delete the three source OS disks

Each node's original `os-disk.vhdx` was left in place at migration as the
rollback path. `Set-VMHardDiskDrive` pointing back at it is a one-line undo for
as long as the file exists. The soak is **7 days** from each node's own
migration timestamp; all three migrated on 2026-08-24/25, so all three are
past it by 2026-09-01.

This is the step that is easy to skip, and expensive to skip in the other
direction: three stale VHDXs sitting on volumes that have other uses,
indefinitely, because nobody wanted to be the one to delete them. They are
*dynamic* files, so each holds roughly its node's pre-migration root filesystem
rather than a full 100 GB — the run prints the exact bytes it frees.

### The refusals do the thinking

`-RemoveSourceDisk` is the only mode of `Move-NodeOsDisk.ps1` that deletes, and
it refuses a run that:

- is **inside the 7-day soak** (waivable only with `force`);
- targets a node that is **not `Ready`** — "the source disk is the rollback
  path for a node that is not well, which is exactly what this one is";
- runs while **any etcd member is unhealthy**;
- names a disk **still attached to a VM**, or **referenced by a checkpoint**.

Only the first is waivable. This is why the deletion could be a date rather
than a discipline: a run dispatched early is refused, not obeyed.

### Steps, once per node

One node at a time is not required here — nothing is drained and no VM stops —
but do them one at a time anyway so a surprise on the first stops the other
two.

- [ ] **1.1 — Read the migration record on each host** before dispatching. It
      is the only thing that still names the source disk:

      ```powershell
      Get-Content <os-disk-path>\<vm_name>\os-disk-migration.json | ConvertFrom-Json
      ```

      Check `movedUtc` (the soak clock), `sourcePath` (what will be deleted),
      and that `sourceRemovedUtc` is absent — if it is present, that node is
      already done.

- [ ] **1.2 — Dispatch *Provision 8* for the node on host A** with
      `mode: remove-source`. Leave `force` **off**: if it refuses, the refusal
      is the answer, not an obstacle. `destination_path`, `size_gb`,
      `merge_checkpoints` and `preflight_only` are `move`-only and are ignored.

- [ ] **1.3 — Confirm, then repeat for hosts B and C.** A green run prints the
      bytes freed and amends the migration record with `sourceRemovedUtc` and
      `sourceRemovedFreedBytes`. The record is **kept, not deleted** — it is the
      only thing that will explain, a year from now, why that VM's disk sits on
      a different volume from every other file on its host.

- [ ] **1.4 — Note that the rollback window is now closed.** From here, undoing
      the migration is a restore from backup rather than a repoint.

### One node's file has a different name

On the node that was **converted in place** rather than moved, the surviving
disk is `os-disk-fixed.vhdx`, not `os-disk.vhdx`: `Convert-VHD` cannot write
the file it is reading, so the two had to have different names in one
directory. The migration record names both, and the script reads it rather
than guessing. Renaming it back would mean another maintenance window on an
etcd member for no benefit — leave it.

---

## Part 2 — Read the image-GC gate

The other half of the node-storage work bounded what fills the root filesystem:
kubelet's image GC moved from its default 85/80 down to **70/55**, and
journald capped at **512M**. Both landed on all three nodes on 2026-08-25. The
gate is a week of observation.

**The gate as written:** a week after the last node took the settings, root
filesystem used is flat rather than climbing, and no `ImageGCFailed` or
`EvictionThresholdMet` events on any node.

### Read it carefully, because the easy reading is wrong

The growth this was written against was **4–5 percentage points a day on a
32 GB disk**. The same bytes per day on a 99 GB disk is under 1.5 points, and
the nodes started this week at **23–26%**. So a flat-looking line proves much
less than it would have before the disks grew.

What would actually demonstrate the ceiling works is **image GC firing at 70%
and holding**, and nothing has been near 70% since the disks were replaced.

So there are three honest outcomes, not two:

| What you see | What it means |
|---|---|
| Flat at ~24% | **Pass on the letter, deferral of the question.** The bigger disk bought so much room the ceiling has not been tested yet. Record it as such and move on |
| Climbing | **Fail.** The ceiling is not holding, or something new is filling the disk |
| A GC event that errors rather than reclaiming | **Fail.** `ImageGCFailed` is the one that matters |

The first is the likely one. Write it down as a deferral rather than as a
success, so the next person does not read "gate passed" as "ceiling proven".

### Steps

- [ ] **2.1 — Root filesystem used, per node.** Directly:

      ```bash
      df -h /
      ```

      or across all three from Prometheus:

      ```promql
      100 - (node_filesystem_avail_bytes{mountpoint="/",fstype!="tmpfs"}
             / node_filesystem_size_bytes{mountpoint="/",fstype!="tmpfs"} * 100)
      ```

      **Starting line, 2026-08-25:** 22–24 GB of 99 GB, i.e. **23–26%** on all
      three. That is the number today's reading is compared against — it is a
      starting line, not a result.

- [ ] **2.2 — The slope over the week**, which is the actual gate. Graph the
      query above over `7d`, or:

      ```promql
      predict_linear(node_filesystem_avail_bytes{mountpoint="/",fstype!="tmpfs"}[7d], 7*24*3600)
      ```

      A negative trend that would cross 30% of the disk inside a month is a
      climb, whatever today's absolute number is.

- [ ] **2.3 — Events on all three nodes.**

      ```bash
      k3s kubectl get events -A --field-selector reason=ImageGCFailed
      k3s kubectl get events -A --field-selector reason=EvictionThresholdMet
      ```

      Both empty is the pass. Note that the default event TTL is an hour — for
      the week, check the `kubelet_*` image-GC metrics or the nodes' journals
      instead if either matters.

- [ ] **2.4 — Confirm the settings are still what was set**, since a k3s
      upgrade could in principle rewrite the directory:

      ```bash
      cat /var/lib/rancher/k3s/agent/etc/kubelet.conf.d/50-aerie-image-gc.conf
      journalctl --disk-usage
      ```

      Expect `imageGCHighThresholdPercent: 70` / `imageGCLowThresholdPercent: 55`,
      and a journal under 512M. *Provision 1* verifies the first against
      kubelet's own `/configz` on every run, so a dispatch is the stronger
      check if one is due anyway.

- [ ] **2.5 — Record the outcome** in whichever of the three rows above it
      landed on, and delete this file.

---

## Reference: the numbers this file exists to carry

### Reading a fsync number: the percentile lies

`etcd_disk_wal_fsync_duration_seconds` has **power-of-two buckets**, and
`histogram_quantile` interpolates linearly *inside* a bucket. With ~93% of
fsyncs at or under 8 ms and ~99.8% at or under 16 ms, the computed p99 lands
near 15 ms whatever the truth is — all three nodes reported 14.90, 14.94 and
15.43 ms while sitting on destination volumes spanning a **20× range** in
measured latency.

**There is no bucket between 8 and 16 ms, so a "under 10 ms" gate is not
expressible in this metric**: 9 ms and 15 ms are the same reading. Any gate on
this histogram has to be stated at exact bucket boundaries, which is why Part 0
is written the way it is.

The same caution applies backwards. The pre-migration figures below came from
the same interpolating quantile and are equally quantised: their *magnitude* is
sound — those samples were genuinely two to three orders of magnitude up — but
read them as bucket ranges, not measurements.

### Before and after

| node | wal fsync p99 before | after | backend commit before | after |
|---|---|---|---|---|
| host A | 243 ms | ~15 ms | 371 ms | — |
| host B | 204 ms | ~15 ms | 244 ms | — |
| host C | 1130 ms | 14.1 ms (p50 1.7 ms) | 1526 ms | 13.3 ms |

Root filesystems went from 32 GB at 48–84% used to 99 GB at 23–26%. For scale,
the alerts that started the whole thing fire at 250 ms fsync, 500 ms commit,
and 1 s critical — nothing is near them.

---

## What happens to this file

**Delete it when both parts are done.** It is a dated checklist with two dated
items in it; once they are struck, everything durable in it already lives
somewhere else:

- the disk-layout rule and how the destination volume was chosen —
  [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md)
- the two node settings and why they are what they are —
  [`scripts/k3s/README.md`](../../scripts/k3s/README.md)
- the histogram-bucket caution —
  [`cluster.yaml`](../../deploy/cluster/observability/config/alerts/cluster.yaml)
- the migration itself, per host — the `os-disk-migration.json` beside each
  node's disk, which outlives this file on purpose
