# Flux bootstrap

Step 3 of the provisioning pipeline, and the last item in TODO_SWARM.md
Phase 2: installs Flux on the cluster and points it at this repository. After
this run, changing the cluster means committing to `deploy/` — there is no
further `kubectl apply` in the design.

## Run this from the Actions tab, not by hand

**Actions → *Provision 3: Bootstrap Flux* → Run workflow**.

TODO_SWARM.md originally described this step as "run once from an operator
machine". That would have made it the only provisioning step in the repo with
no repeatable, auditable path — and the one that matters most to be able to
re-run, since it's what a rebuilt control plane needs. It's scripted instead,
same thin-wrapper shape as Provision 0-2.
[`Bootstrap-Flux.ps1`](Bootstrap-Flux.ps1) can still be run by hand from any
Windows box with the OpenSSH client; that's the fallback.

The bootstrap itself runs **on the node**, over SSH. k3s already has a
root-owned kubeconfig at `/etc/rancher/k3s/k3s.yaml`, so no cluster credential
is ever copied onto a runner.

## Order of operations

1. Provision 1 — at least one k3s server, Ready.
2. Provision 2 — the ESO bootstrap Secret exists. Not enforced by preflight
   (Flux installs fine without it), but Phase 3's first `HelmRelease` is ESO,
   and it can't sync without that Secret.
3. This.

## One-time setup

| Name | Kind | What |
|---|---|---|
| `FLUX_GITHUB_TOKEN` | repository secret | PAT with **contents:write** (commit the manifests) and **administration:write** (register the deploy key Flux authenticates with from then on). Add workflows:write only if the reconciled path ever holds workflow files. Classic equivalent: `repo`. |
| `NODE_SSH_PRIVATE_KEY` | repository secret | Already set for Provision 0/1/2. |

The Flux version isn't a variable — it's pinned in
[`scripts/versions.json`](../versions.json) and read from the checkout, which
puts the controllers' version in the same commit as the manifests they
reconcile. Bump it in a commit.

The automatic `secrets.GITHUB_TOKEN` **cannot** be used: it can't be granted
administration:write, so it can't register the deploy key.

Owner and repository aren't inputs — they come from the run's own context, so
the cluster can only ever be pointed at the repository it was dispatched from.

## What a run actually does

1. **Preflight.** SSH key resolves, the node answers 22 and 6443, the PAT is
   present, and the pinned version is an exact release rather than a channel.
2. **Install the flux CLI** on the node, at that exact version — skipped if
   it's already there. Downloaded to `/tmp/flux-install.sh` and run from disk
   rather than piped from a URL into a shell, same as the k3s install.
3. **`flux check --pre`** against the live apiserver.
4. **`flux bootstrap github`** — installs the controllers, commits the
   `flux-system` manifests to `path` on `branch`, and registers the deploy key.
5. **Verify** — `flux check`, then `flux get all --all-namespaces` into the job
   summary.

`flux bootstrap` converges an existing install, so re-running after a version
bump or a lost node is a normal thing to do, not a repair.

## The PAT never reaches a command line

It arrives on the remote shell's **standard input** and is read into a variable
with `export`, a shell builtin. `sudo env GITHUB_TOKEN=...` — and equally a
`$(cat tokenfile)` substitution, which the shell expands *before* exec — would
put the token in the argv of a process that lives for the whole multi-minute
bootstrap, where any local `ps` can read it.

That's also why flux doesn't run under `sudo` here: sudo scrubs the
environment. The root-owned kubeconfig is copied to a user-owned `mktemp` file
(created `0600`) that a `trap` removes on every exit path.

## After it succeeds

`deploy/cluster/flux-system/` appears in this repo, committed by Flux itself.
Phase 3 starts by adding the External Secrets Operator `HelmRelease` next to
it — as a commit, not a command.

## Still manual

- **Creating the PAT.** One-time, same tier as generating
  `K3S_CLUSTER_TOKEN`.
- **Choosing which node to bootstrap from.** `kubectl` and Flux target one
  node's IP directly; there's no VIP in front of the apiserver. See the known
  gap in [`docs/secrets-architecture.md`](../../docs/secrets-architecture.md#known-gap-no-vip-in-front-of-the-apiserver).
