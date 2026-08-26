# Part-time node — a fourth host that leaves when its owner wants it back

**Status:** Phase 1 done. Phase 2.1 is merged; **2.2 was merged and then
reverted** — it stopped every application deploy in the cluster, and the
reasoning and the failure are preserved in
[`coredns-availability.yaml`](../../deploy/cluster/infrastructure/config/coredns-availability.yaml)'s
header, so that step is back to unstarted. 2.3's re-read is done; 2.4's rehearsal is written and waiting on a cluster whose CoreDNS is no longer a singleton. **Phase 3's
tooling is written** — the host is a choice on every provisioning workflow,
`-AutomaticStartAction` is an input rather than a manual step, and 3.6/3.8 are
one dispatchable gate — so what is left of that phase is the physical build.
**Phase 4 is written in full**, and its exit criterion is the first click by a
person who is not an administrator. Phase 5 not started. Five phases; the
first three are cluster work that stands on its own merits, the last two are
the machine-specific part.

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
operator's runbook. Hosts A, B and C are the three that already carry nodes.

## The brief, as decisions

| Question | Answer |
|---|---|
| Server or agent | **Agent.** Never a fourth etcd member — quorum stays 2-of-3 among the permanent nodes, and an agent leaving is a scheduling event rather than a raft event |
| Taint it? | **No.** A normal, schedulable node. Placement is expressed by what workloads require, not by what the node forbids |
| Longhorn replicas on it | **No** — on measured grounds (finding 3), not on principle. It joins Longhorn with `allowScheduling: false` and no data disk |
| Can stateful pods run there | **Yes.** A Longhorn engine attaches over the network; the replicas stay on A/B/C. This is how observability gets the RAM without the data following it |
| What moves there | Observability first (the singletons that hold the most memory and are already `HA not required`), then surge replicas and batch |
| Memory | **16 GB static**, of 32. Not Dynamic Memory — see finding 5 |
| Disk | One fixed OS disk on the host's fastest volume, per the rule in [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md). **No second disk** |
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

## [x] Phase 1 — Teach the tooling about agents

**Exit:** a `role: agent` dispatch of Provision 1 produces a node that shows
`<none>` under ROLES in `kubectl get nodes` and schedules pods.

The code is written and the assertions are in it — Verify fails the run if
ROLES is anything but `<none>`. The exit criterion itself cannot be *observed*
until there is a fourth host to dispatch against, which is Phase 3; nothing
here changes any existing dispatch's behavior, so it lands before then rather
than waiting on hardware.

- [x] 1.1 Add an `-Agent` parameter set to
      [`Install-K3sNode.ps1`](../../scripts/k3s/Install-K3sNode.ps1), taking the
      same `-JoinServer` target and token, running `k3s agent --server
      https://<ip>:6443 --token <token>`. Preflight drops the etcd (2379-2380)
      port checks, which an agent never speaks, and keeps 6443 and 10250.
      **Three things this step turned out to also need**, each of which would
      otherwise have produced an install that reports success:
      - The unit is `k3s-agent.service`, not `k3s.service` — the install script
        names it after the command it is given — and the uninstall script beside
        it is `k3s-agent-uninstall.sh`. Every `systemctl` and `-Reinstall` path
        keys off the role now.
      - Both units are probed on every run. A node is one role or the other
        (they share `/var/lib/rancher/k3s` and the same kubelet), so finding the
        other one active stops the run with the uninstall command to run if the
        role change is deliberate.
      - Port **22** on the join server joins the preflight for agents, because
        of 1.3 below.
- [x] 1.2 The node-configuration stage (`vm.max_map_count`,
      `etcd-expose-metrics`) splits: the sysctl applies to agents, the etcd
      metrics setting does not. **The mechanism is worse than this plan
      assumed, and in a useful direction.** There is no
      `/etc/rancher/k3s/agent.yaml` — `k3s agent` reads the same
      `/etc/rancher/k3s/config.yaml` a server does. What differs is the
      filtering: k3s checks the keys it finds there against the **server**
      command's flags only ([`pkg/configfilearg`](https://github.com/k3s-io/k3s/blob/master/pkg/configfilearg/defaultparser.go)'s
      `ValidFlags` has no `agent` entry, and `stripInvalidFlags` returns the
      list untouched for any command that has none), so `etcd-expose-metrics`
      reaches the agent CLI verbatim and the unit exits at start on `flag
      provided but not defined`. So the failure is loud rather than silent —
      but it is a failed install rather than the configuration mistake it
      actually is, which is worth as much documentation either way. An agent
      gets **no** config.yaml, and one left behind by an earlier server install
      on the same node is removed rather than inherited; a config.yaml this
      script didn't write stops the run instead.
      Also confirmed rather than assumed: the kubelet image-GC drop-in *does*
      apply to agents unchanged — k3s runs kubelet from the same
      `/var/lib/rancher/k3s/agent` tree on both roles — and so does journald's
      cap. Three of the four settings, not two.
