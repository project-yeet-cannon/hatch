# Delivery Architecture

How a commit becomes a running thing.

## Summary

**Aerie has one delivery path, and it pulls.** Nothing in GitHub Actions deploys
anything. You push to `main`; controllers inside the cluster notice and converge
on what git says.

| | |
|---|---|
| What runs there | Everything — API, kiosk `files`, the share, Postgres, observability, ingress |
| Defined by | `deploy/cluster/**` and [`charts/aerie/`](../charts/aerie/) |
| Delivery mechanism | Flux **pulls** from GitHub |
| Triggered by | Push to `main` — no workflow involved |
| Credential direction | The cluster holds a read-only git credential; nothing outside holds a credential to the cluster |
| Where the deploy logic lives | In the cluster, as `GitRepository` + `Kustomization` objects |

GitHub Actions still does two things, and neither of them is a deploy:
[`publish.yml`](../.github/workflows/publish.yml) builds container images and
pushes them to `ghcr.io`, and the `provision-*` workflows build or configure
*nodes* and plant out-of-band configuration. Both are covered below.

This used to be two paths. Until the
[Phase 7 cutover](plans/swarm/phase-7-cutover.md) a push-based `cd.yml` deployed
a Compose stack to a single Windows host, and this document spent half its length
keeping the two straight. That host is now the cluster's third node; `cd.yml`,
the `legacy-deployer` runner and every `compose.*.yml` are deleted. If you find a
reference to any of them, it is stale — say so.

If you only remember one thing: **every `kubectl`-shaped verb in this system is
performed by a controller running inside the cluster, reacting to what's in
git.**

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

## How a commit becomes a running thing

Two kinds of change reach the cluster, and they take different routes.

### A manifest change

