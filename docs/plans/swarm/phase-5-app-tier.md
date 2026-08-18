[← Phase 4](phase-4-data-tier.md) · [Design & decisions](design.md) · [Phase 6 →](phase-6-observability.md)

---

# Phase 5 — App tier

**Status: Not started**

> Re-scoped, the same way Phases 3 and 4 were. The seven bullets this file used
> to hold were one sentence each; working them through against the repo turned
> up four things that stop the phase dead if they are discovered mid-flight,
> and one bullet that is simply wrong about *when*.
>
> - **The first-party images are private and nothing in the cluster can pull
>   them.** `ghcr.io/eouw0o83hf/aerie-api` answers an anonymous token request
>   with 403 — the repository is private, so its packages are too. Phases 3 and
>   4 never noticed because every image they pull is public. The old host works
>   because [cd.yml](../../../.github/workflows/cd.yml) does a `docker login`
>   first. Without a pull secret in place *before* the first Deployment, every
>   pod in this phase is `ImagePullBackOff` and every other check is blocked
>   behind it (5a.2, 5b.1).
> - **Flux cannot do image automation as installed, and must not be given what
>   it would take to.**
>   [`Bootstrap-Flux.ps1`](../../../scripts/flux/Bootstrap-Flux.ps1#L338-L351) runs a bare
>   `flux install` — its own comment says "no `--components`: the default set is
>   exactly what `flux bootstrap` would install" — and image-reflector-controller
>   and image-automation-controller are **not** in that set. That half is one
>   re-dispatch of Provision 3, and it has to happen before 5b.12 rather than
>   being discovered by it. The other half is not a gap to be filled: the
>   automation has to commit its tag bump *somewhere*, and the obvious
>   somewhere — upgrading the `flux-system` Secret to `contents:write` on this
>   repository — hands the cluster a credential over the repository that
>   governs it, in order to write down a fact true of exactly one installation.
>   **The bump goes to a private per-installation site repo instead**, and this
>   repo stays read-only to the cluster (5a.3, 5b.4, 5b.10, 5b.12).
> - **The published tags are unsortable, so no `ImagePolicy` can order them.**
>   [publish.yml](../../../.github/workflows/publish.yml) emits `latest` and the
>   raw 40-hex commit SHA. `latest` is a moving pointer and a SHA has no order,
>   so there is nothing for a policy to select "newest" from. Image automation
>   needs a monotonic tag — `<UTC timestamp>-<short sha>` — added alongside the
>   two that exist, not replacing them: the old host's compose files pull by
>   SHA and keep doing so until Phase 7 (5b.12).
> - **`cd.yml` cannot be deleted in this phase.** The phase bullet says image
>   automation "removes `cd.yml` entirely". It cannot: the old host serves
>   production until Phase 7 moves DNS, and `cd.yml` is the only thing that
>   deploys it. It also installs windows_exporter and Tailscale on the host and
>   initializes the restic repositories — none of which the cluster replaces.
>   What Phase 5 does is make the deletion *possible*: move the two host-level
>   installs into the provisioning workflow that owns node setup, so Phase 7's
>   deletion is a deletion rather than a migration (5b.13).
> - **The kiosk Wi-Fi `ExternalSecret` bullet asks for something 3b.6 refuses.**
>   `kiosk/wifi-password` is `required: false`, and the generator now rejects a
>   `kubernetes` block on an optional entry outright — so "add it here, but
>   `required: false`" is not a thing that can be committed. The value lives in
>   `SiteSettings`, obfuscated by
>   [SecretObfuscator](../../../src/Aerie.Api/Common/SecretObfuscator.cs), and moving
>   it out is an app change plus a data migration that this phase does not
>   otherwise need. It is resolved by writing the reason down and moving the
>   parameter's phase marker, not by adding a manifest (5b.14).
>
> Two decisions, taken deliberately and recorded here so they are not
> relitigated mid-phase. **The app tier is a first-party Helm chart**, as the
> original bullet says, even though every other thing under `deploy/` is plain
> kustomize — because a `pre-upgrade` hook is the only mechanism that runs a
> migration exactly once per deploy and gates the pods behind it, and
> reimplementing that on top of kustomize means a Job whose immutable name has
> to change with the image tag. The idiom split is smaller than it looks: the
> `HelmRelease` manifest is itself an ordinary object in a Kustomization, so
> `${...}` substitution reaches its inline `values:` block exactly the way it
> reaches everything else, and `valuesFrom`/`targetPath` is never needed.
> **The SMB share comes with this phase** — `csi-driver-smb`, the two mount
> modes, `share.${DOMAIN}` and the media library — rather than being deferred,
> so Phase 7's cutover loses nothing that works today.

---

## Phase 5a — Manual prerequisites

*Seven one-time steps, none of them code. Do these first, in order, and the
whole of 5b runs from commits and workflow dispatches.*

**[x] 1. Confirm Phase 4 actually landed.** Dispatch *Verify: Data tier*
([`verify-data-tier.yml`](../../../.github/workflows/verify-data-tier.yml)) and get a
green run. Everything this phase builds sits on top of it. Confirm
specifically, in that output:

- the `Cluster` is ready with `POSTGRES_INSTANCES` instances on distinct nodes
- the `quartz` `Database` is ready and its eleven tables exist
- `cnpg-wal-s3` is `SecretSynced`

Then read one thing the gate does not assert, because 5b.5 is built on its
exact shape:

```sh
kubectl -n aerie get secret aerie-pg-app -o jsonpath='{.data}' | jq 'keys'
```

Expect `username`, `password`, `dbname`, `host`, `port`, `uri`, `pgpass`. 5b.5
composes its connection strings from `username` and `password`; if those key
names differ on the operator version actually running, that step's env block
changes and nothing else does.

**[x] 2. Mint the GHCR pull token.** A **classic** personal access token with
the single scope `read:packages` — fine-grained tokens do not grant GHCR pull,
and a fine-grained token here fails as a 403 that reads exactly like a
non-existent package. Nothing in this repo mints it, for the same reason
nothing mints an IAM user: a script that did would have to print it
([ethos](../../ethos.md)).

Then add, under Settings > Secrets and variables > Actions:

- `GHCR_PULL_USERNAME` as a **variable** — your GitHub username. A username
  identifies, it does not authenticate; same split
  [`parameters.json`](../../../scripts/secrets/parameters.json) records as `githubKind`
  for every access key id.
- `GHCR_PULL_TOKEN` as a **secret**.

Getting the tabs backwards resolves to an empty string rather than erroring,
and surfaces four steps later as an unauthenticated pull.

**[x] 3. Create the private site repo, and a token scoped to it.** Image
automation has to commit the tag it selected somewhere. It is not going to be
here.

Two reasons, and the second is the one that decides it. A `contents:write`
token in the `flux-system` Secret is a credential *held by the cluster over the
repository that governs the cluster* — which means a compromised workload's
blast radius no longer stops at the cluster. And the value it would write —
`tag: 20260101000000-abcdef1`, in a committed HelmRelease — is an operator
value in the shared artifact: which build one installation happens to be
running is not true of every installation, and [ethos](../../ethos.md) keeps
exactly that class of fact out of this repo. 5b.12 as first written introduced
that violation; this is where it is removed rather than inherited.

So: **Aerie is the template, and a private repo per installation holds what the
cluster writes.** Create it now — `aerie-site-<something>`, private, one
branch, **no workflows** — containing one file:

```yaml
# image-tags.yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: aerie-image-tags
  namespace: flux-system
data:
  AERIE_API_IMAGE_TAG: latest
  KIOSK_FILES_IMAGE_TAG: latest
```

`latest` as the seed is deliberate, not a placeholder: it is a tag that
certainly exists, so the cluster reconciles to something real on day one and
5b.12's first automation run overwrites it with a timestamped tag. A value that
did not resolve would make the first HelmRelease failure ambiguous between
"automation has not run yet" and "the substitution path is wrong end to end".

The namespace is `flux-system` and is not a choice: `postBuild.substituteFrom`
reads a ConfigMap from the namespace of the `Kustomization` that names it, and
5b.10's lives there like every other one.

Then mint a **fine-grained** PAT with `contents:write` on *that repository and
nothing else* — fine-grained works here, unlike 5a.2's GHCR token, because this
is the git API rather than the registry — and add:

- `SITE_REPO_URL` as a **variable** — the HTTPS clone URL, e.g.
  `https://github.com/<owner>/aerie-site-<something>`, no credentials in it.
  5b.4 declares it as a `cluster-config.json` key.
- `SITE_GIT_USERNAME` as a **variable**, your GitHub username, for the same
  reason `GHCR_PULL_USERNAME` is one.
- `SITE_GIT_TOKEN` as a **secret**. 5b.1 seeds it into the store and ESO
  materializes it as the `aerie-site-git` Secret in `flux-system`.

`FLUX_GITHUB_TOKEN` is untouched by all of this and stays `contents:read`.

*What this deliberately does not do yet:* move the HelmRelease, the chart
values, or anything else out of this repo. The site repo holds one ConfigMap in
this phase — the smallest thing that gets the write out of Aerie — and the
larger split is recorded for Phase 9 at the end of this file. If standing up a
second repo is unwanted, the fallback is `publish.yml` committing the tag
itself and no image automation in the cluster at all; say so before 5b.4 is
written, because that step's `SITE_REPO_URL` key is the first commitment to
this shape.

**[x] 4. Settle the SMB share credential.** Both CIFS mounts on the old host
use `guest`
([compose.share.yml](../../../compose.share.yml#L96-L120)), which works because the
share allows anonymous access. `csi-driver-smb` still requires a
`nodeStageSecretRef` — a Secret with `username`/`password` keys — even when the
answer is guest with an empty password.

Decide which:

- **Guest**, matching today: `username: guest`, `password: ""`. Zero change to
  the share, and the credential is not a credential.
- **A dedicated share account**, which is the better answer for a share that
  will hold family files behind `share.${DOMAIN}`'s login. Create it on the
  Windows box that hosts the share and grant it that share only.

Either way, add `SMB_USERNAME` as a **variable** and `SMB_PASSWORD` as a
**secret**. Set `SMB_PASSWORD` to an empty string for the guest path — 5b.3's
parameter is `required: true` either way, and an entry that is sometimes seeded
and sometimes not is the exact failure `required` exists to prevent.

**[x] 5. Set the new repository variables.** 5b.4 declares these in
[`cluster-config.json`](../../../scripts/k3s/cluster-config.json) and Provision 4 plants
them; set them now so that dispatch is one step and not a round trip:

| Variable | Value | Notes |
|---|---|---|
| `IMAGE_REGISTRY` | `ghcr.io/<owner>` | Registry **and** namespace, no image name, no trailing slash. Keeps the owner out of git. |
| `SHARE_HOST` | FQDN or IP of the SMB server | Never the bare hostname. Same constraint the compose file already documents: the mount is performed by a resolver with no DNS suffix search list, no NetBIOS and no mDNS. |
| `SHARE_NAME` | The share name alone | `SHARE_PATH` is split in two because `csi-driver-smb`'s `source` wants `//host/share` while the PV also needs the host on its own for the `volumeHandle`. |
| `MEDIA_LIBRARY_SUBPATH` | e.g. `Music` | Optional. Unset turns media serving off, exactly as today. |
| `TZ` | e.g. `America/New_York` | Already a `cd.yml` variable. |
| `HA_HOST` / `HA_PORT` | Home Assistant address | Optional. Only read on a **fresh** install, by the one-time `SiteSettings` import; after 4b.9's restore the values are already in the database. |

**[x] 6. Size the workloads from real data.** 5b.11 puts requests and limits on
every container, and [goal 2 does not work without
them](design.md#how-goal-2-actually-works) — but the numbers are a judgment
call from measurement, not something a manifest can invent. On the old host:

```sh
docker stats --no-stream --format "table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}"
```

Take a reading at idle and one during a dashboard load. Record, in 5b.11's
commit message, the observed steady-state memory for `api` and `files` and what
you set the request and limit to. Rule of thumb, and the reason: **memory
request ≈ observed steady state, memory limit ≈ 2× that** (a .NET process
grows its heap to fill what it is given, and a limit is an OOMKill rather than
throttling); **CPU request from observed use, and no CPU limit at all** (a CPU
limit throttles at the quota period and shows up as latency nobody can explain,
while the request is what the scheduler actually places on).

*Measured 2026-08-18*, three `--no-stream` samples on the compose host: idle,
during a backfill, and during a dashboard load. `docker stats` reports 100% as
one full core, and cgroup memory accounting includes page cache — so the small
numbers below are already inclusive and can be used as-is.

| Container | idle | backfill | dashboard load | peak CPU |
|---|---|---|---|---|
| `api` | 221.8 MiB | 267.4 MiB | 308.1 MiB | 0.20% |
| `db` | 51.9 MiB | 66.1 MiB | 69.9 MiB | 4.72% |
| `files` | 7.4 MiB | 7.4 MiB | 7.4 MiB | 0.00% |
| `opensearch` | 1.480 GiB | 1.481 GiB | 1.481 GiB | 1.92% |
| `opensearch-dashboards` | 310.1 MiB | 310.2 MiB | 310.2 MiB | 0.13% |
| `grafana` | 225.9 MiB | 226.0 MiB | 226.4 MiB | 5.02% |
| `prometheus` | 134.2 MiB | 141.6 MiB | 140.9 MiB | 0.95% |
| `uptime-kuma` | 151.3 MiB | 151.1 MiB | 151.2 MiB | 0.48% |
| `autokuma` | 32.5 MiB | 32.3 MiB | 31.8 MiB | 0.05% |
| `caddy` | 21.3 MiB | 21.1 MiB | 21.8 MiB | 0.78% |
| `backup` | 8.5 MiB | 8.5 MiB | 8.5 MiB | 0.00% |
| `fluent-bit` | 7.5 MiB | 6.6 MiB | 6.4 MiB | 0.03% |
| `node-exporter` | 3.0 MiB | 3.0 MiB | 3.0 MiB | 0.00% |

Three things the readings say that the raw numbers do not:

- **`api` never reached a steady state.** 222 → 267 → 308 MiB, monotonic,
  never dropping — a .NET heap growing into an unconstrained 11.6 GiB host,
  with nothing ever pressuring it into a Gen2 collection. 308 MiB is a floor.
  Applying "request ≈ observed" literally here would under-request; the
  estimate carried into 5b.11 is ~350 MiB, and the *limit* is what finally
  makes the number true, because .NET reads the cgroup limit and sets its GC
  hard limit to 75% of it.
- **CPU requests cannot come from this measurement at all.** Steady-state
  sampling never sees `api`'s real peak, which is startup — JIT, EF model
  build, Quartz wiring, once per replica. A request derived from 0.20% (2m)
  would let three replicas pack onto one node and starve each other through a
  rolling upgrade. 5b.11's CPU requests are sized for startup and for the
  cpu.shares weighting they buy under contention, not for idle.
- **Nothing observed justifies a CPU limit**, which is the rule above
  arriving as a measurement rather than an assertion: the entire stack under
  "load" used well under a third of one core.

Two workloads are **not** in this reading and their numbers in 5b.11 are
estimates, flagged as such there: `share` (dufs — [compose.share.yml](../../../compose.share.yml)
was not up on that host) and the migrate hook Job, which does not exist yet.

*The observability rows are recorded for [Phase 6](phase-6-observability.md)
and the `db` row for [Phase 4](phase-4-data-tier.md), because after Phase 7
this host is gone and these cannot be re-measured.* Two of them must not be
transplanted as-is: `prometheus` at 140 MiB is scraping a compose project,
where the k8s stack scrapes kube-state-metrics, kubelet cAdvisor, Longhorn and
Flux at several times the series count; and `opensearch` at 1.48 GiB against a
`-Xmx512m` heap is heap plus mmap'd Lucene segments plus JVM overhead, so its
limit is set from the resident figure and not from the heap.

**[ ] 7. Change nothing about the old host, and know how you will test.** DNS
still points at the old host and Phase 7 is what moves it, so this phase's
hostnames are reachable only by asking the VIP directly:

```sh
curl -sv --resolve home.${DOMAIN}:443:${INGRESS_VIP} https://home.${DOMAIN}/health
```

That is how every *Exit* line below is checked, and how 5b.14's gate works.
Do not add hosts-file entries on machines other people use — a stale one
outlives the phase and is diagnosed as a DNS failure weeks later.

---

## Phase 5b — Scriptable, in this order

Steps 1–4 are plumbing with no workload behind them: the pull secret, the app
changes, the SMB driver, the config keys and the site repo source. 5–10 build the chart, one concern at
a time, and only 10 makes it reconcile. 11–13 are the properties the design
demands of any workload, plus the automation. 14 is the gate.

- [x] **1. The registry pull secret, through ESO** —
      [`parameters.json`](../../../scripts/secrets/parameters.json),
      [`New-ExternalSecrets.ps1`](../../../scripts/secrets/New-ExternalSecrets.ps1),
      [`provision-2-seed-secrets.yml`](../../../.github/workflows/provision-2-seed-secrets.yml).

      This needs two extensions to the generator, and both are real gaps rather
      than conveniences:

      1. **A `dockerconfigjson` target.** The generator emits `Opaque` Secrets
         with one key per parameter; a kubelet pull secret must be
         `kubernetes.io/dockerconfigjson` with the credentials nested inside one
         JSON key. ESO does this with a `target.template`. Add an optional
         `dockerconfigjson` field on the `kubernetes` block naming the registry
         host, which renders:

         ```yaml
         target:
           name: ghcr-pull
           creationPolicy: Owner
           template:
             type: kubernetes.io/dockerconfigjson
             engineVersion: v2
             data:
               .dockerconfigjson: |
                 {"auths":{"ghcr.io":{"username":"{{ .username }}","password":"{{ .token }}","auth":"{{ printf "%s:%s" .username .token | b64enc }}"}}}
         ```

         `engineVersion: v2` is not optional — `b64enc` is a v2 function, and
         v1 renders the template with the call left as literal text, producing
         a Secret that is structurally valid and rejected by every registry.
      2. **A parameter that lands in more than one namespace.** The same
         credential is needed twice: in `aerie` for the kubelet to pull with,
         and in `flux-system` for image-reflector-controller to *scan* with
         (5b.12). Let `kubernetes` be either a block or an array of blocks, and
         group by (namespace, secretName) as it already does. The alternative —
         a second pair of parameters with the same value under different paths —
         is a credential that can half-rotate, which is the failure the grouping
         rule exists to prevent.

      Then the same four-part ordering 4b.3 established, because the generator
      refuses a `kubernetes` block on a `required: false` entry and an
      `ExternalSecret` over an unseeded parameter poisons its Kustomization:

      1. Add `registry/pull-username` (`env: GHCR_PULL_USERNAME`,
         `githubKind: variable`) and `registry/pull-token`
         (`env: GHCR_PULL_TOKEN`, `githubKind: secret`), both `required: true`,
         `phase: 5`, both with `kubernetesDeferred` for the moment. In the same
         commit add `site/git-username` (`env: SITE_GIT_USERNAME`,
         `githubKind: variable`) and `site/git-token`
         (`env: SITE_GIT_TOKEN`, `githubKind: secret`) — 5a.3's credential for
         the site repo, which 5b.4 needs and which rides this dispatch rather
         than earning a second one.
      2. Add the four lines to `provision-2-seed-secrets.yml`.
      3. **Dispatch Provision 2 and confirm all four parameters seed.** Only
         then:
      4. Replace `kubernetesDeferred` with the real `kubernetes` blocks. For
         the registry pair: secretName `ghcr-pull`, keys `username` and `token`,
         namespaces `aerie` and `flux-system`, `dockerconfigjson: ghcr.io`. For
         the site pair: secretName `aerie-site-git`, `flux-system` alone, and
         the keys named **`username` and `password`** — the shape Flux's
         `GitRepository` basic auth reads, and the one place in this phase where
         a key name is dictated by its consumer rather than chosen. Then run
         `pwsh ./scripts/secrets/New-ExternalSecrets.ps1`.

      *Exit:* `New-ExternalSecrets.ps1 -Check` exits 0, and after a
      reconciliation `kubectl -n aerie get externalsecret ghcr-pull` reports
      `SecretSynced` with a Secret of type `kubernetes.io/dockerconfigjson`, as
      does `kubectl -n flux-system get externalsecret aerie-site-git`.
      Prove the registry one end to end before trusting it:
      `kubectl -n aerie run pull-probe --rm -it --image=${IMAGE_REGISTRY}/aerie-api:latest --overrides='{"spec":{"imagePullSecrets":[{"name":"ghcr-pull"}]}}' --command -- true`.

- [x] **2. The app changes** — [Program.cs](../../../src/Aerie.Api/Program.cs) and its
      neighbours. Finding 1, and the only code this phase writes. Six things:

      1. **Migrations become a mode of the same image.** Move the
         `MigrateAsync` loop and `SeedAsync` out of the boot path and behind
         an entry point the hook Job invokes — an `AERIE_MIGRATE=1` env check
         that runs them and exits before `app.Run()`. One image, two modes,
         so the migration provably runs the same code as the pods it
         precedes; a second image is a second thing to build, tag, scan and
         get wrong. **`haConnection.ApplyAsync` does *not* move with them.**
         It looks like the same kind of boot-time setup, but it isn't: it
         calls `ClientFactory.Initialize`, which is HADotNet's own
         process-static state (confirmed from the IL — a private static
         `HttpClient` field, null until `Initialize` runs), not a database
         row. The migrate Job is a separate process that runs once and exits;
         if `ApplyAsync` only ran there, none of the three `api` replicas
         would ever initialize their own `ClientFactory`, and every
         HA-dependent call would fail on all three until an admin re-saved
         HA connection settings through `SettingsController` — which even
         then only fixes whichever one replica handled that request. Instead,
         `ApplyAsync` is lazy: every `AddTransient` registration for an HA
         client (`EntityClient`, `HistoryClient`, etc.) routes through a
         `HomeAssistantClientFactoryGate` first, which checks
         `ClientFactory.IsInitialized` and — the first time only, gated by a
         semaphore so concurrent callers coalesce onto one attempt — calls
         `ApplyAsync` before handing back the client. Each replica pays for
         this once, the first time anything on it actually needs HA.
      2. **`JobsInit.WireUpJobs()` stays per-replica and must prove it.**
         Quartz clustering handles execution, not registration. Confirm every
         `AddJob`/`ScheduleJob` in [JobsInit](../../../src/Aerie.Api/Jobs/) passes
         `replace: true`; without it the second replica throws
         `ObjectAlreadyExistsException` at boot and crash-loops.
      3. **DataProtection.** Nothing configures key persistence anywhere, which
         is invisible at one replica and means three independent ephemeral key
         rings at three. Grep for antiforgery, cookie authentication, session
         and `TempData`; the API looks stateless enough that the answer is "none
         of it is used", in which case **write that down in Program.cs** and add
         nothing. If any of it *is* used, persist the key ring to Postgres
         before the replica count goes above one.
      4. **Health checks split.** `MapHealthChecks("/health")` today reports
         healthy with the database unreachable, which makes it useless as a
         readiness probe — a pod that cannot serve stays in the Service. Add a
         `DbContext` check and expose `/health/live` (process is up; the
         liveness probe) and `/health/ready` (dependencies answer; the readiness
         probe). A liveness probe that includes the database is a bug, not
         thoroughness: a database blip restarts every replica at once.
      5. **`KioskFiles` base address becomes configuration.** It is hardcoded
         to `http://files/`
         ([Program.cs:133-136](../../../src/Aerie.Api/Program.cs#L133-L136)), which happens
         to keep working — the k8s Service is also named `files` in the pod's own
         namespace — but a URL that is correct by coincidence is one rename from
         a 404 nobody can explain. Read it from `KioskFiles:BaseAddress` with
         that value as the default.
      6. **Comments that name Caddy and the `edge` network.** The
         `ForwardedHeaders` block
         ([Program.cs:210-226](../../../src/Aerie.Api/Program.cs#L210-L226)) explains
         itself entirely in terms of a topology this phase replaces. The code is
         still right — Traefik is the only peer that connects, for the same
         reason — but the reasoning has to name the real proxy.

      **Do not touch
      [appsettings.Docker.json](../../../src/Aerie.Api/appsettings.Docker.json).** Finding
      2 is emphatic that `user`/`password` stops existing, and it does — but the
      old host is still reading that file until Phase 7, and the cluster
      overrides it with env vars (5b.5) rather than needing it changed. Deleting
      those two lines here takes production down.
      *Exit:* `dotnet test` passes; the image built from this commit runs
      migrations and exits when given the migrate mode, and serves when not.

- [ ] **3. `csi-driver-smb`, and the share credential** —
      `deploy/cluster/infrastructure/controllers/csi-driver-smb.yaml`, one line in
      that directory's
      [`kustomization.yaml`](../../../deploy/cluster/infrastructure/controllers/kustomization.yaml),
      and two more parameters.

      Layer 1, like every other controller: it registers a CSI driver the PVs in
      5b.7 are instances of. Chart `csi-driver-smb` from the
      `kubernetes-csi/csi-driver-smb` chart repository, which is a **new
      `HelmRepository`** — unlike 4b.4, nothing here already declares it. Pin the
      chart version and the driver image explicitly. Namespace `kube-system`,
      matching where the chart expects to run.

      The credential rides the machinery from 5b.1: `smb/username` and
      `smb/password` in `parameters.json`, `required: true`, one `kubernetes`
      block each targeting namespace `aerie`, secretName `smb-share`, keys
      `username` and `password`. Seed with Provision 2 **before** flipping them
      to a `kubernetes` block, same ordering, same reason.

      *Exit:* `kubectl -n kube-system get csidriver smb.csi.k8s.io` exists, the
      `csi-smb-node` DaemonSet is Ready on every node, and
      `kubectl -n aerie get externalsecret smb-share` is `SecretSynced`.
      — *The node plugin runs privileged and mounts into the host mount
      namespace; that is what a CSI node plugin is, and it is worth knowing
      before it appears in a security review as a surprise.*

- [ ] **4. Eight new `cluster-config.json` keys, the site repo source, and a
      Provision 4 re-dispatch** —
      [`cluster-config.json`](../../../scripts/k3s/cluster-config.json),
      [`provision-4-cluster-config.yml`](../../../.github/workflows/provision-4-cluster-config.yml),
      `deploy/cluster/site.yaml`, `deploy/cluster/site/`, and one line in
      [`kustomization.yaml`](../../../deploy/cluster/kustomization.yaml).

      | Key | Required | Pattern notes | `consumedBy` |
      |---|---|---|---|
      | `SITE_REPO_URL` | yes | An `https://` clone URL, no trailing `.git`, no credentials embedded in it | 5b.4 GitRepository |
      | `IMAGE_REGISTRY` | yes | Host plus namespace, no image, no trailing slash, no tag | 5b.10 HelmRelease, 5b.12 ImageRepository |
      | `SHARE_HOST` | yes | A hostname or IPv4 — **not** a UNC path and no leading slashes | 5b.7 PersistentVolume |
      | `SHARE_NAME` | yes | A share name: no slashes | 5b.7 PersistentVolume |
      | `MEDIA_LIBRARY_SUBPATH` | no | A relative path, no leading slash, no `..` | 5b.5 api env |
      | `TZ` | yes | An IANA zone id (`Area/Location`), not an abbreviation | 5b.5 api env |
      | `HA_HOST` | no | Hostname or IPv4 | 5b.5 api env |
      | `HA_PORT` | no | 1–65535 | 5b.5 api env |

      `MEDIA_LIBRARY_SUBPATH` is optional and that is load-bearing rather than
      lenient — the reasoning in
      [compose.share.yml](../../../compose.share.yml#L28-L44) survives the move intact:
      the media path is served **unauthenticated**, so an unset value must leave
      `MediaLibrary__RootPath` empty rather than collapsing to the share root.
      Reproduce that guard in the chart template, not just in the key's
      `description`.

      **The site repo source, in two files and one line.** 5a.3's repo has to be
      reachable before 5b.10 reconciles, because the tags that release resolves
      come from it. Commit:

      - `deploy/cluster/site/gitrepository.yaml` — a `GitRepository` named
        `aerie-site` in `flux-system`, `url: ${SITE_REPO_URL}`,
        `ref.branch` the site repo's branch, and
        `secretRef: {name: aerie-site-git}`, which is 5b.1's ESO output.
      - `deploy/cluster/site.yaml` — two Flux `Kustomization`s. `site-source`
        reads `./deploy/cluster/site` from the `flux-system` GitRepository with
        `postBuild.substituteFrom` the `aerie-cluster-config` ConfigMap, which
        is what resolves `${SITE_REPO_URL}`. `site-config` reads `./` from the
        `aerie-site` GitRepository, `dependsOn: site-source`, `prune: true`, and
        **no substitution at all** — nothing in the site repo is a template, and
        a `${...}` appearing there later would mean the split has been drawn in
        the wrong place.
      - one `- site.yaml` line in `deploy/cluster/kustomization.yaml`.

      The two-Kustomization split is not ceremony: the object that defines the
      site source cannot be reconciled *from* the site source, and saying so
      with `dependsOn` is what keeps that ordering from being a retry loop that
      merely happens to converge.

      **Dispatch Provision 4 before the 5b.10 commit reaches the cluster.** Flux
      expands an undefined token to the empty string, so a HelmRelease that
      lands first is not a failed reconciliation — it is a Deployment pulling
      `/aerie-api:...` and an Ingress with host `home.`. ci.yml catches the
      *spelling* of a token against this file; nothing catches a correctly
      spelled key the cluster has not been given yet.
      *Exit:* `kubectl -n flux-system get configmap aerie-cluster-config -o yaml`
      shows all eight keys, the optional ones present only if set;
      `flux -n flux-system get source git aerie-site` is Ready; and
      `kubectl -n flux-system get configmap aerie-image-tags` shows the two tag
      keys 5a.3 seeded, which is the site repo proving it reconciles before
      anything depends on it.

- [ ] **5. The chart skeleton and the `api` workload** — `charts/aerie/`.

      `Chart.yaml` (`apiVersion: v2`, `type: application`, a `version` that is
      bumped when templates change, `appVersion` left alone), `values.yaml`
      holding the defaults and the shape, `templates/_helpers.tpl` for labels,
      and the first two templates: `api-deployment.yaml`, `api-service.yaml`.

      The Deployment, with the parts that are not obvious:

      - `replicas: 3`, `imagePullSecrets: [{name: ghcr-pull}]`.
      - **Connection strings assembled from the CNPG Secret in the pod spec**,
        using Kubernetes' own `$(VAR)` expansion — which resolves env vars
        against *earlier* entries in the same container's `env` list, so order
        matters and a forward reference silently renders as the literal text:

        ```yaml
        env:
          - name: PGUSER
            valueFrom: { secretKeyRef: { name: aerie-pg-app, key: username } }
          - name: PGPASSWORD
            valueFrom: { secretKeyRef: { name: aerie-pg-app, key: password } }
          - name: ConnectionStrings__Aerie
            value: "Host=aerie-pg-rw;Port=5432;Database=aerie;Username=$(PGUSER);Password=$(PGPASSWORD)"
          - name: ConnectionStrings__Quartz
            value: "Host=aerie-pg-rw;Port=5432;Database=quartz;Username=$(PGUSER);Password=$(PGPASSWORD)"
        ```

        `$(VAR)`, not `${VAR}`: the latter is Flux's `postBuild` syntax and
        would be consumed before the pod ever sees it. Two databases, one
        credential — which is why 5b.2 does not touch the connection strings in
        `appsettings.Docker.json` and does not need to: `ConnectionStrings__X`
        in the environment outranks the file.
        **Verify the password survives the string.** Npgsql parses
        keyword/value pairs, so a generated password containing `;` or `=`
        would truncate silently into a wrong-credential error. Read the actual
        value once (`kubectl -n aerie get secret aerie-pg-app -o
        jsonpath='{.data.password}' | base64 -d`); if it is anything but
        alphanumeric, single-quote the value in the template and say why.
      - The rest of the environment, each replacing a `cd.yml` line:
        `DOTNET_ENVIRONMENT=Docker`, `DOMAIN=${DOMAIN}`,
        `Apps__PublicBaseUrl=https://home.${DOMAIN}`, `TZ=${TZ}`,
        `VM_LOG_SHIPPER_TOKEN` from the `vm-log-shipper` Secret, `HA_TOKEN` from
        the `home-assistant` Secret — the two Phase 3 synced as proof the store
        worked, finally consumed — and `HA_HOST`/`HA_PORT` and
        `MediaLibrary__RootPath` rendered only when their values are non-empty.
      - Probes: `startupProbe` and `readinessProbe` on `/health/ready`,
        `livenessProbe` on `/health/live`. The startup probe is what makes a
        slow first boot (EF taking the migration lock behind a hook Job that is
        still finishing) not look like a crash loop.
      - `securityContext`: non-root, `readOnlyRootFilesystem` if the app
        tolerates it, `allowPrivilegeEscalation: false`.

      The Service is `ClusterIP` on 8080, named `api`.
      *Exit:* `helm template charts/aerie` renders; `helm lint` is clean.
      Nothing is deployed yet — 5b.10 is what reconciles.

- [ ] **6. The migration hook Job** — `charts/aerie/templates/migrate-job.yaml`.
      Finding 1's actual answer, and the reason this tier is a chart at all.

      ```yaml
      metadata:
        annotations:
          "helm.sh/hook": pre-install,pre-upgrade
          "helm.sh/hook-weight": "-5"
          "helm.sh/hook-delete-policy": before-hook-creation
      ```

      **`before-hook-creation` alone, not `hook-succeeded`.** The tempting pair
      deletes the Job the moment it works, taking its logs with it — and the
      question you actually ask a migration Job is "what did the one that ran
      last Tuesday do". Deleting only at the *start* of the next hook keeps
      exactly one Job around at all times, which is the useful number.

      Same image and tag as the api Deployment — one Helm value feeding both, so
      an image bump can never migrate with one version and serve with another.
      Same connection-string env block as 5b.5, plus the migrate-mode switch
      from 5b.2. `backoffLimit: 2` and `restartPolicy: Never`.

      Set `spec.install.timeout` and `spec.upgrade.timeout` on the HelmRelease
      (5b.10) longer than the slowest plausible migration; Helm waits for the
      hook to complete before touching the Deployment, and a timeout here rolls
      the whole release back.
      — *Two things to know about hooks under Flux. helm-controller runs them
      (it is the Helm SDK), but **hook Jobs are invisible to Flux's health
      reporting** — a HelmRelease is Ready when the release succeeded, and the
      hook's own success is folded into that rather than reported separately.
      So 5b.14 asserts on the Job object directly. And a hook Job is **not**
      pruned by `prune: true`, because Helm owns it and Flux does not; deleting
      the HelmRelease leaves it behind.*

- [ ] **7. The SMB volumes** — `charts/aerie/templates/share-volumes.yaml`.

      Two `PersistentVolume`/`PersistentVolumeClaim` pairs over one share,
      because a volume carries exactly one mount mode — the same constraint that
      made the old host declare two Docker volumes, arriving for the same
      reason in a different system:

      - `aerie-share-rw` — `ReadWriteMany`, `dir_mode=0775,file_mode=0664,uid=0,gid=0,vers=3.0`,
        for the dufs GUI, whose whole purpose is uploading.
      - `aerie-share-ro` — same source, mount options including `ro`, for the
        API's media serving. Nothing in Aerie writes to the share, so the API's
        view stays read-only regardless of the GUI's.

      Both: `storageClassName: ""` (static binding, no provisioner),
      `persistentVolumeReclaimPolicy: Retain`, `nodeStageSecretRef` naming the
      `smb-share` Secret from 5b.3, and
      `volumeAttributes.source: "//${SHARE_HOST}/${SHARE_NAME}"`.

      **`volumeHandle` must differ between the two.** It is the driver's
      identity for the volume, not a name — two PVs sharing one handle are one
      volume as far as the CSI layer is concerned, and the second mount silently
      gets the first one's options. Use `aerie-share-rw` / `aerie-share-ro`.
      `capacity.storage` is required by the API and meaningless for SMB; set
      something honest-looking and do not reason from it.

      Bind each PV to its PVC explicitly with `claimRef`, or a PVC from anywhere
      else in the cluster can win the race for it.
      *Exit:* both PVCs report `Bound`.

- [ ] **8. `files` and `share`** — `charts/aerie/templates/files-*.yaml`,
      `share-*.yaml`.

      `files` is the nginx image holding the kiosk APK, its signature checksum
      and `version.json`. **2 replicas** — the design lists it as HA-required,
      because a tablet that cannot reach it cannot self-update. Service named
      `files` on port 80, which is what keeps 5b.2's default base address true.

      `share` is dufs. **1 replica, `strategy: Recreate`** — the design's HA list
      does not include it, and two replicas writing one CIFS mount buys nothing
      but a second thing to reason about. Mount `aerie-share-rw` at `/share`,
      keep the command flags exactly as
      [compose.share.yml](../../../compose.share.yml#L60-L80) has them, including the
      individually-listed allow flags rather than `-A` (which would additionally
      follow symlinks out of the share).

      The dufs credentials come across as they are — `admin:password`, in the
      values file, with the comment that already explains why it is acceptable
      and what would change that. Moving them into ESO is a good idea and it is
      not this phase's; note it beside the value so the next person can see it
      was a decision.

      The API gets `aerie-share-ro` mounted at `/share-ro` and
      `MediaLibrary__RootPath` set to `/share-ro/${MEDIA_LIBRARY_SUBPATH}` —
      **only when the subpath is non-empty**, per 5b.4.
      *Exit:* nothing yet; 5b.10 reconciles.

- [ ] **9. Ingress and the kiosk rewrite** — `charts/aerie/templates/ingress.yaml`,
      `middleware-kiosk.yaml`.

      Four `Ingress` resources, `ingressClassName: traefik`, **no `tls:` block
      on any of them**:

      | Host | Backend | Notes |
      |---|---|---|
      | `home.${DOMAIN}` | `api:8080` | |
      | `kiosk.${DOMAIN}` | `api:8080` | plus the middleware below |
      | `files.${DOMAIN}` | `files:80` | |
      | `share.${DOMAIN}` | `share:5000` | dufs's default listen port |

      **Why no `tls:` block is correct, since it looks like an omission:** TLS is
      terminated at the *entrypoint*, not the router — 3b.9's
      `ports.websecure.http.tls.enabled: true` is what does it — and the
      certificate comes from 3b.10d's `TLSStore`, which serves the wildcard to
      any router that names no Secret of its own. That is the entire argument
      for having bought a wildcard.

      The kiosk rewrite, replacing `caddy_1.@root.path` / `caddy_1.rewrite`:

      ```yaml
      apiVersion: traefik.io/v1alpha1
      kind: Middleware
      metadata: { name: kiosk-root-rewrite, namespace: aerie }
      spec:
        replacePathRegex:
          regex: "^/$"
          replacement: "/apps/dashboard/"
      ```

      referenced from the kiosk Ingress by annotation:
      `traefik.ingress.kubernetes.io/router.middlewares: aerie-kiosk-root-rewrite@kubernetescrd`.
      The `<namespace>-<name>@kubernetescrd` form is required and unforgiving —
      a bare name is silently not found and the route serves unrewritten, which
      presents as the kiosk showing the API landing page.
      `replacePathRegex`, not `redirectRegex`: Caddy rewrote internally, and a
      redirect would change the tablet's address bar and break the kiosk's
      pinned URL.

      **Also add the HTTP→HTTPS redirect**, in
      [`traefik-helmchartconfig.yaml`](../../../deploy/cluster/infrastructure/config/traefik-helmchartconfig.yaml)
      rather than here — Caddy did it implicitly and Traefik does not:

      ```yaml
      ports:
        web:
          redirections:
            entryPoint:
              to: websecure
              scheme: https
              permanent: true
      ```

      *Exit:* checked in 5b.10, since none of this routes to anything yet.

- [ ] **10. The `HelmRelease`, and the `apps` layer** —
      `deploy/cluster/apps/helmrelease.yaml`,
      `deploy/cluster/apps/kustomization.yaml`, `deploy/cluster/apps.yaml`, and one
      line in [`kustomization.yaml`](../../../deploy/cluster/kustomization.yaml).
      The commit that makes everything above real.

      The Flux `Kustomization`, in the shape
      [`data.yaml`](../../../deploy/cluster/data.yaml) established:
      `dependsOn: [data-schema, site-config]`, `path: ./deploy/cluster/apps`,
      `prune: true`, `wait: true`, and `postBuild.substituteFrom` **two**
      ConfigMaps — `aerie-cluster-config` for the operator values, and
      `aerie-image-tags` (5b.4's site repo source, 5b.12's automation target)
      for the two image tags. `dependsOn: data-schema` and not `data-cluster`:
      the migration hook needs the `quartz` database and its tables, not merely
      a running primary. `dependsOn: site-config` for a blunter reason: Flux
      expands an undefined token to the empty string, so an apps layer that
      reconciles before the tag ConfigMap exists deploys `aerie-api:` and reads
      as a registry fault rather than an ordering one.

      The `HelmRelease`:

      ```yaml
      spec:
        interval: 10m
        chart:
          spec:
            chart: ./charts/aerie
            reconcileStrategy: Revision
            sourceRef:
              kind: GitRepository
              name: flux-system
              namespace: flux-system
        targetNamespace: aerie
        install:
          remediation: { retries: 1 }
        upgrade:
          remediation: { retries: 1 }
        values:
          ...
      ```

      Three things this shape depends on:

      - **`reconcileStrategy: Revision`.** The default is `ChartVersion`, which
        ignores a chart edit that does not bump `Chart.yaml`'s `version` — an
        edited template that reconciles to nothing, with every object Ready.
      - **The namespace is not created here.** `aerie` is
        [3b.6's](../../../deploy/cluster/infrastructure/config/namespaces.yaml), and
        `targetNamespace` with no `createNamespace` is what respects that. A
        chart that templates its own Namespace gives one object two owners.
      - **`values:` carries `${...}` tokens and that is the whole idiom
        argument.** The HelmRelease is an ordinary manifest in a Kustomization,
        so `postBuild` substitutes into it textually before it is applied, and
        Helm then sees literals. `valuesFrom` with per-key `targetPath` would
        work and is strictly worse: two substitution mechanisms in one tree.
        The image tags ride the same mechanism — `tag: ${AERIE_API_IMAGE_TAG}`
        and `tag: ${KIOSK_FILES_IMAGE_TAG}`, resolved from the site repo's
        ConfigMap rather than the cluster-config one, so **no concrete tag is
        ever committed here.** That is the whole of 5a.3's argument, expressed
        as two tokens.

      *Exit:* `flux -n flux-system get helmrelease aerie` is Ready; the migrate
      Job reports `Complete`; `kubectl -n aerie get pods` shows 3 api, 2 files,
      1 share, all Running; and each of the four hostnames answers over the VIP
      with a Let's Encrypt production certificate:

      ```sh
      for h in home kiosk files share; do
        curl -sI --resolve $h.${DOMAIN}:443:${INGRESS_VIP} https://$h.${DOMAIN}/ | head -1
      done
      ```

      Then the two behaviours a status check cannot see: `kiosk.${DOMAIN}/`
      returns the dashboard rather than the API landing page, and
      `files.${DOMAIN}/version.json` returns the JSON a tablet polls for.

- [ ] **11. Requests, limits, and the properties goal 2 needs** — the chart's
      templates plus a sweep of Phase 3's HelmReleases.

      [Autoprovisioning is not a feature you switch on](design.md#how-goal-2-actually-works):
      it is a consequence of the scheduler being able to size what it places.
      This step is where that becomes true, and its numbers come from 5a.6.

      On every app container — api, files, share, the migrate hook — set
      `resources.requests` (cpu and memory) and `resources.limits` (memory).
      5a.6 measured them; these are what it concluded:

      | Container | replicas | `requests.cpu` | `requests.memory` | `limits.memory` |
      |---|---|---|---|---|
      | `api` | 3 | `100m` | `384Mi` | `768Mi` |
      | `files` | 2 | `10m` | `32Mi` | `64Mi` |
      | `share` | 1 | `10m` | `32Mi` | `64Mi` |
      | migrate hook Job | 1 | `200m` | `384Mi` | `768Mi` |

      Put them in `values.yaml` under each workload's key, not inline in the
      templates — they are the first thing an operator retunes on different
      hardware, and [Phase 9](phase-9-productization.md) makes `values.yaml` the
      surface where per-installation changes like that are expressed.

      Why these, where they are not just 5a.6's numbers copied across:

      - **`api` requests 384Mi against a 308 MiB observation** because that
        observation was still climbing. The limit at 2× is doing more than
        catching an OOM: .NET sets its GC hard limit to 75% of the cgroup
        limit, so 768Mi hands the runtime a ~576 MiB heap budget and it
        collects rather than grows. Unlimited, it has no reason to ever stop.
      - **Budget three whole copies of `api`, not a third each.** The .NET
        baseline is near-fixed — runtime, JIT'd code, EF model, and a Quartz
        scheduler per replica (5b.5's second sub-point). Three replicas is
        ~1.15Gi requested, not 384Mi divided.
      - **The migrate Job matches `api` on memory** (same image, same EF model)
        and asks for more CPU because Helm blocks the whole release on it. It
        exits, so an overstated request costs nothing after it does.
      - **`share`'s numbers are an estimate, not a measurement** — dufs was not
        running when 5a.6 sampled. It is one replica, so being wrong costs one
        pod; bring [compose.share.yml](../../../compose.share.yml) up and take one
        more reading before 5b.8 if you would rather not guess.
      - **No CPU limits on anything**, and the CPU *requests* above are sized
        for startup and for cpu.shares weighting under contention, not from the
        idle figures — see 5a.6's second bullet for why the measurement cannot
        supply them.

      *Consider adding `DOTNET_gcServer=0` to 5b.5's env block.* ASP.NET Core
      defaults to Server GC, one heap per visible core — four on these nodes,
      with no CPU limit to narrow that. For a workload observed at 0.20% of one
      core, throughput is not the constraint and memory is; workstation GC
      should pull the baseline down far enough to drop the request to 256Mi at
      the next measurement. Add it with a comment saying why, or not at all —
      an unexplained runtime-behavior env var is worse than the memory.

      Then add:

      - `topologySpreadConstraints` on api and files: `maxSkew: 1`,
        `topologyKey: kubernetes.io/hostname`, **`whenUnsatisfiable: ScheduleAnyway`**.
        `DoNotSchedule` is the tempting choice and is wrong during the two-node
        build window — 3 replicas over 2 nodes cannot satisfy it, and the third
        pod sits Pending forever. Revisit at Phase 7, when a third node makes
        `DoNotSchedule` free.
      - `PodDisruptionBudget` on api and files, **`maxUnavailable: 1`**, not
        `minAvailable`. With two nodes and three replicas, `minAvailable: 2`
        blocks a drain of the node holding two of them — a PDB that turns a
        planned reboot into a stuck `kubectl drain` is worse than none, because
        it fails during the Patch Tuesday window Phase 1 schedules.

      Then the sweep, which is the half easily forgotten: the Phase 3
      HelmReleases under
      [`controllers/`](../../../deploy/cluster/infrastructure/controllers/). Check each
      chart's rendered output for containers with no requests and set them —
      cert-manager, external-secrets and kube-vip mostly ship sensible defaults,
      Longhorn's many components mostly do not. A cluster that cannot size its
      own storage layer cannot rebalance around it.

      **Check the total against one node, not against the cluster.** Nodes are
      16 GB / 4 vCPU ([`New-AerieVM.ps1`](../../../scripts/hyperv/New-AerieVM.ps1#L94-L95)),
      so ~15 GiB allocatable; app tier plus CNPG plus Phase 6 plus Longhorn's
      per-node components plus the controllers lands near 9–10 GiB of requests.
      That fits two nodes easily in aggregate, and the number that actually
      matters is whether **one** node can hold it when the other takes its
      staggered reboot. It can, with little to spare — which is the argument
      against padding any of the numbers above "to be safe". Every 100Mi of
      slack on a 3-replica workload is 300Mi the surviving node has to find
      mid-drain.
      *Exit:* the check 5b.14 automates — every container in `aerie` and every
      container in the infrastructure namespaces reports non-empty
      `resources.requests`.

- [ ] **12. Flux image automation, writing to the site repo** —
      `deploy/cluster/apps/automation/`,
      [publish.yml](../../../.github/workflows/publish.yml),
      [`Bootstrap-Flux.ps1`](../../../scripts/flux/Bootstrap-Flux.ps1), and one Provision 3
      re-dispatch. Four parts, in this order, because each is useless without
      the one before:

      1. **A sortable tag.** Add to publish.yml's metadata step, for the `api`
         and `kiosk-files` images:

         ```
         type=raw,value={{date 'YYYYMMDDHHmmss'}}-{{sha}}
         ```

         Alongside `latest` and the raw SHA, not replacing them — the old host's
         compose files pull by SHA until Phase 7. Push at least one build before
         going further, or the policy below has nothing to select and reports it
         as a configuration error.
      2. **The controllers.** Add
         `--components-extra=image-reflector-controller,image-automation-controller`
         to the `flux install` invocation, re-dispatch Provision 3 (it is
         idempotent and upgrades in place), and confirm both new Deployments are
         rolled out. Note what this run does *not* do, since the original plan
         had it doing both: it does not touch `FLUX_GITHUB_TOKEN`, which stays
         `contents:read`. The only write credential in the cluster is
         `aerie-site-git`, it reaches one repository holding one ConfigMap, and
         no controller here can turn it on this one.
      3. **The objects**, all of them in Aerie, because all of them are
         structural. An `ImageRepository` per image
         (`image: ${IMAGE_REGISTRY}/aerie-api`, `secretRef: {name: ghcr-pull}` —
         the flux-system copy from 5b.1, which is what that step's second
         namespace was for), an `ImagePolicy` per image extracting the
         timestamp:

         ```yaml
         filterTags:
           pattern: '^(?P<ts>\d{14})-[0-9a-f]+$'
           extract: '$ts'
         policy:
           numerical: { order: asc }
         ```

         and one `ImageUpdateAutomation` whose `sourceRef` is the **`aerie-site`
         GitRepository** rather than `flux-system`, with `git.push.branch` set
         to the site repo's branch, `update.path: ./`, and a commit template
         naming the image that moved. That one field is the entire difference
         between this design and the one 5a.3 rejected.
      4. **The setter, in the site repo.** 5a.3's `image-tags.yaml` gains the
         markers:

         ```yaml
         data:
           AERIE_API_IMAGE_TAG: latest # {"$imagepolicy": "flux-system:aerie-api:tag"}
           KIOSK_FILES_IMAGE_TAG: latest # {"$imagepolicy": "flux-system:kiosk-files:tag"}
         ```

         Two things to settle here, in this order, because the second is the
         escape hatch for the first:

         - **Prove the marker on a ConfigMap value before building on it.** The
           setter rewrites any marked YAML scalar, so this should work — but
           every documented example marks a Deployment `image:` or a HelmRelease
           value, and "should" is not a thing to discover three steps later.
           Confirm one bump lands in the site repo.
         - **If it does not**, promote `helmrelease.yaml` itself into the site
           repo and mark its `values` tag instead — `chart.spec.sourceRef` keeps
           pointing at the `flux-system` GitRepository, so the chart stays here
           and only the release moves. That is the documented shape, and it is
           also step one of the Phase 9 split recorded at the end of this file,
           so the fallback costs nothing that was not already intended.

         **Only the `:tag` marker, never `:name`.** A `:name` marker makes the
         automation write the policy's fully resolved image name into the file —
         which, whichever file carries it, means this installation's registry
         written down in a commit, permanently, in the last place anyone would
         look for it ([ethos](../../ethos.md)). The comment survives because
         image-automation edits the file in git; kustomize strips comments only
         from the output Flux applies.

      *Exit:* `flux get image all` shows both policies resolved to the newest
      timestamped tag; pushing any commit to main produces, within the
      automation's interval, a bot commit **in the site repo** changing exactly
      the two tag lines; and the HelmRelease rolls to it without a human.
      — *No loop guard is needed, and that is a property of the topology rather
      than good luck: the automation's commit lands in a repository that has no
      workflows, so it cannot re-trigger publish.yml. Confirm the site repo has
      none — that one check replaces the paragraph of reasoning the write-back
      design needed here.*

- [ ] **13. Empty out `cd.yml` of everything that is not a compose deploy** —
      [`provision-0-new-node.yml`](../../../.github/workflows/provision-0-new-node.yml)
      and [cd.yml](../../../.github/workflows/cd.yml).

      The phase bullet says image automation "removes `cd.yml` entirely". It
      cannot, and this is where that gets fixed rather than discovered: the old
      host serves production until Phase 7 moves DNS, and `cd.yml` is the only
      thing that deploys it. But three of its steps have nothing to do with
      compose, and leaving them means Phase 7's deletion is a migration rather
      than a deletion.

      Move the **windows_exporter** and **Tailscale** installs into
      `provision-0-new-node.yml`, which is what already owns "make a Windows host
      ready". Both are install-if-missing then converge-state, which is exactly
      that workflow's shape; neither has anything to do with a deploy, and both
      run on every deploy today for no reason. Per the Phase 5 decision,
      Tailscale **stays on the hosts** rather than moving into the cluster — a
      subnet router advertising the LAN belongs on a machine that has the LAN
      interface, and the three Windows hosts outlive the cutover. Update
      `parameters.json`'s `tailscale/auth-key` entry to say so and drop its
      speculative `phase: 5`.

      Leave the restic step alone: it belongs to Phase 8's backup rework.
      *Exit:* a Provision 0 run on an already-provisioned host converges both
      services with no change; a deploy still succeeds with those steps gone.

- [ ] **14. Phase gate as a command** — `scripts/k3s/Test-AppTier.ps1`, wrapped by
      `.github/workflows/verify-app-tier.yml`, in the exact shape 3b.13 and
      4b.11 established: read-only, **not** numbered into the Provision
      sequence, does not stop at the first failure, and a check it cannot
      evaluate is a failure rather than a skip.

      Take its expectations from the cluster and the committed maps rather than
      from parameters — the hostnames from `DOMAIN` in `aerie-cluster-config`,
      the expected `ExternalSecret` set from `parameters.json` — so it keeps
      checking the right things after a variable moves.

      Assert, at minimum:

      - the `HelmRelease` is Ready, and the most recent migrate hook Job
        reports `Complete` — asserted on the Job directly, because a hook's
        outcome is not separately visible in HelmRelease status
      - api is 3/3 and files 2/2, **spread across distinct nodes**, and no
        container has restarted since its pod started
      - `ghcr-pull` exists in both namespaces, is `dockerconfigjson`, and no pod
        in `aerie` is in `ImagePullBackOff`
      - **every container in `aerie` declares `resources.requests`** — the
        mechanical form of 5b.11, and the one assertion here that catches a
        regression a year from now
      - both PDBs exist and report a non-zero `disruptionsAllowed`
      - all four `Ingress` objects are admitted by Traefik, and each hostname
        answers 200 over the VIP with a **production** Let's Encrypt certificate
      - `kiosk.${DOMAIN}/` serves the dashboard (assert on the response body,
        not the status code — the unrewritten route also returns 200)
      - `files.${DOMAIN}/app-release.apk` and `/version.json` both serve
      - `share.${DOMAIN}/` challenges for authentication rather than listing
      - both SMB PVCs are `Bound`, and `ls /share-ro/<subpath>` inside an api
        pod lists something
      - the `aerie-site` `GitRepository` is Ready and the `aerie-image-tags`
        ConfigMap carries both keys, non-empty — the apps layer substitutes from
        it, so a missing or stale one is an empty tag rather than an error
      - both `ImagePolicy` objects have resolved to a tag, and the last
        `ImageUpdateAutomation` run succeeded

      Run the [portability check](design.md#verification) over `charts/` and
      `deploy/cluster/apps/` before ticking: grep for the domain, the registry
      owner, the share host and any address — each should appear only as a
      `${...}` substitution or a Helm value. Add one grep this phase makes newly
      necessary: a 14-digit timestamped tag anywhere under `deploy/` or
      `charts/` means the write-back has crept back in and the site repo is
      being bypassed. Add `helm lint` and
      `helm template charts/aerie` to
      [ci.yml](../../../.github/workflows/ci.yml)'s `deploy-manifests` job, since
      nothing else builds the chart before the cluster does.
      *Exit:* the workflow exits 0. Leave this box unticked until it has, for
      the same reason 3b.13 and 4b.11 stayed unticked: a gate that has never
      passed has proved nothing.

---

## Where these files live

```text
charts/aerie/                     # the chart itself - outside deploy/, which
  Chart.yaml                      #   holds objects, not packages
  values.yaml
  templates/
    _helpers.tpl
    migrate-job.yaml              # 5b.6, helm.sh/hook
    api-deployment.yaml           # 5b.5
    api-service.yaml
    files-deployment.yaml         # 5b.8
    files-service.yaml
    share-deployment.yaml         # 5b.8
    share-service.yaml
    share-volumes.yaml            # 5b.7, two PV/PVC pairs
    ingress.yaml                  # 5b.9, four hosts, no tls: block
    middleware-kiosk.yaml         # 5b.9
    poddisruptionbudgets.yaml     # 5b.11

deploy/cluster/
  kustomization.yaml              # + site.yaml, + apps.yaml
  site.yaml                       # 5b.4, two Kustomizations: site-source,
                                  #   then site-config sourced from the site repo
  site/
    gitrepository.yaml            # 5b.4, the aerie-site source, url from ${...}
    kustomization.yaml
  apps.yaml                       # the Flux Kustomization, dependsOn
                                  #   [data-schema, site-config]
  apps/
    helmrelease.yaml              # 5b.10, the ${...} values, image tags included
    automation/
      image-repositories.yaml     # 5b.12
      image-policies.yaml
      image-update-automation.yaml  # writes to the site repo, not to this one
    kustomization.yaml
```

And, outside this repository entirely:

```text
aerie-site-<something>/           # private, per installation, no workflows
  image-tags.yaml                 # 5a.3 seeds it; 5b.12's automation writes it
```

The site repo is the only thing the cluster can write to, and everything in it
is true of exactly one installation. Those are the same sentence read from
either end: Aerie holds the shape, the site repo holds the state the cluster
produces about itself.

One Kustomization, not two: unlike Phase 4, there is nothing here to order
against anything else. Helm's hook mechanism does the ordering *inside* the
release, which is the whole reason this tier is a chart, and the layer boundary
that matters — schema before app — is expressed by `dependsOn: data-schema` on
the one Kustomization.

The chart lives at the repository root rather than under `deploy/`, and Flux
reaches it through the same `flux-system` `GitRepository` (which clones the
whole repository and sets no `ignore`). Keeping it out of `deploy/` keeps that
tree's rule intact: everything under it is an object the cluster holds, and a
chart is a package that produces them.

## What Phase 5 deliberately does not do

- **It does not delete `cd.yml`, `compose.prod.yml` or `compose.share.yml`.**
  The old host serves production until Phase 7. 5b.13 removes what does not
  belong there so the deletion, when it comes, is one.
- **It does not touch `appsettings.Docker.json`.** Finding 2's outcome is
  delivered by environment variables that outrank the file; the file dies with
  compose, in Phase 7.
- **It does not add the kiosk Wi-Fi `ExternalSecret`.** See 5b.14's note below
  and the re-scope preamble: the generator refuses it, correctly.
- **It does not move Tailscale into the cluster.** Decided, not deferred; the
  reasoning is in 5b.13.
- **It does not give the cluster write access to this repository.** 5a.3 is
  where that was declined, and why.
- **It does not move the HelmRelease, the chart values, or anything else into
  the site repo.** One ConfigMap is the smallest thing that gets the cluster's
  write out of Aerie. The full template-repo/site-repo split is Phase 9, below.
- **It does not add a `ServiceMonitor` or any scrape configuration.** Phase 6
  registers those CRDs.
- **It does not raise `POSTGRES_INSTANCES` or `LONGHORN_REPLICA_COUNT`.** Both
  are Phase 7, both are repository variables, neither is a commit.

## Additions this phase makes to other phases

Recorded where they belong rather than here:

- **Phase 7 inherits the `cd.yml` deletion**, now reduced to deleting compose
  files, the `aerie-caddy` publish job, `containers/caddy/`, and the
  connection strings in `appsettings.Docker.json`. Add
  "re-point pfSense Unbound at `${INGRESS_VIP}`" beside it if it is not already
  explicit — until that happens, every hostname in this phase is reachable only
  by `--resolve`.
- **Phase 7 should revisit `whenUnsatisfiable`** on 5b.11's
  `topologySpreadConstraints`: with a third node, `DoNotSchedule` becomes free
  and turns a soft preference into a guarantee. Same sentence as 4b.6's
  `dataDurability` note, for the same reason.
- **Phase 9 inherits the site repo split.** This phase creates
  `aerie-site-<something>` for one narrow reason — somewhere for image
  automation to commit that is not here — but the mechanism generalizes to the
  thing the ethos has been working around all along: per-installation
  *structure*, not merely per-installation strings. Today anything site-specific
  has to be expressible as a `${...}` token, so an operator who wants one extra
  Ingress, a different replica count, or a workload Aerie does not ship has to
  fork; with a site repo they patch. The Phase 9 shape: the HelmRelease and its
  values move there, Aerie ships `deploy/site-template/` as what a new site repo
  is seeded from,
  [`Bootstrap-Flux.ps1`](../../../scripts/flux/Bootstrap-Flux.ps1) takes the site repo
  as an explicit parameter instead of deriving owner/repo from the dispatching
  run, and the installer grows a "create the site repo from the template" step.
  Two consequences worth writing down now, while the reasoning is fresh. First,
  the SOPS rejection in [design.md](design.md#secrets--no-bytes-in-git) was a
  product-vision rejection — *one operator's ciphertext sitting in a shared
  artifact* — and a private per-installation repo is not a shared artifact, so
  committed encrypted values stop being forbidden there. Keep ESO as the
  mechanism rather than running two secret systems, but the door is open for
  what ESO handles badly. Second, per-installation values would then live in
  both GitHub variables and a git repo, which is one home too many; collapsing
  `cluster-config.json`'s values into the site repo is the natural end state and
  should be decided deliberately rather than drifted into.
- **Phase 9 owns moving the kiosk Wi-Fi password out of `SiteSettings`.** It is
  the last consumer of `SecretObfuscator`'s XOR alongside the HA token, which
  [goal 6.1](design.md#goal-6--beyond-the-six) already calls out for replacement —
  so the parameter, the obfuscator and the `ExternalSecret` are one piece of
  work, not three. Until then `kiosk/wifi-password` stays `required: false`
  with no manifest, and its `description` should say which phase changes that.