- [x] 1.3 The verify stage asserts `Ready` without asserting a control-plane
      role, and skips the `:2381` etcd metrics probe. It asserts the *opposite*
      too: for an agent, ROLES must read `<none>`, which is this phase's exit
      criterion and the one thing that tells a worker from a server at a
      glance. The part the plan missed: an agent has no kubeconfig and no
      apiserver, so `k3s kubectl` on it talks to a default `localhost:8080` and
      fails. Every cluster-level question in Verify — Ready, `/configz`, the
      labels, the node list — is asked of `-JoinServer` over SSH with the same
      key instead, which is what put port 22 in 1.1's preflight.
- [x] 1.4 Add a `role` choice input (`server` / `agent`) to
      [`provision-1-install-k3s.yml`](../../.github/workflows/provision-1-install-k3s.yml),
      defaulting to `server` so no existing dispatch changes behavior.
      Landed as a third option on the `role` input that already existed
      (`cluster-init` / `join` / **`agent`**) rather than a second input named
      `role` beside it. Same effect on existing dispatches — `cluster-init` is
      still first, and neither of the two existing values changed meaning.
- [x] 1.5 Node labels, applied at install from a new `-NodeLabel` parameter, so
      placement never keys off a node **name**. Two to start:
      `aerie.family/availability=part-time` and `aerie.family/storage=none`. The
      GPU label that finding's decision table defers
      (`aerie.family/gpu=<model>`) uses the same mechanism when it arrives, and
      that is the whole of "design for it now".
      `--node-label` alone is not enough: kubelet writes those labels when it
      first creates the Node object and never revisits them, so re-dispatching
      to change one would report success having changed nothing. They are
      passed to the install *and* reconciled against the live Node object
      afterwards. Labels named are added or overwritten; nothing is removed,
      since kubelet, k3s and Longhorn all write onto the same object.
      Exposed on the workflow as a comma-separated `node_labels` input.

## [] Phase 2 — Make a node leaving a non-event

Independently valuable, and a prerequisite: this lands **before** D joins.

**Exit:** `kubectl drain` of any node completes without `--force`, and ingress
and DNS survive it with no gap.

The two manifest changes are written; the two things that can only be learned
from the running cluster are not done. Neither 2.1 nor 2.2 has been *observed*
yet — they reach the cluster by being committed, like everything else under
`deploy/`, so the first proof either works is Flux reconciling them, and the
first proof they were the right changes is 2.4.

- [x] 2.1 **Traefik to 2 replicas** with `requiredDuringScheduling` pod
      anti-affinity on `kubernetes.io/hostname`, via
      [`traefik-helmchartconfig.yaml`](../../deploy/cluster/infrastructure/config/traefik-helmchartconfig.yaml).
      Required, not preferred: two replicas that land on one node are one
      replica with extra steps. Add a PDB with `maxUnavailable: 1`, matching the
      reasoning already written into
      [`poddisruptionbudgets.yaml`](../../charts/aerie/templates/poddisruptionbudgets.yaml).
      Landed as written — `deployment.replicas`, `affinity` and
      `podDisruptionBudget` in the same `valuesContent`, all three verified by
      rendering the chart k3s actually serves (pulled from
      `/var/lib/rancher/k3s/server/static/charts`) against k3s's own baseline
      values plus this file, and diffing the result against the same render
      without it. The diff is exactly three things: `replicas: 1` → `2`, the
      `affinity` block on the pod spec, and a new PodDisruptionBudget. Nothing
      else in the render moved — in particular the `deployment` map k3s's own
      values set is merged into rather than replaced, which is the key-by-key
      Helm merge this file's header describes.

      **One check that turned out not to bite, and would have been a failed
      install if it did.** The chart's `templates/deployment.yaml` *fails the
      render* — `fail`, not a warning — if `replicas > 1` and any
      `additionalArguments` entry contains `.acme.`, because Traefik's own ACME
      resolver keeps certificates in a file no second replica can share. k3s's
      packaged `traefik.yaml` sets no `additionalArguments` at all, and this
      installation's certificates come from cert-manager into a Secret served
      through 3b.10's TLSStore, which every replica reads equally. Checked
      rather than assumed, because the failure mode is a HelmChart that stops
      installing with no object left behind to explain why.

      The cost, recorded in the manifest as well: the chart's update strategy
      is `maxSurge: 1` / `maxUnavailable: 0`, so a rolling update wants a third
      pod, and hard anti-affinity means it wants a third node with no Traefik
      pod on it. Three nodes satisfy that. With one of the three already
      cordoned, a Traefik upgrade *stalls* rather than dropping traffic, and
      resolves when the node returns. That is the right trade against two
      replicas quietly sharing a node on an ordinary Tuesday.
