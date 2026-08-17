# Delivery Architecture

How a commit becomes a running thing — and why the answer is different depending
on which half of the system you're changing.

## Summary

Aerie currently has **two independent delivery paths**, and they work in
opposite directions:

| | Legacy Windows host | k3s cluster |
|---|---|---|
| What runs there | The whole production stack today — API, kiosk `files`, Postgres, observability, backup | Infrastructure controllers only, so far |
| Defined by | `compose.*.yml` at the repo root | `deploy/cluster/**` |
| Delivery mechanism | GitHub Actions **pushes** to the host | Flux **pulls** from GitHub |
| Triggered by | Push to `main` → build → deploy workflow | Push to `main` — no workflow involved |
| Credential direction | The runner holds a Docker socket on the host | The cluster holds a read-only git credential |
| Where the deploy logic lives | [`.github/workflows/cd.yml`](../.github/workflows/cd.yml) | In the cluster, as `GitRepository` + `Kustomization` objects |

This split is temporary by design. [the cluster plan](plans/swarm/phase-7-cutover.md) Phase 7 is
the cutover that retires the left column. Until then, **the deploy workflow never
touches Kubernetes and Kubernetes never reads the deploy workflow.**

If you only remember one thing: **`cd.yml` does not deploy the cluster.** Every
`kubectl`-shaped verb in this system is performed by a controller running inside
the cluster, reacting to what's in git.

## If you're new to Kubernetes

You've built and shipped SaaS; the vocabulary below is the part that's new. This
section is the minimum needed to read the rest of this document. Skip it if
you've used Flux before.

**Kubernetes is a reconciliation engine, not a deployment target.** This is the
single biggest mental shift from Compose, and everything else follows from it.
With `docker compose up`, *you* make the change: the CLI computes a diff and
imperatively creates and destroys containers, then exits. Kubernetes has no
equivalent moment. You write down a *desired state* — "I want three replicas of
this image" — and a control loop inside the cluster continuously compares desired
against actual and takes whatever action closes the gap. Forever. Kill a
container by hand and something puts it back, because you never told the cluster
to stop wanting it. There is no "deploy finished" event, only "actual currently
matches desired."

**A "controller" is one of those loops.** Kubernetes ships with controllers for
its built-in types (Deployments, Services). Everything else in
[`deploy/cluster/infrastructure/controllers/`](../deploy/cluster/infrastructure/controllers/)
— cert-manager, External Secrets, Longhorn, kube-vip, CloudNativePG — is a
third-party
controller you install, each of which watches for its own object types and acts
on them. cert-manager watches for `Certificate` objects and goes and gets TLS
certificates. External Secrets watches for `ExternalSecret` objects and copies
values out of AWS SSM into Kubernetes `Secret`s.

