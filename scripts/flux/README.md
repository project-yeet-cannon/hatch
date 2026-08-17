# Flux install

Step 3 of the provisioning pipeline, and the last item in TODO_SWARM.md
Phase 2: installs Flux on the cluster and points it at this repository. After
this run, changing the cluster means committing to `deploy/` — there is no
further `kubectl apply` in the design.

## Why this doesn't run `flux bootstrap`

`flux bootstrap github` is the documented way to do this, and it is not used
here. Its convenience is that it **commits Flux's own manifests back to the
repository**, which is the one thing [`docs/ethos.md`](../../docs/ethos.md)
rules out:

- `gotk-sync.yaml` carries one installation's owner, repository, branch and
  path. A value true of exactly one install does not go in the artifact every
  install shares.
- `gotk-components.yaml` is ~10k lines and becomes a *second* pin for the Flux
  version [`scripts/versions.json`](../versions.json) already owns. A
  downstream operator bumping that file wouldn't move it, and every re-run
  rewrites it — a permanent merge conflict on the largest file in the tree.
- The deploy key it registers is a per-installation credential on the
  operator's own repository.

So the script does bootstrap's halves explicitly:

| | |
|---|---|
| `flux install` | the controllers, at the pinned version. Default component set — the same four bootstrap installs. |
| `flux create source git` | the `GitRepository`: url, branch, poll interval. |
| `flux create kustomization` | the `Kustomization`: path, `--prune`, apply interval. |

Those last two objects are what bootstrap would have serialized into
`gotk-sync.yaml`. They live in the cluster instead, declared from the
workflow's inputs. **This run writes nothing to git.**

The cost is that Flux no longer manages its own upgrade from a committed
manifest. In exchange, `versions.json` is the *only* place the Flux version
exists, and upgrading is "bump the line, re-dispatch this workflow" — the same
shape as every other pin in that file.

## Run this from the Actions tab, not by hand

**Actions → *Provision 3: Bootstrap Flux* → Run workflow**.

TODO_SWARM.md originally described this step as "run once from an operator
machine". That would have made it the only provisioning step in the repo with
no repeatable, auditable path — and the one that matters most to be able to
re-run, since it's what a rebuilt control plane needs. It's scripted instead,
same thin-wrapper shape as Provision 0-2.
[`Bootstrap-Flux.ps1`](Bootstrap-Flux.ps1) can still be run by hand from any
Windows box with the OpenSSH client; that's the fallback.

The install itself runs **on the node**, over SSH. k3s already has a
root-owned kubeconfig at `/etc/rancher/k3s/k3s.yaml`, so no cluster credential
is ever copied onto a runner.

## Order of operations

1. Provision 1 — at least one k3s server, Ready.
2. Provision 2 — the ESO bootstrap Secret exists. Not enforced by preflight
   (Flux installs fine without it), but Phase 3's first `HelmRelease` is ESO,
   and it can't sync without that Secret.
3. `path` exists on `branch`. [`deploy/cluster/kustomization.yaml`](../../deploy/cluster/kustomization.yaml)
   is committed for exactly this reason: bootstrap used to create the directory
   on its way past, and nothing does now.
4. This.

## One-time setup

| Name | Kind | What |
|---|---|---|
| `NODE_SSH_PRIVATE_KEY` | repository secret | Already set for Provision 0/1/2. |
| `FLUX_GITHUB_TOKEN` | repository secret | **Optional.** Only for a private repository: a PAT with **contents:read**, nothing else. Leave unset for a public one. |

That second row used to demand contents:**write** (to commit the manifests)
plus administration:**write** (to register a deploy key), which is why the
automatic `secrets.GITHUB_TOKEN` couldn't be used — it cannot be granted
administration:write. Neither scope is needed now. A public repository — which
is where this one is headed — is cloned anonymously and needs no credential at
all.

When a token *is* supplied it becomes the `flux-system` Secret
(`username=git`, `password=<pat>`) in the cluster, and the `GitRepository`
references it. Rotating it is a re-run.

The Flux version isn't a variable — it's pinned in
[`scripts/versions.json`](../versions.json) and read from the checkout, which
puts the controllers' version in the same commit as the manifests they
reconcile. Bump it in a commit.

Owner and repository aren't inputs — they come from the run's own context, so
the cluster can only ever be pointed at the repository it was dispatched from.

## What a run actually does

1. **Preflight.** SSH key resolves, the node answers 22 and 6443, and the
   pinned version is an exact release rather than a channel. It also proves the
   repository is clonable with whatever credential was supplied, by asking
   GitHub directly: with no token, whether it is anonymously readable (a
   definite 404 means private); with one, a `git-upload-pack` request carrying
   that exact credential (401 means the PAT was rejected, 403/404 that it
   carries no **contents:read** here). Either way the failure lands in preflight
   rather than as an opaque source-controller error four stages and fifteen
   minutes later. Any other answer — timeout, DNS, rate limit — says nothing
   about the repository and only warns; this runs on a home runner whose egress
   isn't the script's business.