- [ ] 2.2 **CoreDNS to 2 replicas**, same anti-affinity. **Merged, then
      reverted — back to unstarted.** Everything below was written while it was
      merged and is left as written, because the reasoning is sound and the
      *mechanism* is what turned out to be wrong: as a partial server-side
      apply it passes a hand-run `kubectl apply --server-side
      --dry-run=server` and fails kustomize-controller's own drift-detection
      dry-run, which validated a merge result with no `spec.selector` in it.
      infra-config is the root of the Kustomization dependency chain, so one
      un-appliable manifest there stopped every application deploy in the
      cluster. The file keeps its full reasoning and the failure in its header;
      a second attempt starts from that difference rather than from this
      design. k3s owns this manifest,
      so the override is a `HelmChartConfig` beside Traefik's rather than an
      edit — an edit is reverted on the next k3s restart.

      **Both halves of that sentence are wrong, and finding out why produced a
      better mechanism than the one it replaced.** Landed as
      [`coredns-availability.yaml`](../../deploy/cluster/infrastructure/config/coredns-availability.yaml),
      which carries the full reasoning; the short version:

      - **A `HelmChartConfig` would do nothing.** k3s ships Traefik as a
        `HelmChart` CR, which is what gives `HelmChartConfig` something to
        layer onto. CoreDNS is not a chart at all — `kubectl get helmchart -A`
        returns only `traefik` and `traefik-crd`, while CoreDNS appears under
        `kubectl get addon -A` as a plain manifest at
        `/var/lib/rancher/k3s/server/manifests/coredns.yaml`. A
        `HelmChartConfig` named `coredns` would apply cleanly, be found by
        nothing, and change nothing — the worst of the available failures, and
        the same shape of trap as 1.2's `etcd-expose-metrics` except silent.
      - **An edit is *not* reverted, because k3s deliberately does not own
        `replicas`.** The packaged manifest declares no `spec.replicas`, so the
        Deployment comes up at Kubernetes' default of 1 and the count is left
        to the operator. That is readable in the object rather than inferred:
        k3s's deploy controller records everything it reconciles in a gzipped
        `objectset.rio.cattle.io/applied` annotation, and decoding it on the
        live Deployment gives `spec` keys `revisionHistoryLimit`, `selector`,
        `strategy`, `template` — no `replicas`. A field in neither the
        last-applied set nor the desired manifest is in no merge patch.
      - So the file is a **partial server-side apply**: a Deployment object
        carrying its identity and `spec.replicas` and nothing else. SSA
        validates the merged result, not the apply configuration, so the
        missing `selector` and `template` are supplied by the object already in
        the cluster and stay owned by k3s. Flux owns one field.
      - Verified against the live cluster with `kubectl apply --server-side
        --dry-run=server` under Flux's own field manager before committing. It
        reports one conflict on `.spec.replicas` with `deploy@aerie-node-1` —
        which claimed the field when it *created* the object, because the typed
        client fills in the API default before sending, and has never asserted
        it since. `fluxcd/pkg/ssa` passes `client.ForceOwnership`
        unconditionally on every apply (it is not gated on `spec.force`, which
        is the unrelated recreate-on-immutable-change mechanism), so
        kustomize-controller takes the field. The same dry-run with
        `--force-conflicts` returns `replicas: 2` with `kustomize-controller`
        as its sole manager and `deploy@aerie-node-1` still owning the rest.
      - **`kustomize.toolkit.fluxcd.io/prune: disabled` is load-bearing.**
        infra-config is `prune: true`; without the annotation, deleting or
        renaming that file would delete the cluster's only DNS Deployment, and
        k3s would not put it back until some node restarted, because its deploy
        controller re-applies an addon on manifest-checksum change and the
        manifest would not have changed. The object is not ours to delete; one
        field of it is ours to set.
      - **The anti-affinity was already there, and is better than what this
        step would have added.** k3s's own manifest gives the pod template a
        `topologySpreadConstraints` entry with `maxSkew: 1`,
        `topologyKey: kubernetes.io/hostname`, `whenUnsatisfiable:
        DoNotSchedule` — the same strength as Traefik's
        `requiredDuringScheduling`, and it keeps spreading instead of going
        unschedulable if the replica count ever exceeds the node count. It sits
        inside `spec.template`, which this file does not touch.
      - One addition beyond the step as written: a PDB for CoreDNS with
        `maxUnavailable: 1`, since k3s ships none and two replicas without one
        still lose both to two drains in the same window — which is precisely
        what a personal-mode entry on top of a running
        [`stagger-update-reboots.yml`](../../.github/workflows/stagger-update-reboots.yml)
        window would be.
