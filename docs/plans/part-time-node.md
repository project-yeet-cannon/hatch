# Part-time node — a fourth host that leaves when its owner wants it back

**Status:** Not started. Five phases; the first three are cluster work that
stands on its own merits, the last two are the machine-specific part.

A fourth Windows host joins the cluster as a k3s **agent**, carrying a Linux VM
sized to take real load off the three permanent nodes. Its owner uses the
physical machine a few evenings a month, and on those evenings a control on the
Windows desktop hands the whole machine back: the node drains, the VM stops, and
the Actions runner stops accepting work. One click returns it.

The framing that decides most of this plan: **it is a normal node.** Not a
tainted second-class citizen that workloads must opt into. Nodes drop offline —
that is what a cluster is for — and the correct response to "this one drops on
purpose" is to make the workloads survive a node leaving, which they should
survive anyway. Phase 2 is that work, and it is worth doing whether or not the
fourth host ever appears.

There is exactly one place where "normal node" does not survive contact with the
measurements, and it is Longhorn. See finding 3.

Per [`ethos.md`](../ethos.md), the host is **D** here and its measurements are
observations of one installation. Which physical machine that is belongs in the
operator's runbook. Hosts A, B and C are the same three
[`node-storage.md`](node-storage.md) names.

## The brief, as decisions

| Question | Answer |
|---|---|
| Server or agent | **Agent.** Never a fourth etcd member — quorum stays 2-of-3 among the permanent nodes, and an agent leaving is a scheduling event rather than a raft event |
| Taint it? | **No.** A normal, schedulable node. Placement is expressed by what workloads require, not by what the node forbids |
| Longhorn replicas on it | **No** — on measured grounds (finding 3), not on principle. It joins Longhorn with `allowScheduling: false` and no data disk |
| Can stateful pods run there | **Yes.** A Longhorn engine attaches over the network; the replicas stay on A/B/C. This is how observability gets the RAM without the data following it |
| What moves there | Observability first (the singletons that hold the most memory and are already `HA not required`), then surge replicas and batch |
| Memory | **16 GB static**, of 32. Not Dynamic Memory — see finding 5 |
| Disk | One fixed OS disk, sized by [`node-storage.md`](node-storage.md)'s rules. **No second disk** |
| GPU | **Not now, but not designed out.** Placement keys off capability labels, never node names, so a GPU-bearing node slots in later. Hyper-V DDA needs Windows Server; GPU-P is the only consumer path and is its own plan |
| Personal mode depth | **VM stops; hypervisor stays.** One tier, seconds not reboots |
| Who may enter personal mode | The **desktop user, unelevated**, via a Scheduled Task registered by an administrator once |
| May entering fail? | **No.** Every step has a deadline and proceeds past it. Personal mode is a promise to a person, not a negotiation with a cluster |
| May leaving fail? | **Yes.** Exit is the direction allowed to report an error and stop |

---

## Findings

Six. The first two are about the tooling, the next two decide the shape of the
node, and the last two decide the control. All cluster numbers were read from
the live cluster on **2026-08-24**.

### 1. Nothing in the tooling can build an agent

[`Install-K3sNode.ps1`](../../scripts/k3s/Install-K3sNode.ps1) has exactly two
parameter sets, `-ClusterInit` and `-JoinServer`, and both run `k3s server`.
There is no `k3s agent` path anywhere in the repo, and
[`provision-1-install-k3s.yml`](../../.github/workflows/provision-1-install-k3s.yml)
has no role input to expose one.

This is the first thing to fix and the most reusable thing in the plan: "add a
worker" is a normal operation for any installation, and it is currently not
expressible. Phase 1.

### 2. The cluster is a pile of singletons, and a drain would notice

Every one of these runs at **one replica** today:

| Workload | Replicas | What its absence costs |
|---|---|---|
| `kube-system/traefik` | 1 | **All ingress.** Every host on the domain |
| `kube-system/coredns` | 1 | **All in-cluster name resolution** |
| `kube-system/metrics-server` | 1 | HPA and `kubectl top` |
| `kube-system/kube-vip-cloud-provider` | 1 | New LoadBalancer address assignment |
| `observability/*` | 1 each | Metrics, logs, dashboards, alerting |

Traefik and coredns are the two that matter. Both are single points of failure
**today**, on a three-node cluster, independent of anything in this plan — if
host A reboots on its Patch Tuesday window and traefik was on it, the house is
off the internet for as long as rescheduling takes. Adding a fourth node does
not create that problem; it raises the odds of meeting it from one-in-three to
one-in-four and it makes meeting it a weekly event rather than a monthly one.

So Phase 2 fixes it, and would be worth doing if this plan stopped there.

### 3. Longhorn's anti-affinity is hard, and that is the one carve-out

Read from the live cluster:

```text
default-replica-count                  {"v1":"3","v2":"3"}
replica-soft-anti-affinity             false
storage-over-provisioning-percentage   100
```

`replica-soft-anti-affinity: false` means a replica placement that would put two
replicas of one volume on the same node is **refused**, not merely discouraged.
With three nodes and three-replica volumes, every `longhorn-r3` volume today
holds exactly one replica per node, and the arithmetic is exact.

Add a fourth schedulable Longhorn node and the arithmetic stops being exact.
Longhorn is now free to place a replica on D. When D leaves for the evening,
that volume is degraded — and it **cannot rebuild**, because the three remaining
nodes already hold the other replicas and hard anti-affinity forbids a second
copy on any of them. The volume sits degraded until D comes back, which is to
say for the whole personal-mode session, during which it is one node failure
from data loss instead of two.

That is a genuine reason, and it is the only one. The node joins Longhorn with
`allowScheduling: false` and gets no data disk at all — which also means one
less fixed VHDX on a host that is short on space.

What it does **not** mean is that stateful pods stay off the node. A Longhorn
volume attaches to whatever node its pod runs on and reaches its replicas over
the network; the engine is local, the data is not. Prometheus can run on D with
its 20 GiB `longhorn-r2` PVC still replicated on A/B/C. That is precisely the
trade this plan wants: the memory moves, the data does not.

Postgres is untouched by all of it. CNPG's three instances bind `local-path`
PVCs, which pin each pod to the node holding its directory; a fourth node with
no instance on it is simply not a candidate.

### 4. Observability is where the memory actually is

The three permanent nodes carry these requests today:

| node | memory requests | of allocatable |
|---|---|---|
| node on host A | 1756 Mi | 10% |
| node on host B | 4704 Mi | 19% |
| node on host C | 3936 Mi | 16% |

Requests are not the pressure — the limits are, at 46–50% each with CPU limits
already at 110–132% overcommit, and the observability stack is most of it.
`opensearch-cluster-master`, `prometheus`, `alertmanager` and `grafana` are four
singleton StatefulSets/Deployments with Longhorn PVCs, and
[`design.md`](swarm/design.md)'s own decision table lists observability under
**HA not required** — reschedule-on-failure is stated to be acceptable for
exactly these workloads.

They are therefore the right first tenants for D: the largest memory consumers,
already declared tolerant of the exact disruption D introduces, and (per finding
3) able to run there without their data going with them.

A caveat found while reading, which Phase 2 must resolve before any drain is
attempted: several PDBs report **`ALLOWED DISRUPTIONS: 0`** right now, including
`aerie/api` (3/3 ready, `maxUnavailable: 1`, which should compute to 1) and
`observability/opensearch-cluster-master-pdb`. Some of that is the observability
stack being mid-repair at the time of reading — `grafana` was
`PodInitializing`, `autokuma` was `Init:Error`. Some of it may not be. A
`kubectl drain` blocks on a PDB with zero allowed disruptions, so this must be
understood before the drain is the load-bearing step of a control someone
presses on a Friday night.

### 5. Hyper-V Dynamic Memory and kubelet disagree about what memory is