**A "CRD" is how a controller teaches the cluster a new noun.** Custom Resource
Definition. Installing cert-manager registers the type `Certificate` with the
API server; only after that can a `Certificate` object be *accepted*, let alone
acted on. This is why ordering matters and why
[`infrastructure.yaml`](../deploy/cluster/infrastructure.yaml) splits into two
layers: you cannot apply an instance of a type nothing has defined yet. The error
you'd get is a flat rejection at the API server — `no matches for kind
"Certificate"` — not a retry.

**Helm is the package manager; a `HelmRelease` is a declarative install of a
chart.** A chart is a parameterized bundle of manifests. Instead of running
`helm install` by hand, you commit a `HelmRelease` object and Flux's
helm-controller performs the install and keeps it converged.

**Flux is the controller that makes git the desired state.** It adds two types
that matter here:

- **`GitRepository`** — "clone this repo/branch, re-check every *N*, and publish
  the current commit as an artifact for others to consume." It is only a source.
  It applies nothing.
- **`Kustomization`** — "take path *P* out of that source, build it, apply it to
  the cluster, and keep it applied." This is the thing that actually writes to
  the cluster. (Confusingly, there is also an unrelated *upstream* `kustomize`
  tool with a `kustomization.yaml` file format — that's the plain YAML-templating
  thing in each directory. Flux's `Kustomization` is a different object with the
  same name. Both appear in this repo; the file
  [`deploy/cluster/kustomization.yaml`](../deploy/cluster/kustomization.yaml) is
  the former, and the objects in
  [`infrastructure.yaml`](../deploy/cluster/infrastructure.yaml) are the latter.)

**"Reconcile" is the verb for one pass of the loop.** When you read "Flux
reconciles every 10m," it means the loop re-runs on that cadence even if nothing
changed — re-applying identical YAML, which is a no-op the API server absorbs
cheaply. It is *not* the deploy latency for your change; see the timeline below.

**"Drift" is actual state diverging from git**, usually because a human ran
`kubectl edit` on something. Reconciliation is what corrects it, which is why
hand-editing a live object is a temporary act — the next pass overwrites you.

**GitOps** is the umbrella name for this arrangement: git holds desired state,
an in-cluster agent pulls it, and there is no privileged CI pipeline with a
foothold in the cluster.

## Path 1 — the legacy Windows host

Push-based, and conventional. Three workflows chain by event:

1. **Push to `main`** →
   [`publish.yml`](../.github/workflows/publish.yml) builds each container image
   and pushes it to `ghcr.io`, tagged with the commit SHA.
2. **On that workflow succeeding** →
   [`cd.yml`](../.github/workflows/cd.yml) fires via `workflow_run`. It does not
   trigger on push directly, which is what guarantees a deploy never runs against
   images that were never published.
3. `cd.yml` runs on the `legacy-deployer` self-hosted runner — a runner
   physically on the Docker host — and does `docker compose pull` then
   `docker compose up -d`, with `IMAGE_TAG` pinned to
   `github.event.workflow_run.head_sha` so the deploy pulls exactly the images
   step 1 built.

Everything operator-specific arrives as `vars.*` / `secrets.*` in that
workflow's `env:` block and is interpolated into the compose files. That's the
mechanism [`docs/ethos.md`](ethos.md) describes for keeping values out of git,
on this path.

Note what the runner has: `DOCKER_HOST: tcp://localhost:2375`. A GitHub Actions
job holds an open socket to the production Docker daemon. That is normal for
push-based CD and it is exactly what the cluster path is designed to avoid.

## Path 2 — the k3s cluster

**Nothing in GitHub Actions deploys to the cluster.** The provisioning workflows
(`provision-0` through `provision-5`) are `workflow_dispatch` only — you run them
by hand, from the Actions tab, and they build or configure *nodes*.
[`provision-3-bootstrap-flux.yml`](../.github/workflows/provision-3-bootstrap-flux.yml)
is the last one that has anything to do with application delivery, and it runs
once: it installs Flux and points it at this repository. After that the cluster
is self-driving, and as its header says — *"changing the cluster means committing
to `deploy/` — there is no further kubectl apply in the design."*

### What was created at bootstrap

Two objects, living in the cluster's `flux-system` namespace, created by
[`Bootstrap-Flux.ps1`](../scripts/flux/Bootstrap-Flux.ps1):

| Object | Configuration | Meaning |
|---|---|---|
| `GitRepository/flux-system` | url, branch `main`, `--interval=1m` | Poll GitHub once a minute for a new commit |
| `Kustomization/flux-system` | `--path=./deploy/cluster`, `--prune`, `--interval=10m` | Apply that directory, and keep it applied |

These are deliberately **not** committed to the repo — they encode one
installation's owner/repo/branch, which [`docs/ethos.md`](ethos.md) rules out.
That is also why this repo runs `flux install` rather than `flux bootstrap`; see
[`scripts/flux/README.md`](../scripts/flux/README.md) for the full reasoning.

### The timeline of a push

You commit a change to `deploy/cluster/**` and push to `main`:

