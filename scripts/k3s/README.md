# k3s

The cluster's own provisioning scripts, in the order they run:

| Script | Workflow | Scope |
|---|---|---|
| [`Install-K3sNode.ps1`](Install-K3sNode.ps1) | *Provision 1: Install k3s* | **once per node** |
| [`Set-ClusterConfig.ps1`](Set-ClusterConfig.ps1) | *Provision 4: Cluster configuration* | **once per cluster** |

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

TODO_SWARM.md Phase 2's first item: installs k3s server on one node that
Phase 1 already built, either forming the cluster's embedded etcd or joining
an existing one.

### Order of operations

1. Node 1 first, `role: cluster-init`. This forms the single-node embedded
   etcd cluster.
2. Node 2 next, `role: join`, `join_server` = node 1's LAN address.

Running node 2 before node 1 exists fails preflight: the workflow checks
`join_server:6443` answers before it ever touches the node being installed.

> Two-node embedded etcd has *worse* availability than one node (tolerates
> zero losses, not one). TODO_SWARM.md calls this build window explicitly
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
reason TODO_SWARM.md Phase 2 gives. To try a bump before merging, dispatch the
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
  a non-issue in practice — TODO_SWARM.md flags it as worth revisiting only
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

TODO_SWARM.md Phase 3b's first step: plants this installation's operator
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

### Next

- **Provision 5: node storage prep** — the next step, and the next one that
  genuinely runs **once per node**: it formats and mounts each node's
  Longhorn data disk. Everything after that is a commit under `deploy/`.