- [ ] 2.3 **Resolve the zero-allowed-disruptions PDBs** from finding 4. Repair
      the observability stack first (`autokuma` `Init:Error`, `grafana` stuck
      initializing, `opensearch` 0/1 — these predate this plan), then re-read.
      If `aerie/api` still computes 0 with 3/3 ready, that is a bug to find, not
      a number to work around.

      **Re-read on 2026-08-25, and finding 4's caveat is mostly answered.** The
      observability stack repaired itself: every pod in `observability` is
      Running and Ready, `autokuma` and `grafana` included, and
      `opensearch-cluster-master-pdb` now reports 1 allowed disruption. So does
      `aerie/api`, at 3/3 ready with `currentHealthy: 3` and
      `desiredHealthy: 2` — it was the mid-repair reading, not a bug. Nothing
      to work around.

      Five PDBs still report zero, and all five are that way **by design**,
      which is a different answer from "unresolved":

      - `longhorn-system/instance-manager-*`, one per node, `minAvailable: 1`
        over a selector matching that node's single instance-manager pod.
        Longhorn's `node-drain-policy` is at its default
        `block-if-contains-last-replica`, and these PDBs are the mechanism it
        blocks *with*: its node controller removes the PDB once the node is
        cordoned and it has satisfied itself that no volume's last healthy
        replica is at stake. A standing zero on an uncordoned node is the
        resting state, not a blocker — but "Longhorn takes the PDB away when
        asked" is a claim 2.4 should watch happen rather than trust.
      - `aerie/aerie-pg-primary` and `immich/immich-pg-primary`,
        `minAvailable: 1` over the single primary. Per CloudNativePG's own
        upgrade documentation at the running 1.30.0, "if a node is to be
        drained and contains a cluster's primary instance, a switchover happens
        ahead of the drain" — the operator watches for the cordon and moves
        the primary, after which the PDB selects a pod on a different node and
        the drain proceeds. Again: something 2.4 should observe, with the timing
        written down, because it is the step most likely to dominate a
        personal-mode entry's 90-second budget.

      What is left of this item is therefore the *watching*, which is 2.4.
- [ ] 2.4 **A drain rehearsal.** Cordon and drain one permanent node, time it,
      confirm the house stays up, uncordon. This is the dress rehearsal for
      every future personal-mode entry, run against a node whose owner is not
      waiting to play a game.

      Do this **after** Flux has reconciled 2.1 and 2.2 and both show two
      Ready pods on two different nodes — a drain rehearsed against a single
      Traefik replica measures the old cluster. Three things to time and write
      down, because 4.1's 90-second deadline is a guess: how long Longhorn
      takes to drop the instance-manager PDB, how long CNPG's switchover takes,
      and how long the whole drain takes end to end.

      **Written as [`Invoke-DrainRehearsal.ps1`](../../scripts/k3s/Invoke-DrainRehearsal.ps1),
      dispatchable as *Node drain rehearsal*
      ([`drain-rehearsal.yml`](../../.github/workflows/drain-rehearsal.yml)).**
      It measures all three and prints them together at the end, so they can be
      copied into this file rather than reconstructed from a table. What
      writing it settled:

      - **The "after 2.1 and 2.2" precondition is a gate in the script, not a
        note in a plan.** It refuses to drain unless Traefik and CoreDNS each
        have two Ready pods on distinct nodes. `-ProceedWithSingletons`
        overrides it, and the report then says that is what happened — so a
        measured gap is a number about a known single point of failure rather
        than a surprise. With 2.2 currently reverted, that override is the only
        way this runs today, and the gap it measures is the argument for
        finishing 2.2.
      - **The probes run on a k3s server, not in a pod.** A node reaches a
        ClusterIP through the same kube-proxy rules a pod does, so CoreDNS can
        be queried at its ClusterIP directly and Traefik at the ingress VIP —
        no image pull, no scheduling. A rehearsal that had to schedule
        something *during a drain* would be measuring its own scaffolding.
      - **A DNS probe it could not run is a failure, not a skip.** The resolver
        is whichever of `dig`, `nslookup` or `busybox nslookup` the node has;
        with none of the three, the run reports that a DNS gap could not have
        been detected rather than reporting that there wasn't one.
      - **The whole rehearsal is one remote script.** The interesting events
        happen inside tens of seconds, and a probe whose sample interval is a
        Windows-to-Linux SSH round trip cannot see them. It samples once a
        second and hands back a transcript to be read.
      - **Probing continues past the drain** (`-SettleSeconds`, 30 by default).
        Ingress and DNS are disturbed by the rescheduling that *follows* an
        eviction, not by the eviction, and a probe that stopped at the drain's
        last second would miss exactly that.