| Elapsed | What happens |
|---|---|
| 0s | GitHub has the commit. Nothing in the cluster knows yet. |
| ≤60s | The `GitRepository` poll finds the new revision, clones it, and publishes it as a new source artifact. |
| immediately after | kustomize-controller **watches** the source, so a revision change triggers reconciliation at once. It does *not* wait for the 10m interval. |
| +seconds | The root `Kustomization` builds `deploy/cluster` and applies it — which, since that path contains only pointers, means applying the two child Kustomizations in [`infrastructure.yaml`](../deploy/cluster/infrastructure.yaml). |
| then | `infra-controllers` reconciles, and because it carries `wait: true`, it does not report Ready until every object it applied is *healthy* — chart installed, CRDs registered, DaemonSets up. Budgeted at `timeout: 10m` because Longhorn is slow on a cold node. |
| then | `infra-config` reconciles, gated behind `dependsOn: infra-controllers`. |

So: **typically under two minutes for a trivial change**, and bounded below by
the 1-minute source poll. The 10m / 30m intervals on the Kustomizations are the
*idle* re-apply cadence — drift correction — not your deploy latency.

The intervals are chosen accordingly. `infra-controllers` sits at 30m precisely
because re-applying an unchanged `HelmRelease` every ten minutes accomplishes
nothing; drift *inside* a Helm release is helm-controller's job on its own
schedule, and a git change is picked up by the source poll regardless.

### The two-layer split

[`infrastructure.yaml`](../deploy/cluster/infrastructure.yaml) defines exactly
one ordering constraint, and it's the CRD constraint from the primer above:

```
infra-controllers   (path: infrastructure/controllers/,  wait: true)
        │             HelmReleases: External Secrets, cert-manager,
        │             kube-vip, Longhorn, CloudNativePG — these
        │             register the types
        ▼
infra-config        (path: infrastructure/config/,  dependsOn: infra-controllers)
                      Instances of those types: ClusterSecretStore,
                      ExternalSecrets, ClusterIssuers, the wildcard
                      Certificate, StorageClasses, Traefik's HelmChartConfig
```

`wait: true` on the first layer is what makes the boundary real. Without it a
`Kustomization` reports Ready as soon as its objects are *applied* — for a
`HelmRelease` that means "the CR exists," not "the chart installed and its CRDs
landed." `dependsOn` would then be waiting on a lie.

### `prune: true`, and what it means for your workflow

Both Kustomizations set `prune: true`. **Deleting a manifest from git deletes the
object from the cluster** on the next reconcile. This is the property that makes
the repo the whole truth about the cluster rather than an append-only log of
things once applied — but it means `git rm` is a destructive production action on
this path in a way it never is on the Compose path.

### How operator values reach the manifests

Flux reconciles from git, and git may not hold this installation's domain, VIP,
or hosted zone id. The bridge is
[`provision-4-cluster-config.yml`](../.github/workflows/provision-4-cluster-config.yml),
which plants an `aerie-cluster-config` ConfigMap in the cluster out of band. Both
Kustomizations then carry `postBuild.substituteFrom` pointing at it, so a
manifest can write `${DOMAIN}` and have it resolved at apply time.
[`scripts/k3s/cluster-config.json`](../scripts/k3s/cluster-config.json) is the
committed half — it names every key and what a valid value looks like, but holds
no values.

Two sharp edges worth internalizing before you write a manifest under `deploy/`:

- **Substitution is not scoped to tokens you chose.** Flux expands *every* `$VAR`
  and `${VAR}` in the built output, including ones belonging to some upstream
  chart — Prometheus rule bodies with `$labels`, shell in a ConfigMap, anything
  with a bare `$`. Escape those as `$$` or annotate the object
  `kustomize.toolkit.fluxcd.io/substitute: disabled`.
- **An undefined token expands to an empty string, not an error.** `${DOMIAN}` is
  not a failed reconciliation; it's an Ingress with no host, which fails much
  later and looks like something else.

Changing an operator value is therefore **not** a commit: set the repository
variable, re-dispatch Provision 4, and the next reconciliation picks it up. This
is how Phase 7 raises `LONGHORN_REPLICA_COUNT` from 2 to 3.

## Why pull instead of push

Worth stating plainly, since the push model in Path 1 is the more familiar one
and the cluster deliberately abandons it.