You edit something under `deploy/` or in `charts/aerie/`, and push to `main`.
That is the entire procedure. The `GitRepository` polls once a minute, the
Kustomizations reconcile, and the change is live — see [the timeline](#the-timeline-of-a-push)
below.

### An application code change

Application code becomes an image, and the image tag has to reach a manifest.
Three actors, none of which is a deploy pipeline:

1. **Push to `main`** →
   [`publish.yml`](../.github/workflows/publish.yml) builds each image and pushes
   it to `ghcr.io`, tagged three ways: `latest`, the commit SHA, and
   `<YYYYMMDDHHmmss>-<short-sha>`. That third tag is the one that matters — it
   sorts chronologically as a number, which is what makes automated selection a
   `numerical` policy rather than a guess. The build starts in parallel with CI
   and only *pushes* once CI reports success for the same commit, so an image
   that exists is an image that passed.
2. **Flux's image-reflector-controller scans GHCR** every 5m
   ([`image-repositories.yaml`](../deploy/cluster/apps/automation/image-repositories.yaml)),
   and an `ImagePolicy`
   ([`image-policies.yaml`](../deploy/cluster/apps/automation/image-policies.yaml))
   picks the highest timestamp matching that pattern.
3. **image-automation-controller commits the selected tag** — not here. It writes
   to a **private, per-installation site repo**
   ([`gitrepository.yaml`](../deploy/cluster/site/gitrepository.yaml)), whose one
   file is an `aerie-image-tags` ConfigMap. The `apps` Kustomization substitutes
   from that ConfigMap, so the new tag lands in the `HelmRelease`'s values on the
   next reconcile.

**Why the site repo exists is worth understanding before you touch any of this.**
A `contents:write` token in the cluster's own `flux-system` namespace, over the
repository that governs the cluster, means a compromised workload's blast radius
no longer stops at the cluster. And the value being written — which build one
installation happens to be running — is an operator value, exactly the class of
fact [`docs/ethos.md`](ethos.md) keeps out of this repo. The site repo takes both
problems at once: the write credential is scoped to a repository that governs
nothing, and this repository stays true of every installation.

The practical consequence: **shipping application code is still just a push to
`main`**, with roughly a five-minute tail while the scan and the commit happen.
Nothing to dispatch, and no tag to hand-edit.

## The cluster path

The provisioning workflows (`provision-0` through `provision-5`) are
`workflow_dispatch` only — you run them by hand, from the Actions tab, and they
build or configure *nodes*.
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

The `aerie-site` `GitRepository` is the one exception in kind: it *is* committed,
but its `url` is a `${SITE_REPO_URL}` token resolved from the cluster ConfigMap
rather than a literal.

### The timeline of a push

You commit a change to `deploy/cluster/**` and push to `main`:

| Elapsed | What happens |
|---|---|
| 0s | GitHub has the commit. Nothing in the cluster knows yet. |
| ≤60s | The `GitRepository` poll finds the new revision, clones it, and publishes it as a new source artifact. |
| immediately after | kustomize-controller **watches** the source, so a revision change triggers reconciliation at once. It does *not* wait for the 10m interval. |
| +seconds | The root `Kustomization` builds `deploy/cluster` and applies it — which, since that path contains only pointers, means applying the layer Kustomizations in [`infrastructure.yaml`](../deploy/cluster/infrastructure.yaml), [`data.yaml`](../deploy/cluster/data.yaml), [`site.yaml`](../deploy/cluster/site.yaml), [`apps.yaml`](../deploy/cluster/apps.yaml) and [`observability.yaml`](../deploy/cluster/observability.yaml). |
| then | Each layer reconciles in dependency order, and because every one carries `wait: true`, it does not report Ready until the objects it applied are *healthy* — chart installed, CRDs registered, DaemonSets up. |

So: **typically under two minutes for a change to a leaf layer**, and bounded
below by the 1-minute source poll. A change that has to walk the whole dependency
chain takes as long as the slowest layer in front of it. The 10m / 30m intervals
on the Kustomizations are the *idle* re-apply cadence — drift correction — not
your deploy latency.

The intervals are chosen accordingly. The controller layers sit at 30m precisely
because re-applying an unchanged `HelmRelease` every ten minutes accomplishes
nothing; drift *inside* a Helm release is helm-controller's job on its own
schedule, and a git change is picked up by the source poll regardless.

### The layers, and the one rule that orders them

Every ordering constraint in the tree is the CRD constraint from the primer
above: you cannot apply an instance of a type nothing has defined yet. Read
`dependsOn` as "the types and the data I need exist."

```
infra-controllers   (infrastructure/controllers/, wait: true, 30m)
      │              External Secrets, cert-manager, kube-vip, Longhorn,
      │              CloudNativePG, the barman-cloud plugin, csi-driver-smb
      ▼
infra-config        (infrastructure/config/, 10m)
      │              ClusterSecretStore, ExternalSecrets, ClusterIssuers, the
      │              wildcard Certificate, StorageClasses, Traefik's config
      ├──────────────────────────────────┐
      ▼                                  ▼
data-cluster        (data/cluster/)     observability-controllers  (30m)
      │              the CNPG Cluster     │   kube-prometheus-stack, OpenSearch,
      │              and its ObjectStore  │   fluent-bit, Uptime Kuma, AutoKuma
      ▼                                  ▼
data-schema         (data/schema/)      observability-config
      │              DDL and backup       │   dashboards, alert rules, scrape
      │              schedule Jobs        │   configs, the logs/status Ingresses
      ▼
apps                (apps/, dependsOn: data-schema + site-config)
                     the aerie HelmRelease and image automation

site-source (site/) ──► site-config (the private site repo) ──┘
                        supplies aerie-image-tags
```

`wait: true` is what makes each boundary real. Without it a `Kustomization`
reports Ready as soon as its objects are *applied* — for a `HelmRelease` that
means "the CR exists," not "the chart installed and its CRDs landed." `dependsOn`
would then be waiting on a lie.

`apps` depending on **both** `data-schema` and `site-config` is the load-bearing
pair: the API needs a schema to talk to, and the HelmRelease needs an image tag
to substitute. Either missing produces a workload that starts and then fails in a
way that looks like an application bug.
### `prune: true`, and what it means for your workflow

Every `Kustomization` in the tree sets `prune: true`. **Deleting a manifest from git deletes the
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
variable, re-dispatch Provision 4, and the next reconciliation picks it up. That
is how Phase 7 raised `LONGHORN_REPLICA_COUNT` and `POSTGRES_INSTANCES` from 2 to
3 when the third node joined — two variables and a dispatch, no commit, which is
the entire argument for putting them in a ConfigMap.

## Why pull instead of push

Worth stating plainly, since push-based CD is the more familiar model and this
system deliberately abandoned it — including on the one path that used it.

**The credential direction inverts.** Push-based CD requires the pipeline to hold
production credentials; the retired `cd.yml` held an open Docker socket on the
production host. A cluster equivalent would mean a kubeconfig with write access
sitting in GitHub secrets, reachable by any workflow, any compromised action, any
fork with a clever PR. Pull-based means the cluster holds a *read-only* credential
to git and nothing outside holds a credential to the cluster. Note that Provision
3's own token requirement dropped to `contents:read` — and for a public repo, to
nothing at all — precisely because the design stopped needing to write. The one
write credential that does exist, image automation's, is scoped to a repository
that governs nothing.

**Drift correction is continuous, not per-deploy.** A push-based deploy converges
the cluster once, at deploy time, and says nothing about what happens between
deploys. Reconciliation runs whether or not anyone pushed, so a hand-edit or a
crashed operator is corrected on the next pass rather than surviving until the
next release.

**Recovery is a rebuild, not a replay.** With the cluster's desired state entirely
in git, rebuilding a control plane is Provision 1–4 plus "let Flux catch up." There
is no need to re-run a year of pipeline history in order. This is also why
[`docs/disaster-recovery.md`](disaster-recovery.md) is as short as it is: the only
thing a restore has to carry is data.

**The cost, honestly:** you lose the single green checkmark that means "it
shipped." A push completes long before the change lands, failures surface in
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
where the first two get closed, with pre-merge checks in
[`ci.yml`](../.github/workflows/ci.yml); the third is a Phase 6 alert on
`gotk_reconcile_condition`, and it is the only one no gate in front of a merge
can reach.

## What a push does *not* do

| Change | How it actually reaches the cluster |
|---|---|
| Operator values (`DOMAIN`, `INGRESS_VIP`, `LONGHORN_REPLICA_COUNT`, …) | Set the repository variable, re-dispatch **Provision 4** |
| Operator secrets (Route53 creds, HA token, …) | Re-dispatch **Provision 2**, which seeds AWS SSM; External Secrets syncs them in |
| The Flux version | Bump [`scripts/versions.json`](../scripts/versions.json), re-dispatch **Provision 3** |
| The k3s version | Bump `versions.json`, re-run **Provision 1** per node |
| Adding a node | **Provision 0** → **5** → **1** (storage before the join — a node that joins first starts filling its OS disk with Longhorn replicas) |
| The `GitRepository` / root `Kustomization` themselves | They live in the cluster, not git — re-dispatch **Provision 3** |
| An application image tag | image automation commits it to the site repo; see [above](#an-application-code-change) |

Two more consequences of the push path being uninvolved:

- **A cluster change lands even if CI is red.** Nothing gates the Flux path on
  `ci.yml` or `publish.yml` — a manifest change reconciles regardless. Branch
  protection on `main` is the only gate that exists. (An application *image* is
  the exception, and by construction: `publish.yml` withholds the push until CI
  passes, so a red build produces no new tag for automation to select.)
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
| Everything downstream NotReady, one layer NotReady | `dependsOn` doing its job — fix the layer that is actually failing and the rest follow |
| `apps` NotReady with a substitution complaint | `site-config` hasn't reconciled, so `aerie-image-tags` isn't in the cluster yet |
| `infra-controllers` NotReady with a timeout | `wait: true` gave up at 10m; usually Longhorn still rolling out on a cold node, so check whether it's failure or slowness |
| `no matches for kind "X"` | A layer-2 object whose CRD isn't installed — something is in `config/` that should be in `controllers/`, or its controller failed |
| `substitution variable not set`, or a resource with an empty field | Missing or misspelled key vs. `cluster-config.json`; check the ConfigMap exists — a missing one fails loudly on purpose |
| An object you deleted from git is gone from the cluster | `prune: true` working as intended |

A layer reporting NotReady is not automatically a fault: `wait: true` means it is
also how the tree reports "still coming up." Check the age of the condition before
treating it as an incident.

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

## What's still ahead

The two paths converged at [Phase 7](plans/swarm/phase-7-cutover.md), and what
that leaves is a shorter list than this document used to carry:

- **[Phase 8](plans/swarm/phase-8-backup-v2.md)** owns backup v2. Everything
  except Postgres currently has no copy at all — see
  [`docs/disaster-recovery.md`](disaster-recovery.md), which names the gap rather
  than papering over it.
- **[Phase 9](plans/swarm/phase-9-productization.md)** owns the productization
  pass: consolidating the architecture documents that now describe one system,
  and deciding what to do about the apiserver having no VIP in front of it
  ([`docs/secrets-architecture.md`](secrets-architecture.md#known-gap-no-vip-in-front-of-the-apiserver)).
- **The alerting flows have no teeth.** The routes and providers exist; nothing
  reaches a person yet. A `gotk_reconcile_condition` alert is what closes the
  honest gap named above, and it is only worth as much as the receiver behind it.

## See also

- [`docs/ethos.md`](ethos.md) — why operator values and secrets are kept out of
  git in the first place; the constraint that shapes this path, the site repo,
  and the ConfigMap
- [`scripts/flux/README.md`](../scripts/flux/README.md) — the bootstrap step, and
  why it isn't `flux bootstrap github`
- [`scripts/k3s/README.md`](../scripts/k3s/README.md) — node provisioning
- [`docs/secrets-architecture.md`](secrets-architecture.md) — the SSM → External
  Secrets path that `deploy/` holds pointers into
- [`docs/reverse-proxy-architecture.md`](reverse-proxy-architecture.md) — how a
  request from the house reaches one of these workloads
- [`docs/disaster-recovery.md`](disaster-recovery.md) — what is backed up, and
  what a rebuild looks like
- [the cluster plan](plans/swarm/design.md) — the migration these phases belong to