## [] Phase 3 — Build the node

**Exit:** four nodes Ready; D holds no Longhorn replicas; the house is unchanged.

**This build is also the first exercise of the changed provisioning path.**
Provision 0 was rewritten in August 2026 to lay out disks differently — fixed OS
disk, its own volume argument, a 100 GB default, automatic checkpoints off at
creation — and *nothing has been dispatched through it since*. Every node that
exists was retrofitted by `Move-NodeOsDisk.ps1` rather than built correctly in
the first place, so "a node built today comes out right" is a claim nobody has
observed. The steps below verify it rather than assuming it, and a surprise
here is a finding about the tooling, not about host D.

The rule those changes encode, in one sentence: **the OS disk goes on the
host's fastest volume and is fixed; the Longhorn data disk goes on its largest
and is fixed; nothing Aerie creates on a host is dynamic, and no Aerie VM has
automatic checkpoints.** [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md)
carries it, along with how to measure which volume is which.

- [ ] 3.1 **Measure D's disk before choosing anything**, with the method in
      [`scripts/hyperv/README.md`](../../scripts/hyperv/README.md)'s "Choosing
      the OS disk's volume". "A few hundred GB free" does not say whether it is
      spinning or solid-state, and this plan should not guess: the answer sets
      the OS disk's destination volume and confirms (or overturns) finding 3's
      no-data-disk decision.

      Two cautions that section records from the three hosts already done, both
      of which cost a wrong reading there: do not measure the volume the
      documented default *says* the VM will be on, and do not accept "it is an
      SSD" as the answer. Compare every volume on the host against the same
      query — 6.8, 5.7 and 49 ms from the same workload shape is a statement
      about the media in a way any one of those numbers alone is not.

      Note that D needs `windows_exporter` running to be measurable this way,
      which 3.3's runner and a first Provision 0 dispatch install. Either
      dispatch `preflight_only` first to get the exporter on the box (that
      step runs before the provisioning step and is not skipped by the flag),
      or read the same underlying counter directly and take the Prometheus
      comparison later:

      ```powershell
      Get-Counter '\LogicalDisk(*)\Avg. Disk sec/Write' -SampleInterval 5 -MaxSamples 60
      ```
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

      **The repository half of this is done** — `hyperv-host-3` is a choice on
      all nine `provision-*` workflows and is in the stagger workflow's default
      host list, with the reason written there: that host takes Windows Updates
      like any other, and the unscheduled reboot it takes *is* finding 6. What
      is left is registering the runner itself on the machine, which is not a
      repository change. The `verify-*` workflows deliberately did **not** get
      the new label: their `host` input only picks which runner dispatches an
      SSH-based check, and pointing one at a machine that may be in personal
      mode makes a verification that fails for a reason unrelated to what it
      verifies.
- [ ] 3.4 **Dispatch Provision 0 with `preflight_only` first.** Cheap, and it
      exercises the rewritten free-space check before anything is built. That
      check now charges each volume separately rather than summing everything
      onto one — it has to, because `os_disk_path` and `data_disk_path` may be
      different volumes — and it charges the OS disk its **full fixed size**
      rather than the template's sparse bytes. With `data_disk_gb: 0` it should
      ask for `os_disk_gb` + ~2 GB on D's chosen volume and nothing anywhere
      else. A refusal here is a real answer about D's free space; a refusal
      that looks arithmetically wrong is a bug in that check, and worth
      stopping for.