**The credential direction inverts.** Push-based CD requires the pipeline to hold
production credentials — `cd.yml` holds a Docker socket on the host. A cluster
equivalent would mean a kubeconfig with write access sitting in GitHub secrets,
reachable by any workflow, any compromised action, any fork with a clever PR.
Pull-based means the cluster holds a *read-only* credential to git and nothing
outside holds a credential to the cluster. Note that Provision 3's own token
requirement dropped to `contents:read` — and for a public repo, to nothing at
all — precisely because the design stopped needing to write.

**Drift correction is continuous, not per-deploy.** A push-based deploy converges
the cluster once, at deploy time, and says nothing about what happens between
deploys. Reconciliation runs whether or not anyone pushed, so a hand-edit or a
crashed operator is corrected on the next pass rather than surviving until the
next release.

**Recovery is a rebuild, not a replay.** With the cluster's desired state entirely
in git, rebuilding a control plane is Provision 1–4 plus "let Flux catch up." There
is no need to re-run a year of pipeline history in order.

**The cost, honestly:** you lose the single green checkmark that means "it
shipped." Push completes long before the change lands, failures surface in
cluster state rather than in a workflow log, and there is no build artifact
gating the apply — a syntactically valid but wrong manifest reaches the cluster
without anything having tried it first. The observability section below is how
you get that feedback back.

Be precise about what "failures surface in cluster state" means, because three
unlike things wear the same `Ready=False` condition. A **build** failure applies
nothing and prunes nothing — the cluster keeps serving the last good state. An
**apply** failure lands the objects it can and reports the rest, leaving a
cluster no single commit describes. And an object can apply perfectly and never
become healthy, which needs no push at all: helm-controller can fail an upgrade
on its own interval hours after a commit that was fine. All three retry forever
and none of them tell anyone. [the cluster plan](plans/swarm/phase-3-platform-services.md) Phase 3b.14 is
where that gets closed — two pre-merge checks against the first two, and a Phase
6 alert on `gotk_reconcile_condition` for the third, which is the only one no
gate in front of a merge can reach.

## What a push does *not* do

| Change | How it actually reaches the cluster |
|---|---|
| Operator values (`DOMAIN`, `INGRESS_VIP`, `LONGHORN_REPLICA_COUNT`, …) | Set the repository variable, re-dispatch **Provision 4** |
| Operator secrets (Route53 creds, HA token, …) | Re-dispatch **Provision 2**, which seeds AWS SSM; External Secrets syncs them in |
| The Flux version | Bump [`scripts/versions.json`](../scripts/versions.json), re-dispatch **Provision 3** |
| The k3s version | Bump `versions.json`, re-run **Provision 1** per node |
| Adding a node | **Provision 0** → **1** → **5** |
| The `GitRepository` / root `Kustomization` themselves | They live in the cluster, not git — re-dispatch **Provision 3** |

Also note: **there is no image automation on the cluster path.** Flux *can* watch
a registry and rewrite image tags into git (`ImageUpdateAutomation`); this repo
does not configure it. When app workloads move to the cluster in Phase 5, an
image tag will be a value in a manifest, so shipping new application code will
mean a commit that changes that tag — not merely a `publish.yml` run. Today this
is moot, because the cluster runs no application images.

Two more consequences of the push path being uninvolved:

- **A cluster change lands even if CI is red.** Nothing gates the Flux path on
  [`ci.yml`](../.github/workflows/ci.yml) or `publish.yml`. Branch protection on
  `main` is the only gate that exists.
- **Uncommitted work is invisible.** Files sitting in your working tree under
  `deploy/` do not exist as far as the cluster is concerned — including
  `git add`-ed but unpushed ones. The cluster reads `origin/main`, not your disk.

## Watching it happen

There's no workflow run to open, so this is how you get feedback. The `flux` CLI
and the kubeconfig both live **on a node**, not on your machine — k3s writes a
root-owned kubeconfig at `/etc/rancher/k3s/k3s.yaml`, and Bootstrap-Flux
installed the CLI there. So SSH to a k3s server first:

```sh
ssh <user>@<node-ip>
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml   # or prefix each command with sudo env KUBECONFIG=...
```