The temptation on a machine whose whole point is giving memory back is to let
Hyper-V balloon it. Do not. The kubelet reads allocatable memory once at
startup, from the memory present at boot; ballooning changes what the guest
actually has without changing what the scheduler believes, so the cluster
happily places pods against capacity that has been removed underneath them and
the OOM killer resolves the disagreement. Static memory, the same as every
existing node.

The whole-machine reclaim is what personal mode is *for*, and it reclaims all 16
GB at once rather than negotiating for it continuously.

### 6. Autostart is currently on, and would undo personal mode

[`New-AerieVM.ps1`](../../scripts/hyperv/New-AerieVM.ps1) creates VMs with
autostart enabled and `ShutDown` as the stop action, which is right for a
permanent node and wrong for this one. A Windows Update reboot during a gaming
evening — and [`Set-UpdateRebootSchedule.ps1`](../../scripts/hyperv/Set-UpdateRebootSchedule.ps1)
deliberately sets `NoAutoRebootWithLoggedOnUsers = 0`, so it reboots straight
through an active session — would bring the node back up mid-session with no
one having asked for it.

This VM gets `-AutomaticStartAction Nothing`, and a boot-time task reads the
persisted mode and decides. Phase 4.

---

## Phase 1 — Teach the tooling about agents

**Exit:** a `role: agent` dispatch of Provision 1 produces a node that shows
`<none>` under ROLES in `kubectl get nodes` and schedules pods.

- [ ] 1.1 Add an `-Agent` parameter set to
      [`Install-K3sNode.ps1`](../../scripts/k3s/Install-K3sNode.ps1), taking the
      same `-JoinServer` target and token, running `k3s agent --server
      https://<ip>:6443 --token <token>`. Preflight drops the etcd (2379-2380)
      port checks, which an agent never speaks, and keeps 6443 and 10250.
- [ ] 1.2 The node-configuration stage (`vm.max_map_count`,
      `etcd-expose-metrics`) splits: the sysctl applies to agents, the etcd
      metrics setting does not. `/etc/rancher/k3s/config.yaml` is a server file;
      an agent reads `/etc/rancher/k3s/agent.yaml`. Getting this wrong installs
      cleanly and is never read — the same trap
      [`longhorn.yaml`](../../deploy/cluster/infrastructure/controllers/longhorn.yaml)'s
      note 4 documents.
- [ ] 1.3 The verify stage asserts `Ready` without asserting a control-plane
      role, and skips the `:2381` etcd metrics probe.
- [ ] 1.4 Add a `role` choice input (`server` / `agent`) to
      [`provision-1-install-k3s.yml`](../../.github/workflows/provision-1-install-k3s.yml),
      defaulting to `server` so no existing dispatch changes behavior.
- [ ] 1.5 Node labels, applied at install from a new `-NodeLabel` parameter, so
      placement never keys off a node **name**. Two to start:
      `aerie.family/availability=part-time` and `aerie.family/storage=none`. The
      GPU label that finding's decision table defers
      (`aerie.family/gpu=<model>`) uses the same mechanism when it arrives, and
      that is the whole of "design for it now".

## Phase 2 — Make a node leaving a non-event

Independently valuable, and a prerequisite: this lands **before** D joins.

**Exit:** `kubectl drain` of any node completes without `--force`, and ingress
and DNS survive it with no gap.

- [ ] 2.1 **Traefik to 2 replicas** with `requiredDuringScheduling` pod
      anti-affinity on `kubernetes.io/hostname`, via
      [`traefik-helmchartconfig.yaml`](../../deploy/cluster/infrastructure/config/traefik-helmchartconfig.yaml).
      Required, not preferred: two replicas that land on one node are one
      replica with extra steps. Add a PDB with `maxUnavailable: 1`, matching the
      reasoning already written into
      [`poddisruptionbudgets.yaml`](../../charts/aerie/templates/poddisruptionbudgets.yaml).
