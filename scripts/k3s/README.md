# k3s node install

Step 1 of the provisioning pipeline (TODO_SWARM.md Phase 2's first item):
installs k3s server on one node that Phase 1 already built, either forming
the cluster's embedded etcd or joining an existing one.

## Run this from the Actions tab, not by hand

**Actions → *Provision 1: Install k3s* → Run workflow** is how this runs.
Same reasoning as [`scripts/hyperv/README.md`](../hyperv/README.md#the-github-actions-workflow-is-the-way-to-run-this):
this repo's goal is infrastructure-as-code, so a dispatched, auditable run —
not a hand-typed SSH session — is what "installing a node" means here.
Running [`Install-K3sNode.ps1`](Install-K3sNode.ps1) directly is a fallback
for when the runner isn't reachable, not an equally-valid alternative; the
workflow is a thin wrapper that checks out this repo and calls the same
script with the same parameters, so the two can't drift.

The runner dispatching the workflow doesn't need to be the node being
installed — it only needs outbound SSH to the node (and, when joining, to
node 1's `:6443`). It reuses the same `hyperv-host-*` runners
[`scripts/hyperv/`](../hyperv/) already requires, so there's no new
prerequisite: whichever of the three you pick, it already has the OpenSSH
client Phase 1 needed for its own post-boot verification.

## Order of operations

1. Node 1 first, `role: cluster-init`. This forms the single-node embedded
   etcd cluster.
2. Node 2 next, `role: join`, `join_server` = node 1's LAN address.

Running node 2 before node 1 exists fails preflight: the workflow checks
`join_server:6443` answers before it ever touches the node being installed.

> Two-node embedded etcd has *worse* availability than one node (tolerates
> zero losses, not one). TODO_SWARM.md calls this build window explicitly
> non-production — the third server doesn't rejoin until Phase 7, when the
> current prod box is rebuilt as a node.

## One-time setup

| Name | Kind | What |
|---|---|---|
| `K3S_VERSION` | repository variable | Pinned k3s version, e.g. `v1.31.4+k3s1`. **Never** the latest/stable channel — see TODO_SWARM.md Phase 2 for why. Bump deliberately; every future node install reads this one value. |
| `K3S_CLUSTER_TOKEN` | repository secret | Generate once with `openssl rand -hex 32` *before* installing node 1. Identical across every server in the cluster — store it like the SSH keys, never in git. Rotating it means reinstalling every node. |
| `NODE_SSH_PRIVATE_KEY` | repository secret | Already set for [`provision-0-new-node.yml`](../../.github/workflows/provision-0-new-node.yml) — the same keypair Phase 1 baked into the node's `authorized_keys`. |

## What a run actually does

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

## Still manual

- **UDP 8472 (flannel VXLAN)** isn't checked by the join preflight — a TCP
  connect can't meaningfully probe a connectionless port. The TCP ports
  (6443, 2379-2380, 10250) are checked; see "Order of operations" above.
  Debian/Ubuntu cloud images ship with no firewall active, so this has stayed
  a non-issue in practice — TODO_SWARM.md flags it as worth revisiting only
  if that default ever changes.
- **Generating `K3S_CLUSTER_TOKEN`** — a one-time secret-bootstrap action,
  same tier as `NODE_SSH_PRIVATE_KEY` and Phase 0's `RESTIC_PASSWORD`.
- Everything after this: `age-keygen` for SOPS and `flux bootstrap` are the
  remaining Phase 2 items and aren't scripted yet.