- [ ] 3.5 Dispatch **Provision 0** for `aerie-node-3`: 16 GB static memory,
      `os_disk_path` set to the volume 3.1 chose, `os_disk_gb: 100`, and
      **`data_disk_gb: 0`**. Checked while doing Phase 1: `0` already means "no
      data disk" all the way through
      [`New-AerieVM.ps1`](../../scripts/hyperv/New-AerieVM.ps1),
      `Initialize-AerieNode.ps1` and `provision-0-new-node.yml` — the Phase 0
      scratch VM has always used it. Nothing to add.

      With no data disk, `data_disk_path` is irrelevant; leave it blank so it
      inherits `vm_storage_path` rather than implying a placement that never
      happens.

      D is a **fresh host with no golden image**, so this run builds the
      template rather than reusing one — which means it builds it at 100 GB
      directly and the per-VM resize below is a no-op. That is the *untested*
      half of the pairing: the three existing hosts all carry 32 GB templates,
      so a build on any of them exercises the resize instead. Note which one
      happened here.

- [ ] 3.6 **Verify the disk is what the rule says**, on the host, before the
      node is doing anything worth disturbing. This is the step that observes
      the claim nobody has observed yet.

      **This is now a command rather than three blocks to paste and read with
      your eyes**: [`Test-NodeVm.ps1`](../../scripts/hyperv/Test-NodeVm.ps1),
      dispatched as **Verify: Node VM shape**
      ([`verify-node-vm.yml`](../../.github/workflows/verify-node-vm.yml)). It
      asserts every expectation below and 3.8's as well, in one run, and it is
      worth running against the three permanent nodes too — the defects it
      looks for all look identical to a healthy node in every dashboard, which
      is the property that makes them worth a gate rather than a glance. The
      raw reads it replaced, for when the script is itself what is missing:

      ```powershell
      Get-VHD <os_disk_path>\aerie-node-3\os-disk.vhdx |
        Select-Object Path, VhdType, @{n='SizeGB';e={$_.Size/1GB}}, FileSize
      Get-VM aerie-node-3 |
        Select-Object Name, AutomaticCheckpointsEnabled, AutomaticStartAction, State
      Get-VM aerie-node-3 | Get-VMSnapshot
      ```

      Expected: `VhdType` **Fixed**, `SizeGB` **100**, `FileSize` at or near
      the full 100 GB rather than a sparse fraction of it,
      `AutomaticCheckpointsEnabled` **False**, and **no snapshots at all**.

      The checkpoint line is the one whose absence is hardest to notice: a node
      built with it left on looks identical in every dashboard and is quietly
      running on a dynamic differencing disk over its fixed one, which is the
      whole cost the fixed disk was meant to remove. It was a Hyper-V default
      that caught all three existing nodes.

      Also read the template's provenance, which now records its own
      subformat — it should say `dynamic` (the template is the one deliberate
      exception to the rule, because it is copied and never booted) and
      `sizeGB: 100`:

      ```powershell
      Get-Content <template_path>\debian-13-genericcloud.vhdx.provenance.json |
        ConvertFrom-Json | Select-Object sizeGB, vhdxSubformat, builtUtc
      ```

- [ ] 3.7 Set `-AutomaticStartAction Nothing` on the VM (finding 6). Everything
      else about the VM stays as the script builds it — including the
      automatic-checkpoint setting 3.6 just confirmed, which is *not* the same
      flag and must stay off.

      **Not a step after the build any more — an input to it.**
      `-AutomaticStartAction` is a parameter on
      [`New-AerieVM.ps1`](../../scripts/hyperv/New-AerieVM.ps1) and
      [`Initialize-AerieNode.ps1`](../../scripts/hyperv/Initialize-AerieNode.ps1),
      and an `automatic_start_action` choice on Provision 0, defaulting to
      `Start` so no existing dispatch changes behaviour. Two things that fell
      out of writing it:

      - `-AutomaticStopAction` is deliberately *not* parameterised beside it. A
        node should always be asked to shut down cleanly when its host is,
        whoever owns the machine; only the *return* is in question, and only
        here.
      - The **resume** path reconciles it. Unlike every cloud-init input, which
        is baked into a disk at creation and which a resumed VM can never pick
        up, the start action is a property of the VM object — so re-dispatching
        Provision 0 against an existing VM is how a node's start action gets
        changed. That is what keeps this a dispatch rather than a line in a
        runbook, and it means a permanent node can be converted to a part-time
        one without rebuilding it.

