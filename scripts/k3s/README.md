# k3s

The cluster's own provisioning scripts, in the order they run:

| Script | Workflow | Scope |
|---|---|---|
| [`Install-K3sNode.ps1`](Install-K3sNode.ps1) | *Provision 1: Install k3s* | **once per node** |
| [`Set-ClusterConfig.ps1`](Set-ClusterConfig.ps1) | *Provision 4: Cluster configuration* | **once per cluster** |
| [`Initialize-NodeStorage.ps1`](Initialize-NodeStorage.ps1) | *Provision 5: Node storage* | **once per node** |
| [`Test-ClusterPlatform.ps1`](Test-ClusterPlatform.ps1) | *Verify: Cluster platform* | **read-only, any time** |

Between them sit [`scripts/secrets/`](../secrets/) (Provision 2) and
[`scripts/flux/`](../flux/) (Provision 3), which are also once per cluster.
Getting that column wrong is the usual confusion: anything that writes to
etcd — a Secret, a ConfigMap, Flux itself — is shared by every server the
moment one node accepts it, so running it again against a second node is a
no-op rather than a requirement. Only work on a node's own filesystem or
systemd is per node.

## Run these from the Actions tab, not by hand

**Actions → *Provision N* → Run workflow** is how each of these runs. Same
reasoning as [`scripts/hyperv/README.md`](../hyperv/README.md#the-github-actions-workflow-is-the-way-to-run-this):
this repo's goal is infrastructure-as-code, so a dispatched, auditable run —
not a hand-typed SSH session — is what "installing a node" means here.
Running the `.ps1` directly is a fallback for when the runner isn't
reachable, not an equally-valid alternative; each workflow is a thin wrapper
that checks out this repo and calls the same script with the same parameters,
so the two can't drift.

The runner dispatching a workflow doesn't need to be the node being worked on
— it only needs outbound SSH to that node (and, when joining, to node 1's
`:6443`). These reuse the same `hyperv-host-*` runners
[`scripts/hyperv/`](../hyperv/) already requires, so there's no new
prerequisite: whichever of the three you pick, it already has the OpenSSH
client Phase 1 needed for its own post-boot verification.

## Provision 1 — install k3s

[The cluster plan](../../docs/plans/swarm/phase-2-k3s-flux-secrets.md) Phase 2's first item: installs k3s server on one node that
Phase 1 already built, either forming the cluster's embedded etcd or joining
an existing one.

### Order of operations

1. Node 1 first, `role: cluster-init`. This forms the single-node embedded
   etcd cluster.
2. Node 2 next, `role: join`, `join_server` = node 1's LAN address.

Running node 2 before node 1 exists fails preflight: the workflow checks
`join_server:6443` answers before it ever touches the node being installed.

> Two-node embedded etcd has *worse* availability than one node (tolerates
> zero losses, not one). [The cluster plan](../../docs/plans/swarm/phase-2-k3s-flux-secrets.md) calls this build window explicitly
> non-production — the third server doesn't rejoin until Phase 7, when the
> current prod box is rebuilt as a node.

### One-time setup

| Name | Kind | What |
|---|---|---|
| `K3S_CLUSTER_TOKEN` | repository secret | Generate once with `openssl rand -hex 32` *before* installing node 1. Identical across every server in the cluster — store it like the SSH keys, never in git. Rotating it means reinstalling every node. |
| `NODE_SSH_PRIVATE_KEY` | repository secret | Already set for [`provision-0-new-node.yml`](../../.github/workflows/provision-0-new-node.yml) — the same keypair Phase 1 baked into the node's `authorized_keys`. |

Nothing else to set: the k3s version is **not** a variable. It's pinned in
[`scripts/versions.json`](../versions.json) and read from the checkout the run
was dispatched from, so the version is a property of the commit rather than of
the repository's settings — a node rebuilt from an old tag gets that tag's k3s.
Bump it in a commit; **never** point it at the latest/stable channel, for the
reason [the cluster plan](../../docs/plans/swarm/phase-2-k3s-flux-secrets.md) Phase 2 gives. To try a bump before merging, dispatch the
workflow from its branch.

### What a run actually does

1. **Preflight.** Resolves the SSH key, confirms the OpenSSH client is on the
   runner, confirms the node answers port 22, and — for `role: join` — that
   `join_server` answers 6443. Cheap failures before an install that
   downloads a binary and starts etcd.
1a. **Join preflight.** For `role: join`, also checks that `join_server`
   answers 6443 (apiserver), 2379-2380 (etcd client/peer), and 10250
   (kubelet) from the runner. Only proves those ports answer from the
   runner, not from the joining node itself — but a miss here is almost
   always a typo'd `join_server` or a firewalled node 1, worth catching
   before the remote install starts. UDP 8472 (flannel VXLAN) isn't checked;
   see "Still manual" below.
2. **Inspect.** Checks whether k3s is already active on the node. If so, the
   install is skipped (safe to rerun this workflow) unless `reinstall` is
   checked, which runs the node's own `k3s-uninstall.sh` first.
3. **Install.** Downloads the pinned install script to `/tmp/k3s-install.sh`
   on the node and runs it via `sudo env INSTALL_K3S_VERSION=... sh
   /tmp/k3s-install.sh server [--cluster-init | --server https://<node1>:6443]
   --disable servicelb --token ...` — `sudo env`, not a bare `sudo VAR=val`
   prefix, because most sudoers policies reset the environment before exec
   and would otherwise silently drop the version pin.
4. **Verify.** Polls until `k3s.service` is active and the node's own name
   shows `Ready` in `k3s kubectl get nodes`, then prints the full node list
   to the job summary.

### Still manual

- **UDP 8472 (flannel VXLAN)** isn't checked by the join preflight — a TCP
  connect can't meaningfully probe a connectionless port. The TCP ports
  (6443, 2379-2380, 10250) are checked; see "Order of operations" above.
  Debian/Ubuntu cloud images ship with no firewall active, so this has stayed
  a non-issue in practice — [the cluster plan](../../docs/plans/swarm/phase-2-k3s-flux-secrets.md) flags it as worth revisiting only
  if that default ever changes.
- **Generating `K3S_CLUSTER_TOKEN`** — a one-time secret-bootstrap action,
  same tier as `NODE_SSH_PRIVATE_KEY` and Phase 0's `RESTIC_PASSWORD`.

### What comes after

Both remaining Phase 2 steps are scripted, in the same dispatch-a-workflow
shape as this one:

- [`scripts/secrets/`](../secrets/) — **Provision 2: Seed secrets**. Pushes this
  installation's secrets into the parameter store and plants the ESO bootstrap
  Secret. (Replaces the `age-keygen` / SOPS step the plan originally called for
  — no secret bytes in git, encrypted or otherwise. See
  [`docs/ethos.md`](../../docs/ethos.md).)
- [`scripts/flux/`](../flux/) — **Provision 3: Bootstrap Flux**. After it, the
  cluster changes by commit rather than by command.

## Provision 4 — cluster configuration

[The cluster plan](../../docs/plans/swarm/phase-3-platform-services.md) Phase 3b's first step: plants this installation's operator
values in the cluster as the `aerie-cluster-config` ConfigMap in
`flux-system`, which every Kustomization under [`deploy/`](../../deploy/) then
reads through `postBuild.substituteFrom`.

### Why this exists

Flux reconciles from git, and [`docs/ethos.md`](../../docs/ethos.md) keeps
per-installation values out of git. Phases 0–2 passed `${DOMAIN}` and friends
as `vars.*` at deploy time, which a Flux-reconciled manifest has no
equivalent of — nothing in the original plan bridged that. This is the
bridge: the values arrive out of band, once, and the committed manifests stay
identical for every installation.

> **Run it before the first commit under `deploy/`.** A Kustomization whose
> substitution source is missing fails to reconcile rather than degrading
> gracefully, so planting the ConfigMap afterwards means watching the whole
> tree fail on a variable that was never going to be there yet.

### The seven variables

Seven repository **variables** — Settings → Secrets and variables → Actions →
*Variables*. Not secrets: an operator value isn't credential material, so it
stays unmasked and legible in run logs, and the job summary becomes a
readable record of what the cluster was configured with. (Reading a variable
through `secrets.` doesn't error — it resolves to an empty string — which is
why the script names the expected tab when a value comes back empty.)

| Variable | From | New in Phase 3? |
|---|---|---|
| `DOMAIN` | base domain | existing |
| `ACME_EMAIL` | Let's Encrypt account address | existing |
| `AWS_REGION` | region holding the `/aerie` SSM tree | existing |
| `INGRESS_VIP` | Phase 3a.2 — free address on the node subnet, outside the DHCP pool | **new** |
| `NODE_INTERFACE` | Phase 3a.3 — `ip -o -4 addr show scope global` on a node | **new** |
| `ROUTE53_HOSTED_ZONE_ID` | Phase 3a.4 — the `Z...` id alone, not `/hostedzone/Z...` | **new** |
| `LONGHORN_REPLICA_COUNT` | `2` during the two-node build window; Phase 7 raises it | **new** |

What each one is, what shape a valid value has, and which step first
substitutes it all live in [`cluster-config.json`](cluster-config.json) —
the same pointer-half-in-git split as
[`scripts/secrets/parameters.json`](../secrets/parameters.json). Add a key
there and the workflow only needs the matching `vars.` line; the script
learns the rest from the file.

### What a run actually does (Provision 4)

1. **Preflight.** Parses the map, resolves the SSH key, confirms the node
   answers 22, and checks every required value is set *and* syntactically
   valid — case-sensitively, which PowerShell's `-match` is not. Rejecting a
   value here is the entire point of the `pattern` field: a hosted zone id
   with `/hostedzone/` still on the front is otherwise diagnosed at step
   3b.10 as what looks like an AWS permissions failure. Either the whole
   ConfigMap is written or none of it is.
2. **Inspect.** Confirms the apiserver answers, then checks the two values
   the node itself can settle: that `NODE_INTERFACE` exists (listing the
   node's real interfaces if it doesn't), and that `INGRESS_VIP` sits on that
   interface's subnet and isn't the node's own address. kube-vip's ARP mode
   answers for the VIP on that interface's layer 2, so an address outside it
   installs cleanly and simply never answers. Then it diffs the live
   ConfigMap and prints what would change.
3. **Apply.** One `kubectl apply` of the rendered ConfigMap, over the SSH
   channel's standard input.
4. **Verify.** Reads it back and asserts every key arrived with the value
   that was sent.

`preflight_only` runs 1 and 2 and stops — the useful thing to dispatch before
changing a value on a cluster that's already serving, since the diff is
printed without anything being applied.

### Changing a value later

Set the repository variable, dispatch again. That's the whole procedure —
**no commit**, which is the entire reason these live in a ConfigMap rather
than in the manifests, and what Phase 7 relies on to raise
`LONGHORN_REPLICA_COUNT` to `3`. The next reconciliation picks it up on its
own interval; to not wait:

```sh
sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml \
  flux reconcile kustomization flux-system --with-source
```

Because every run applies the full key set, a key deleted from
`cluster-config.json` is also pruned from the cluster — the map stays the
whole truth rather than an append-only log. The run reports any such key
before it does it.

## Provision 5 — node storage

[The cluster plan](../../docs/plans/swarm/phase-3-platform-services.md) Phase 3b's second step, and the last one that runs **once per
node**: it prepares the Longhorn data disk Phase 1 attached and deliberately
left unformatted.

> **Run it before step 3b.11 installs Longhorn.** Longhorn's default data path
> is `/var/lib/longhorn` *on the root filesystem*, so a cluster that gets
> Longhorn first quietly fills every node's OS disk with replica data while the
> 200GB disk attached for exactly this purpose sits idle. The first symptom is a
> node under disk pressure, not a storage error.

Dispatch it once per node. Nothing it does is shared through etcd — a mounted
disk lives on one node's own filesystem — which is what puts it in the "once per
node" column with Provision 1 rather than with 2, 3 and 4.

### Finding the disk without naming it

The one thing this script will not do is take a device name. `/dev/sdb` is not
stable across reboots under Hyper-V, and a script that hardcodes it is one
disk-controller reorder away from formatting the wrong thing — which is also
why the `fstab` entry it writes is keyed by **UUID**. It identifies the disk
three ways instead, in descending order of certainty:

1. whatever is already mounted at `/var/lib/longhorn`;
2. otherwise, the device carrying the `longhorn` filesystem label — which
   survives a wiped fstab, a reordered controller, and a rebuilt OS disk;
3. otherwise, **the unpartitioned, empty disk of about `data_disk_gb`**.

Ambiguity is refused rather than resolved: two disks that could equally be it,
or two carrying the label, fails the run and names both. Two rules hold no
matter what, and `force` does **not** relax either — a disk with anything
mounted from it anywhere in its tree is never a candidate, and neither is the
one holding the root filesystem. `force` only allows wiping a disk that has a
partition table or a filesystem but is otherwise idle.

Before any of that, the node has to agree it is the node you named: the run
compares `vm_name` against the node's own hostname and stops on a mismatch,
since a wrong `ip_address` here means formatting a disk on the wrong machine.

### What a run actually does (Provision 5)

1. **Preflight.** Resolves the SSH key, confirms the OpenSSH client is on the
   runner, confirms the node answers port 22.
2. **Inspect.** One read-only probe collects the node's identity, its whole
   block-device tree, the current mount, `/etc/fstab`, and the package state.
   Everything below is decided from that one snapshot — eight round trips
   would be eight moments at which the node could change under a decision
   whose next action is `mkfs`. Prints the disk it picked and the plan.
3. **Packages.** `open-iscsi`, `nfs-common`, `cryptsetup`, then `systemctl
   enable --now iscsid`. These are *node* packages: Longhorn attaches volumes
   over iSCSI to the host, not into a container. `multipath-tools` is expected
   to be absent on a Debian cloud image; if it isn't, Longhorn's devices are
   blacklisted from `multipathd` rather than fought with — multipathd claiming
   them is the classic "volume stuck in Attaching" failure.
4. **Disk.** `mkfs.ext4 -m 0` (whole disk, no partition table), labelled
   `longhorn`, then an fstab entry keyed by UUID and the mount.
5. **Verify.** Re-reads the mount, the generated systemd mount unit, the fstab
   entry, `findmnt --verify` and `iscsid` from the node — the state Longhorn
   will actually find at 3b.11 — and checks the capacity is the disk that was
   asked for rather than the root filesystem.

`preflight_only` runs 1 and 2 and stops, printing which disk it would use and
what it would change. Re-running against a prepared node reports *nothing
needed changing* and means it: nothing is reformatted, remounted or rewritten.

### `nofail`, and the immutable mount point

The fstab entry is `defaults,nofail,x-systemd.device-timeout=30s`. Without
`nofail`, a missing or unreadable data disk stops the boot in emergency mode —
on a headless VM that is a node which is simply gone until someone opens
`vmconnect`. With it, the node boots, and the *empty directory underneath* the
mount is left `chattr +i` so nothing can quietly write Longhorn data to the OS
disk in the mount's absence. Longhorn fails loudly with `EPERM` instead, which
is the outcome worth engineering for. Mounting over an immutable directory is
unaffected — the flag governs writes through the inode, not the mount
namespace. One consequence: while something *is* mounted there, `lsattr` reads
the mounted filesystem's root rather than the directory underneath, so the
guard can't be inspected without unmounting.

The original `/etc/fstab` is copied to `/etc/fstab.aerie-orig` on the first run.

### Next

Everything after this is a commit under [`deploy/`](../../deploy/) — from here
the cluster changes by commit rather than by command.

## Verify — the Phase 3 gate

[The cluster plan](../../docs/plans/swarm/phase-3-platform-services.md) Phase 3b's last step. Steps 3b.1–3b.12 each carry an *Exit* line;
[`Test-ClusterPlatform.ps1`](Test-ClusterPlatform.ps1) asserts all of them in
one run and exits 0 or non-zero. "Phase 3 is done" should be something that
exits 0, not something remembered — and the same run is the smoke test after a
node rebuild, a restore, or before starting Phase 4.

**Read-only.** It writes nothing, to the cluster or to a node, so it is safe to
dispatch at any time and safe to re-run. That is also why its workflow is
*Verify: Cluster platform* rather than "Provision 6": every Provision workflow
changes something, and numbering a verification step into that sequence would
misdescribe it.

### It takes no repository variables

Everything it compares against comes from the cluster's own
`aerie-cluster-config` ConfigMap and from the two committed maps —
[`cluster-config.json`](cluster-config.json) and
[`parameters.json`](../secrets/parameters.json). Add a key to the first, or a
`kubernetes` block to the second, and this gate requires it without being
edited. The one input is `data_disk_gb`, which describes what Phase 1 attached
and must match what Provision 5 was given.

### Three properties that make it a gate

- **It does not stop at the first failure.** Every other script here throws
  immediately, correctly — their next action writes to a disk or to etcd. This
  one changes nothing, so it evaluates everything and prints one table. A gate
  that stops at the first failure costs one dispatch per problem.
- **A check it cannot evaluate is a failure, not a skip.** An absent object and
  an unreachable node both mean *not proven*, and an exit code that treats
  those as anything else can be satisfied by a cluster that is switched off.
- **Where a check is made from is part of the check.** The VIP answering,
  Traefik answering and the certificate being the production wildcard are all
  evaluated **from the runner, over the LAN** — a raw TLS handshake against
  `${INGRESS_VIP}:443` with `home.${DOMAIN}` as SNI, which is what
  `openssl s_client -servername` does and what no DNS record yet resolves. A
  node asked the same question can answer yes about its own loopback while
  every client on the LAN sees nothing, which is exactly the kube-vip failure
  3b.9 documents. Everything else is cluster state, read through
  `sudo k3s kubectl` on whichever server was named.

`3b.2`'s mount is checked on **every node the cluster reports**, not on the one
dispatched against — a mounted disk is not shared through etcd, so asking one
node has proven one third of the property.

### A roll-out in flight looks like a failure, so it is named as one

The gate takes one instantaneous sample. Push a commit and dispatch it a minute
later and `infra-config` reports
`DependencyNotReady: dependency 'flux-system/infra-controllers' revision is not
up to date` — which reads like a broken `dependsOn` and is nothing of the kind.
`Ready` is reported against whatever revision a layer last *applied*, while
`dependsOn` is enforced against the revision the *source* currently holds, so
the two disagree for as long as `infra-controllers` takes to roll the new
commit forward — up to its `timeout: 10m`, because `wait: true` holds it until
every HelmRelease under it is healthy.

The **Layers are at the committed revision** check exists to say which case it
is: it prints the GitRepository's revision and each layer's applied one, so a
lag is visible as a lag. It is still a failure — three Ready Kustomizations
pinned to last week's commit satisfy every other assertion in this file, and
"the cluster matches the tree" is what 3b.3 actually claims — but it is a
failure that clears by re-running once the roll-out lands, rather than one to
debug.

### The portability half

3b.13 also asks for the [portability
check](../../docs/plans/swarm/design.md#verification) over `deploy/`: the base domain, any
LAN address and the VIP should appear as `${...}` substitutions and nowhere
else. That splits in two, because the two halves need different things:

| Check | Where | Why there |
|---|---|---|
| No address literal anywhere under `deploy/` | [`ci.yml`](../../.github/workflows/ci.yml) | Needs no per-installation value, so it runs on every PR |
| Every `${TOKEN}` in the built output is a declared key | [`ci.yml`](../../.github/workflows/ci.yml) | Flux expands an undefined token to the empty string, so `${DOMIAN}` is not a failed reconciliation — it is an Ingress with no host |
| The base domain and VIP appear in no file under `deploy/` | this script | Needs the actual values, which [`docs/ethos.md`](../../docs/ethos.md) keeps out of the repository — the cluster's ConfigMap is the only place both halves exist at once |