- [ ] 2.2 **CoreDNS to 2 replicas**, same anti-affinity. k3s owns this manifest,
      so the override is a `HelmChartConfig` beside Traefik's rather than an
      edit — an edit is reverted on the next k3s restart.
- [ ] 2.3 **Resolve the zero-allowed-disruptions PDBs** from finding 4. Repair
      the observability stack first (`autokuma` `Init:Error`, `grafana` stuck
      initializing, `opensearch` 0/1 — these predate this plan), then re-read.
      If `aerie/api` still computes 0 with 3/3 ready, that is a bug to find, not
      a number to work around.
- [ ] 2.4 **A drain rehearsal.** Cordon and drain one permanent node, time it,
      confirm the house stays up, uncordon. This is the dress rehearsal for
      every future personal-mode entry, run against a node whose owner is not
      waiting to play a game.

## Phase 3 — Build the node

**Exit:** four nodes Ready; D holds no Longhorn replicas; the house is unchanged.

- [ ] 3.1 **Measure D's disk before choosing anything**, with
      [`node-storage.md`](node-storage.md) Phase 1's method. "A few hundred GB
      free" does not say whether it is spinning or solid-state, and this plan
      should not guess: the answer sets the OS disk's destination volume and
      confirms (or overturns) finding 3's no-data-disk decision.
- [ ] 3.2 Host prerequisites, per
      [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md): Hyper-V role,
      an External switch bound to the physical NIC with
      `-AllowManagementOS $true`, a DHCP reservation for the new MAC
      (`00-15-5D-04-01-01` under the existing `[host]-[vm]-[nic]` scheme), and
      the node SSH public key.
- [ ] 3.3 Register a self-hosted runner labelled `hyperv-host-3`, service
      account a local Administrator. Add it to
      [`stagger-update-reboots.yml`](../../.github/workflows/stagger-update-reboots.yml)'s
      matrix and to every provisioning workflow's `host` choice list.
- [ ] 3.4 Dispatch **Provision 0** for `aerie-node-3`: 16 GB static memory,
      fixed OS disk on the volume 3.1 chose, **`-DataDiskSizeGB 0`**. If that
      parameter cannot express "no data disk" today, it is a small addition to
      [`New-AerieVM.ps1`](../../scripts/hyperv/New-AerieVM.ps1) and belongs in
      Phase 1.
- [ ] 3.5 Set `-AutomaticStartAction Nothing` on the VM (finding 6). Everything
      else about the VM stays as the script builds it.
- [ ] 3.6 Dispatch **Provision 1** with `role: agent`, joining any permanent
      node's address.
- [ ] 3.7 In Longhorn, set the node's `allowScheduling: false`. Confirm by
      reading back `nodes.longhorn.io/aerie-node-3` — and then confirm the thing
      that actually matters, that an existing `longhorn-r3` volume still reports
      three healthy replicas across A, B and C only.
- [ ] 3.8 Leave it empty for a few days and watch. Nothing is moved yet.

## Phase 4 — Personal mode

**Exit:** an unelevated desktop user clicks a shortcut; within a minute the node
is drained, the VM is off and the runner is stopped. Another click returns all
three. Neither depends on remembering a command.

The control is `scripts/hyperv/Set-PersonalMode.ps1`, with `-Enter`, `-Exit` and
`-Status`, and the state lives in one file on the host that both the script and
the boot task read.

- [ ] 4.1 **Enter**, in order, each step with its own deadline and **none of
      them able to stop the sequence**:
      1. Label the node `aerie.family/personal-mode=true` — before the drain,
         while the API server is still reachable from it. Phase 5 reads this.
      2. `kubectl cordon`, then `kubectl drain --ignore-daemonsets
         --delete-emptydir-data`, with a hard timeout (start at 90s). Past the
         timeout, proceed to 3 regardless — an undrained pod is an ordinary node
         failure, which Phase 2 made survivable.
      3. Wait for the runner to finish its current job by polling for the
         absence of a `Runner.Worker` process — local, credential-free, and
         exact. Re-check after each exit in case the listener started another.
         Total deadline, then stop anyway; every workflow that runs here is a
         re-runnable dispatch.
      4. `Stop-Service actions.runner.*`.
      5. `Stop-VM` (ACPI), escalating to `-Force` after a timeout.
      6. Write the state file.