2. **Install the flux CLI** on the node, at that exact version — skipped if
   it's already there. Downloaded to `/tmp/flux-install.sh` and run from disk
   rather than piped from a URL into a shell, same as the k3s install.
3. **`flux check --pre`** against the live apiserver.
4. **`flux install`** — the controllers, at the pinned version. Touches no
   credential of any kind.
5. **Point it at the repository** — the auth Secret if a token was supplied,
   then the `GitRepository` and the `Kustomization`. Both `flux create` calls
   block until the object is Ready, so a bad token or a missing path fails
   here.
6. **Verify** — `flux check`, then `flux get all --all-namespaces` into the job
   summary.

`flux install` upgrades in place and both `flux create` calls upsert, so
re-running after a version bump or a lost node is a normal thing to do, not a
repair.

## The token never reaches a command line

When there is one, it arrives on the remote shell's **standard input** and is
redirected straight into a `mktemp` file created `0600`, reaching `kubectl`
through `--from-file`. It never lands in a shell variable on the way. A
`--from-literal=password=…` — and equally a `$(cat tokenfile)` substitution,
which the shell expands *before* exec — would put it in the argv of a process
any local `ps` can read. A `trap` removes the file on every exit path. Same
discipline as Provision 2.

Everything in that remote script is quoted with `'`, never `"`, and
`Invoke-NodeSsh` now *rejects* a command containing a double quote. Windows
PowerShell 5.1 doesn't escape embedded double quotes when it builds `ssh.exe`'s
command line, and `ssh.exe`'s own argument parsing then strips them — so the
node would run a script that is subtly not the one that was written, with no
error anywhere. That is not theoretical: it is what broke this stage. `tr -d
"\r\n"` arrived as `tr -d rn`, silently deleting every `r` and `n` from the
token, and GitHub answered 401 on a PAT that was perfectly good.

## The token crosses as base64

Standard input is a *text* channel, and PowerShell picks its encoding from
ambient state no script here owns. On this repo's runner a piped string reaches
the node with a UTF-8 BOM (`EF BB BF`) welded to its front — pinning
`$OutputEncoding` to a BOM-less encoding does not stop it, so something below
that variable is emitting the preamble. The PAT went into the `flux-system`
Secret three bytes too long, preflight passed because it encodes the
in-process string and never touches the pipe, and `source-controller` answered
`401` fifteen minutes later.

Note *why* only this step noticed. Provision 2 pipes a YAML manifest through
the same helper and is unharmed, because the YAML spec explicitly permits a
byte order mark at the start of a stream. A credential permits nothing.

Chasing each encoding artifact as it turns up is a losing game — the CRLF was
the first, the BOM the second, and every future runner's console settings get a
turn — so the token is base64-encoded before the pipe and decoded on the node:

```sh
tr -dc 'A-Za-z0-9+/=' | base64 -d > $AERIE_TOKEN_FILE
```

`tr -dc` keeps only the base64 alphabet, so a BOM, a CR, an LF, or the
interleaved NULs of a UTF-16 conversion are all dropped without any of them
having to be anticipated by name. What comes out is byte-for-byte what preflight
validated against GitHub, or nothing.

A `grep -qE '^[A-Za-z0-9_]+$'` still guards the decoded result, even though the
transport should make it unreachable. That is the point of it: every GitHub PAT
is drawn from that set, so if it ever fires, the transport is wrong — and it
says so in seconds, at the node, instead of as a `401` a quarter of an hour
later inside a controller.

## After it succeeds

Nothing new appears in this repo — that's the point. The cluster holds a
`GitRepository` and a `Kustomization` pointing at `deploy/cluster`, and Phase 3
starts by adding the External Secrets Operator `HelmRelease` under that path,
as a commit rather than a command.

To see what the cluster thinks it's syncing:

```sh
sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml flux get sources git
sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml flux get kustomizations
```

For the steady state this bootstraps into — what a push to `main` actually
triggers, the poll and reconcile intervals, what still needs a dispatched
workflow instead of a commit, and how to debug a change that hasn't landed —
see [`docs/delivery-architecture.md`](../../docs/delivery-architecture.md).

## Still manual

- **Choosing which node to install from.** `kubectl` and Flux target one
  node's IP directly; there's no VIP in front of the apiserver. See the known
  gap in [`docs/secrets-architecture.md`](../../docs/secrets-architecture.md#known-gap-no-vip-in-front-of-the-apiserver).
- **Creating the PAT**, in the private-repository case only. One-time, same
  tier as generating `K3S_CLUSTER_TOKEN`.