- [ ] 3.8 **Confirm the guest actually got the space.** Over SSH once cloud-init
      has finished and rebooted:

      ```bash
      df -h /
      lsblk
      ```

      Expect ~99 GB on `/` — growpart runs on first boot and expands the root
      filesystem into whatever the disk turned out to be, so this is the
      end-to-end check that the size survived template → convert → resize →
      boot. A root filesystem near 32 GB means the resize did not happen and
      `os_disk_gb` was silently ignored, which is the exact failure the
      per-VM resize exists to prevent.

      3.6's `Test-NodeVm.ps1` makes both of these assertions itself, against
      `-OsDiskSizeGB` rather than against a number written here, so one
      dispatch answers 3.6 and 3.8 together. It also asks the guest the data
      disk's absence a second way: Longhorn claims a raw unformatted disk, so
      "the host attached none" and "the guest sees none" are the same fact
      reached from two directions, and them disagreeing is worth knowing.

- [ ] 3.9 Dispatch **Provision 1** with `role: agent`, joining any permanent
      node's address.

      Two of the four node settings it reconciles have never been observed on a
      node built this way, and this run is where both are answered. The
      **journald cap** is written by
      [`cloud-init/user-data.tmpl.yaml`](../../scripts/hyperv/cloud-init/user-data.tmpl.yaml)
      on new nodes and byte-identically by
      [`Install-K3sNode.ps1`](../../scripts/k3s/Install-K3sNode.ps1) on existing
      ones — so on this node the script should report the file as **already
      matching** rather than rewriting it. If it rewrites, the two copies have
      drifted and they are supposed to be edited together. The **image GC**
      drop-in was confirmed during Phase 1 to apply to agents unchanged (k3s
      runs kubelet from the same tree on both roles); this is the first time
      that is true on a running agent rather than in a reading of the code.

      Both are verified by the run itself — it reads kubelet's own `/configz`
      through the apiserver and refuses unless it reports 70/55 — so a green
      run *is* the check. Read the log for the journald line rather than
      assuming it.

- [ ] 3.10 In Longhorn, set the node's `allowScheduling: false`. Confirm by
      reading back `nodes.longhorn.io/aerie-node-3` — and then confirm the thing
      that actually matters, that an existing `longhorn-r3` volume still reports
      three healthy replicas across A, B and C only.

- [ ] 3.11 Leave it empty for a few days and watch. Nothing is moved yet.

      Watch the root filesystem specifically. This node starts at ~100 GB with
      the image-GC ceiling already in place from its first boot, which is the
      condition the three existing nodes only reached by retrofit — so its slope
      over the first week is the cleanest reading anyone will get of whether
      that ceiling holds. If it is flat here while the node is doing real work,
      that is worth more than the deferred gate the older nodes produced.

## [] Phase 4 — Personal mode

**Exit:** an unelevated desktop user clicks a shortcut; within a minute the node
is drained, the VM is off and the runner is stopped. Another click returns all
three. Neither depends on remembering a command.

The control is [`scripts/hyperv/Set-PersonalMode.ps1`](../../scripts/hyperv/Set-PersonalMode.ps1),
with `-Enter`, `-Exit` and `-Status`, and the state lives in one file on the
host that both the script and the boot task read.

**All of it is written.** Two more actions than the plan named, both of which
turned out to be the boot task and the daily task rather than separate scripts:
`-Reconcile` (4.5) and `-AutoExit` (4.6), plus `-Pin`/`-Unpin` for 4.6's flag.
[`Register-PersonalModeControl.ps1`](../../scripts/hyperv/Register-PersonalModeControl.ps1)
installs the lot, and is dispatchable as **Provision 9**
([`provision-9-personal-mode.yml`](../../.github/workflows/provision-9-personal-mode.yml)).

Five things that fell out of writing it, each of which would otherwise have
been discovered on an evening somebody wanted their machine:

- **"Every step has a deadline" needed a mechanism, not an intention.**
  `kubectl --timeout` bounds the drain, but nothing bounds an `ssh` whose TCP
  connection is established and whose remote command never returns — which is
  exactly the shape of an unwell cluster, and exactly when a person is
  waiting. So every bounded step runs in a background job that is stopped at
  its deadline, which kills `ssh.exe` with it. One process start per call,
  about a second, for the difference between a promise and a hope.
- **The state file is written last, not first.** A run interrupted halfway
  therefore leaves the state saying `cluster`, and the boot task puts the node
  back. That is the safe direction to be wrong in: it costs a person one click,
  where the other direction costs the cluster a node it believes it has.
- **The boot task has work to do in the personal-mode branch too**, which
  "leave everything alone" hides. The runner service is Automatic, so Windows
  starts it on the way up; personal mode means the machine is the person's,
  including its CPU, so `-Reconcile` stops it again.