- [ ] 4.2 **Exit** is the mirror, and is allowed to fail loudly: start the
      runner service, `Start-VM`, wait for `Ready`, `kubectl uncordon`, remove
      the label, clear the state file.
- [ ] 4.3 **Registration**, once, by an administrator:
      `Register-PersonalModeControl.ps1` creates two Scheduled Tasks running as
      SYSTEM at highest privilege — modelled directly on
      [`Register-VmConsoleLogShipper.ps1`](../../scripts/hyperv/lib/Register-VmConsoleLogShipper.ps1),
      including its `ExecutionTimeLimit` lesson — and grants the desktop user
      run rights on them. This is the whole reason for the task indirection: the
      user needs to trigger an action requiring Administrator without holding
      Administrator and without a UAC prompt every time.
- [ ] 4.4 **The shortcuts.** Two on the desktop, calling
      `schtasks /run /tn Aerie-PersonalMode-Enter` (and `-Exit`), each opening a
      console that shows the steps and their timings and closes when done.
      Feedback matters more than polish here: the user needs to know when the
      machine is theirs, and a silent shortcut means they wait, or don't.
- [ ] 4.5 **A boot-time task** (`AtStartup`, SYSTEM) reads the state file and
      reconciles: personal mode off and the VM down means start it; personal
      mode on means leave everything alone. This is what makes finding 6's
      Windows Update reboot harmless in both directions.
- [ ] 4.6 **A daily auto-exit** at an hour nobody games, exiting personal mode
      unless a `pin` flag is set. Forgetting to give the node back should cost
      one night, not one month.
- [ ] 4.7 **Verify the ungraceful path**, because it is the one that will
      happen: pull the power mid-session. The house should stay up, the node
      should go `NotReady`, its pods should reschedule, and the boot task should
      bring it back correctly on the next power-on.

## Phase 5 — An absence that doesn't page anyone

**Exit:** a full personal-mode evening produces no alert and no red tile, and a
node that is down *without* personal mode set still produces both.

- [ ] 5.1 Alert rules in
      [`cluster.yaml`](../../deploy/cluster/observability/config/alerts/cluster.yaml)
      that fire on node readiness gain an exclusion for
      `aerie.family/personal-mode=true`. The label persists on the Node object
      while the node is `NotReady`, which is what makes this work — and 4.1
      applies it *before* the drain for the same reason.
- [ ] 5.2 The same exclusion in Uptime Kuma via
      [`autokuma`](../../deploy/cluster/observability/controllers/autokuma.yaml),
      so the status page reads "off by request" rather than "down".
- [ ] 5.3 A dashboard row: which node is part-time, whether it is in personal
      mode, and how much capacity is currently on loan. The number that answers
      "can I afford to hand it back right now" should be on a screen, not in
      someone's head.
- [ ] 5.4 **Only now, move the tenants.** Observability first, per finding 4,
      one workload at a time with a personal-mode cycle between each. Surge
      replicas and batch follow once the first survives a few evenings.

---

## What this plan does not do

- **No GPU.** Node labels are shaped so a GPU node needs no redesign (1.5), but
  DDA requires Windows Server and GPU-P is undocumented, per-driver-fragile, and
  its own plan.
- **No second tier of personal mode.** Turning off the hypervisor entirely
  (`bcdedit /set hypervisorlaunchtype off`) removes Hyper-V's few-percent cost
  in games, and costs two reboots and a boot-time state machine to get right. If
  the few percent turns out to be noticeable, that is a follow-up with a
  measurement behind it.
- **No second part-time host.** Nothing here forbids one — the labels and the
  control are per-host and would work — but the Longhorn arithmetic in finding 3
  changes again with two, and should be re-derived rather than assumed.