Then, roughly in order of usefulness:

```sh
# The one-screen answer to "is everything converged?"
flux get all -A

# Is the cluster seeing my commit at all? Check the revision against your SHA.
flux get sources git -A

# Which layer is stuck, and what does it say?
flux get kustomizations -A

# Stop waiting for the poll — pull now. --with-source re-fetches git first.
flux reconcile kustomization flux-system --with-source

# The actual error, when the one-line status isn't enough.
kubectl describe kustomization -n flux-system infra-config
kubectl -n flux-system logs deploy/kustomize-controller -f

# Helm chart install failures live here instead.
flux get helmreleases -A
kubectl -n flux-system logs deploy/helm-controller -f
```

### Reading the common failures

| Symptom | Usual cause |
|---|---|
| Source revision is behind your SHA | Pushed to the wrong branch, or the poll hasn't fired yet (≤60s) |
| `infra-config` NotReady, `infra-controllers` Ready | Genuine object failure — a `Certificate` stuck on DNS-01, an `ExternalSecret` that can't reach SSM |
| `infra-controllers` NotReady with a timeout | `wait: true` gave up at 10m; usually Longhorn still rolling out on a cold node, so check whether it's failure or slowness |
| `no matches for kind "X"` | A layer-2 object whose CRD isn't installed — something is in `config/` that should be in `controllers/`, or its controller failed |
| `substitution variable not set`, or a resource with an empty field | Missing or misspelled key vs. `cluster-config.json`; check the ConfigMap exists — a missing one fails loudly on purpose |
| An object you deleted from git is gone from the cluster | `prune: true` working as intended |

An `infra-config` sitting NotReady during the deliberate staging-issuer stop in
Phase 3b.10 is the *expected report*, not a fault.

## Mental model, mapped

For an application architect coming from managed SaaS platforms:

| You know | Cluster equivalent |
|---|---|
| CD pipeline stage that deploys | Doesn't exist. The cluster pulls. |
| "Deploy finished" event | "`Kustomization` reports Ready" — an ongoing condition, not an event |
| Rollback = redeploy previous build | `git revert` and push; reconciliation carries it in |
| Environment variables in a deploy config | `${...}` substituted from the `aerie-cluster-config` ConfigMap |
| Secret manager binding | `ExternalSecret` pointing at an AWS SSM path; ESO materializes a `Secret` |
| Load balancer + TLS termination | Traefik + kube-vip VIP; cert-manager issues the wildcard cert |
| Blue/green or rolling deploy config | A `Deployment`'s own rolling update strategy — the platform's default, not a pipeline concern |
| Infrastructure-as-code apply step | The reconciliation loop, running continuously rather than on invocation |
| "Someone hotfixed prod by hand" | Drift — and it gets reverted automatically on the next pass |

## Where this is going

The two paths converge at [the cluster plan](plans/swarm/phase-7-cutover.md) Phase 7. Phases 4–6
move the data tier (CloudNativePG), app tier, and observability into
`deploy/cluster/`; Phase 7 points DNS at the cluster VIP and retires the Windows
host. At that point `cd.yml`, the `legacy-deployer` runner, and the root
`compose.*.yml` files all go away, and every change to Aerie is a commit under
`deploy/` — which is the reason the comment at the top of `cd.yml` says to
retarget its runner only when the stack itself moves, not before.

## See also

- [`docs/ethos.md`](ethos.md) — why operator values and secrets are kept out of
  git in the first place; the constraint that shapes both paths
- [`scripts/flux/README.md`](../scripts/flux/README.md) — the bootstrap step, and
  why it isn't `flux bootstrap github`
- [`scripts/k3s/README.md`](../scripts/k3s/README.md) — node provisioning
- [`docs/secrets-architecture.md`](secrets-architecture.md) — the SSM → External
  Secrets path that `deploy/` holds pointers into
- [`docs/disaster-recovery.md`](disaster-recovery.md) — rebuild procedure
- [the cluster plan](plans/swarm/design.md) — the migration plan these phases belong to