- **A SYSTEM task's console output is in session 0, where nobody can see it.**
  `schtasks /run` returns the instant the task launches, so a shortcut that
  only did that would flash a window and leave the person guessing for ninety
  seconds. 4.4's "feedback matters more than polish" is therefore a third
  script — [`lib/Watch-PersonalModeTask.ps1`](../../scripts/hyperv/lib/Watch-PersonalModeTask.ps1),
  which runs unelevated in the user's session, starts the task and tails the
  shared log. Reading a log file needs no privilege, so this crosses the
  session boundary without weakening the one 4.3 draws.
- **The VM is asked to stop in three escalating ways, and the guest is asked
  first.** Hyper-V's ACPI shutdown depends on the guest running the shutdown
  integration service, and this is a cloud image rather than a machine anybody
  configured — so `systemctl poweroff` over SSH is both likelier to work and
  cleaner when it does. `Stop-VM` is the fallback and `-TurnOff` is what the
  deadline buys. The hard turn-off is affordable here for a reason specific to
  this node: it holds no Longhorn replica (finding 3), so the worst case is a
  filesystem journal to replay.

- [x] 4.1 **Enter**, in order, each step with its own deadline and **none of
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
- [x] 4.2 **Exit** is the mirror, and is allowed to fail loudly: start the
      runner service, `Start-VM`, wait for `Ready`, `kubectl uncordon`, remove
      the label, clear the state file.
- [x] 4.3 **Registration**, once, by an administrator:
      `Register-PersonalModeControl.ps1` creates two Scheduled Tasks running as
      SYSTEM at highest privilege — modelled directly on
      [`Register-VmConsoleLogShipper.ps1`](../../scripts/hyperv/lib/Register-VmConsoleLogShipper.ps1),
      including its `ExecutionTimeLimit` lesson — and grants the desktop user
      run rights on them. This is the whole reason for the task indirection: the
      user needs to trigger an action requiring Administrator without holding
      Administrator and without a UAC prompt every time.

      Four tasks, not two: the boot task (4.5) and the daily auto-exit (4.6)
      are the same script under different triggers, and they keep the default
      administrator-only descriptor. A user who could run the boot task by
      hand could put the node back mid-evening, which is a strange thing to
      hand someone whose whole problem is wanting the machine to themselves.

      The run right is granted by **replacing** each task's security descriptor
      rather than appending an ACE to it — `D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;<sid>)`,
      through `IRegisteredTask.SetSecurityDescriptor`. Appending means parsing
      an SDDL whose default content varies by Windows build and by how the task
      was created, and getting that wrong on a task that runs as SYSTEM at
      highest privilege is worth designing out. `GRGX` is generic read plus
      generic execute; execute on a task is the right to run it, and there is
      no write, so the user cannot change what it does or who it runs as.

      The SSH key lands at `C:\ProgramData\Aerie\personal-mode\node.key`
      with inheritance stripped and two ACEs, SYSTEM and Administrators — the
      same shape as `lib/AerieSsh.ps1`'s `Protect-PrivateKeyFile`, for SYSTEM
      rather than for whoever ran the install, because the tasks are what use
      it. Windows OpenSSH refuses a key file other principals can read, and a
      file written under `ProgramData` inherits an ACL granting Users read, so
      this is a real refusal rather than a precaution. The desktop user never
      reads the key; they trigger a task and SYSTEM does.
- [x] 4.4 **The shortcuts.** Two on the desktop, calling
      `schtasks /run /tn Aerie-PersonalMode-Enter` (and `-Exit`), each opening a
      console that shows the steps and their timings and closes when done.
      Feedback matters more than polish here: the user needs to know when the
      machine is theirs, and a silent shortcut means they wait, or don't.
- [x] 4.5 **A boot-time task** (`AtStartup`, SYSTEM) reads the state file and
      reconciles: personal mode off and the VM down means start it; personal
      mode on means leave everything alone. This is what makes finding 6's
      Windows Update reboot harmless in both directions.
- [x] 4.6 **A daily auto-exit** at an hour nobody games, exiting personal mode
      unless a `pin` flag is set. Forgetting to give the node back should cost
      one night, not one month.
- [ ] 4.7 **Verify the ungraceful path**, because it is the one that will
      happen: pull the power mid-session. The house should stay up, the node
      should go `NotReady`, its pods should reschedule, and the boot task should
      bring it back correctly on the next power-on.

## [] Phase 5 — An absence that doesn't page anyone

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
