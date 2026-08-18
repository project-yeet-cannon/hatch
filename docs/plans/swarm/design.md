# Cluster — design and decisions

Moving Aerie from one Windows Docker host to a resilient 3-node cluster.

> Originally scoped as "should we use Docker Swarm?" — Swarm was evaluated and
> rejected. See [Why not Swarm](#why-not-swarm).

## Phases

The implementation plan lives one file per phase. This document holds everything
the phases share — goals, decisions, findings, and the verification bar they are
each measured against.

| Phase | Status |
|---|---|
| [Phase 0 — Backup + DR on the current host](phase-0-backup-and-dr.md) | Complete |
| [Phase 1 — Node substrate](phase-1-node-substrate.md) | Complete |
| [Phase 2 — k3s + Flux + secrets](phase-2-k3s-flux-secrets.md) | Complete |
| [Phase 3 — Platform services](phase-3-platform-services.md) | Complete |
| [Phase 4 — Data tier](phase-4-data-tier.md) | Not started |
| [Phase 5 — App tier](phase-5-app-tier.md) | Not started |
| [Phase 6 — Observability](phase-6-observability.md) | Not started |
| [Phase 7 — Cutover](phase-7-cutover.md) | Not started |
| [Phase 8 — Backup v2 + rehearsal](phase-8-backup-v2.md) | Not started |
| [Phase 9 — Productization + docs](phase-9-productization.md) | Not started |

Phases 0–3 are kept as written rather than summarised. They are the record of
what was decided and why, and their knowledge is dissipated into the permanent
docs — [`delivery-architecture.md`](../../delivery-architecture.md),
[`secrets-architecture.md`](../../secrets-architecture.md), and the
`cluster-architecture.md` Phase 9 calls for — as this directory is closed out.

---

## Goals

1. **Decentralized deployment** — deploy across all servers, expand to any
   number of hosts, cattle-not-pets.
2. **Autoprovisioning** — redistribute services when a host dies or runs out of
   overhead; minimal downtime.
3. **Data volume sync** — volumes consistent across a changing server landscape,
   without a NAS and without vendor lock-in.
4. **Data backup** — rule-of-three: two local copies, one remote, automatic.
5. **Disaster recovery** — scripted cold-start rebuild, doubling as first-time
   setup so the stack is releasable as a product.
6. **Operational maturity** — anything else a serious distributed system needs.

## Decisions

| Question | Decision |
|---|---|
| Node OS | One Hyper-V **Linux VM per Windows host**; Windows stays as hypervisor |
| Orchestrator | **k3s** — 3 servers, embedded etcd |
| Ingress / TLS | **Traefik** (k3s-bundled) + **cert-manager**, Route53 DNS-01. Caddy deleted |
| Deploy model | **Flux** GitOps, reconciling from this repo. The cluster never writes back to it — see [The site repo](#the-site-repo--what-the-cluster-writes) |
| Postgres | **CloudNativePG** — 3 instances, synchronous replication |
| Volumes | **Longhorn**, except Postgres (see [Storage split](#storage-split)) |
| Ingress IP | **kube-vip** ARP-mode floating VIP |
| Backup | **restic** → local repo + **AWS S3**; CNPG WAL archiving to S3 |
| Secrets | **External Secrets Operator**, git holds pointers only. Never secret bytes, encrypted or not — see [Secrets](#secrets--no-bytes-in-git) |
| HA required | Postgres, API, kiosk `files`, ingress |
| HA *not* required | Observability — reschedule-on-failure is acceptable |
| Sequencing | **Backup + DR first**, on the current host, before any cluster work |
| Hardware | 24 / 32 / 32 GB hosts → 16 / 24 / 24 GB VMs (~64 GB cluster) |

### Why not Swarm

Two independent reasons:

- Multi-node Swarm is **unsupported on Docker Desktop** — overlay networking
  (VXLAN 4789) doesn't traverse WSL2's NAT. Making it work needs Linux hosts
  anyway, which removes Swarm's only real advantage.
- Swarm has no credible answer for goals 3, 4, and 5: no distributed volume
  driver that isn't a NAS or a vendor plugin, no Postgres HA operator, no backup
  tooling. All three become bespoke code to own forever.

Since Linux VMs are on the table regardless, k3s costs a steeper learning curve
once and buys mature off-the-shelf answers for every goal.

### Secrets — no bytes in git

This plan originally specified SOPS + age with encrypted secrets committed to the
repo. **That is rejected**, per [`docs/ethos.md`](../../ethos.md): Aerie is meant
to be open-sourced and redeployed by other operators, and a committed encrypted
secret is one operator's secret sitting in a shared artifact — meaningless to
everyone downstream, and architecture (`.sops.yaml` creation rules) that only
makes sense if the repo has exactly one owner. This is a product-vision
constraint, not a security judgment; SOPS-in-git is sound crypto.

The replacement is **External Secrets Operator (ESO)**. Git holds an
`ExternalSecret` naming a path in a secret store; ESO reads the value at runtime
and materializes a real k8s `Secret` in-cluster. Flux still reconciles
everything, and the committed manifest is structural — identical for every
installation.

**The store is a deployment parameter, not a decision this table makes.** ESO
speaks to many backends; the interface is committed, the provider is chosen per
install. This installation uses **AWS SSM Parameter Store** — the AWS account
already exists for Route53 DNS-01 and the restic S3 bucket, `SecureString`
parameters are free at this scale (Secrets Manager would be $0.40/secret/month),
and it puts the keys off-site, which is a better DR story than a key that only
lives on the nodes it protects. **A self-hosted provider — in-cluster OpenBao —
is required before open-sourcing**, since forcing every home user to open an AWS
account to run a home server defeats the premise. Phase 3 keeps the
`ClusterSecretStore` isolated so swapping it touches one manifest.

Rotation becomes: change the parameter, ESO re-syncs on `refreshInterval`,
reloader bounces the pods. **No commit, no deploy** — which is what Goal 6.1
actually asked for and something SOPS never delivers, since SOPS rotation *is* a
commit.

Bootstrap chain — one imperative secret per installation, and only one:

```text
printed / offline copy
  └→ GitHub Actions repository secrets
       └→ bootstrap Secret in-cluster (created by workflow, never in git)
            └→ ESO ClusterSecretStore
                 └→ every other secret in the cluster
```

### The site repo — what the cluster writes

Reconciliation is one-way by design: Flux reads this repo and nothing in the
cluster writes to it. Phase 5's image automation is the first thing that wants
to, and the usual answer — a `contents:write` token in the `flux-system`
Secret — is rejected twice over. It is a credential *held by the cluster over
the repository that governs the cluster*, so a compromised workload no longer
stops at the cluster boundary; and the value it would commit, a resolved image
tag, is an operator value in the shared artifact, which is the one thing
[`docs/ethos.md`](../../ethos.md) rules out.

The replacement is a **private per-installation site repo**. Aerie is the
template; the site repo holds what is true of exactly one installation and what
the cluster produces about itself. Phase 5 gives it a single ConfigMap of image
tags, reached by a second `GitRepository` and consumed through the same
`postBuild.substituteFrom` every other operator value already uses, with a
write token scoped to that repo alone.

The narrow version is deliberate. The general version — the HelmRelease and its
values living there, so an operator can express per-installation *structure*
rather than only per-installation strings, without forking — is
[Phase 9](phase-5-app-tier.md#additions-this-phase-makes-to-other-phases), and
it is also where the SOPS question reopens: a private per-installation repo is
not a shared artifact, so the product-vision objection above does not reach it.

---

## Findings — what actually breaks

Verified against the repo, not assumed.

### 1. EF migrations at startup — the one real code change

[Program.cs:143-153](../../../src/Aerie.Api/Program.cs#L143-L153) runs `MigrateAsync()`,
then `SeedAsync()`, then `haConnection.ApplyAsync()` inline at boot, unguarded.
Three replicas starting at once means three concurrent migration attempts
against one database.

Move migrations into a Helm `pre-install`/`pre-upgrade` hook Job that runs
exactly once per deploy, and gate the API pods behind it. `SeedAsync` and
`ApplyAsync` either move to the same Job or get verified idempotent under
concurrency.

`JobsInit.WireUpJobs()` ([Program.cs:156-161](../../../src/Aerie.Api/Program.cs#L156-L161))
also runs per replica. Quartz clustering handles *execution*, but concurrent
`ScheduleJob`/`StoreJob` calls need a confirmed `replace: true` path.

**Also verify:** no ASP.NET DataProtection key persistence is configured
anywhere. Invisible with one replica; with three, each pod generates its own
ephemeral key ring. Confirm nothing (antiforgery, cookies, TempData) depends on
it, or persist keys to Postgres.

### 2. Postgres credentials and exposure

[compose.prod.yml:19-20](../../../compose.prod.yml#L19-L20) sets `POSTGRES_USER: user` /
`POSTGRES_PASSWORD: password`, matched by hardcoded connection strings in
[appsettings.Docker.json:10-11](../../../src/Aerie.Api/appsettings.Docker.json#L10-L11).
[compose.prod.yml:14](../../../compose.prod.yml#L14) publishes `5432:5432` on **all
interfaces** — anything on the LAN has full database access today.

Phase 4 removes both: CNPG generates credentials into a Secret, and nothing is
published. Connection strings become env vars (`ConnectionStrings__Aerie`,
`ConnectionStrings__Quartz`) sourced from CNPG's `<cluster>-app` Secret, with
host `aerie-pg-rw`.

### 3. `pginit.sql` doesn't survive CNPG as-is

[containers/aerie-db/pginit.sql](../../../containers/aerie-db/pginit.sql) creates **two**
databases (`aerie`, `quartz`), then uses `\c quartz` — a *psql meta-command, not
SQL* — before ~190 lines of Quartz DDL. CNPG's `postInitApplicationSQL` executes
SQL, not psql scripts, so `\c` fails there.

Plan: CNPG bootstraps `aerie`; declare `quartz` via CNPG's `Database` CRD; apply
the Quartz DDL from a one-shot Job running `psql -d quartz -f`.

⚠️ The DDL's `CREATE TABLE` statements have **no `IF NOT EXISTS`**, so that Job
is not re-runnable. Add the guard, or make it a `pre-install`-only hook.

*Simplification worth considering:* Quartz supports a table prefix, so the
`qrtz_*` tables could live in the `aerie` database instead — collapsing two
databases into one and making CNPG bootstrap trivial. It's a behavior change, so
it's an option, not the default.

### 4. fluent-bit needs a rewrite (moderate)

[fluent-bit.conf](../../../containers/fluent-bit/fluent-bit.conf) tails
`/var/lib/docker/containers/*/*.log` with the `docker` parser.
[service_tag.lua](../../../containers/fluent-bit/service_tag.lua) derives the `service`
field from `record["attrs"]["com.docker.compose.service"]`, which the
`x-logging` anchor ([compose.prod.yml:4-6](../../../compose.prod.yml#L4-L6)) attaches.

Under k3s, containerd writes CRI-format logs to `/var/log/containers/*.log`.
Input path and parser both change; the compose-label branch of the Lua dies and
is replaced by fluent-bit's `kubernetes` filter.

**The `State.Service` branch survives and still matters** — that's what
attributes `UiLogsController` lines to the specific frontend (dashboard vs
admin) instead of lumping them under the API. Keep that half.

### 5. Prometheus scrape topology

[prometheus.yml](../../../containers/prometheus/prometheus.yml) has three static targets:
itself, `host.docker.internal:9182` (windows_exporter), and `cadvisor:8080`.

- `host.docker.internal` doesn't exist in k8s — and windows_exporter now runs on
  the three Windows **hosts**, which sit *outside* the cluster. Becomes three
  static targets at the hosts' real LAN IPs. Still worth scraping: those are the
  physical machines.
- The standalone cadvisor container goes away — the k3s kubelet already exposes
  cAdvisor metrics at `/metrics/cadvisor`.
- Add node-exporter for the three Linux VMs.

`kube-prometheus-stack` brings node-exporter, kube-state-metrics, and the
kubelet/cadvisor scrape wiring pre-configured; windows_exporter targets go in as
`additionalScrapeConfigs`. The `cd.yml` Prometheus hot-reload step
([cd.yml:101-103](../../../.github/workflows/cd.yml#L101-L103)) disappears — the operator
handles config reload.

### 6. Provisioning one-shots become Jobs

`kuma-provision`, `opensearch-provision`, and `autokuma` are one-shot compose
services today. They become k8s Jobs with their scripts in ConfigMaps.

`kuma-provision` currently runs `pip install ... && python provision.py` at every
deploy ([compose.observability.yml:66](../../../compose.observability.yml#L66)) — make it
a built image so a PyPI outage can't block a deploy.

### 7. Things that simply disappear

- `containers/caddy/` and the `aerie-caddy` image — one fewer image to build and
  publish. `caddy_data` goes with it: cert-manager stores certs as Secrets, so
  it's no longer a volume needing replication.
- The `edge` external network and its manual `docker network create` prerequisite.
- `DOCKER_HOST: tcp://localhost:2375` ([cd.yml:22](../../../.github/workflows/cd.yml#L22))
  — an unauthenticated, root-equivalent Docker API. Retiring the self-hosted
  runner removes it. Genuine security fix.
- The implicit compose project name, derived from the runner's checkout
  directory, which made volume names path-dependent.
- All `./containers/*` relative bind mounts → ConfigMaps.

### 8. Already in your favor

Quartz **already** runs `UseClustering()` on Postgres
([Program.cs:70](../../../src/Aerie.Api/Program.cs#L70)), so multiple API replicas won't
double-fire scheduled jobs. The API is otherwise stateless — all state lives in
`pgdata`. The app tier is essentially ready to scale horizontally today, which
is why API + `files` HA is the cheapest win in the whole stack.

---

## Target architecture

```
Windows host A (24GB)     Windows host B (32GB)     Windows host C (32GB)
  └ Hyper-V VM (16GB)       └ Hyper-V VM (24GB)       └ Hyper-V VM (24GB)
     k3s server 1              k3s server 2              k3s server 3
     (embedded etcd — quorum 3, tolerates exactly 1 node loss)

  kube-vip ARP VIP ─→ Traefik ─→ home / kiosk / files / status / logs / metrics
  cert-manager     ─→ one wildcard *.${DOMAIN} cert (Route53 DNS-01), as a Secret
  CloudNativePG    ─→ 3 Postgres instances, sync replication, local-path PVs
  Longhorn         ─→ replicated volumes: grafana / kuma / opensearch / prometheus
  Flux             ─→ reconciles all of the above from this repo
```

### Storage split

**Postgres does not go on Longhorn.** Running it on distributed block storage
means replicating twice — Postgres streaming replication layered on Longhorn's —
which is wasteful and slow. CNPG instances use k3s's built-in `local-path` class
with anti-affinity across nodes, and Postgres handles its own replication.

Longhorn is reserved for the small stateful leftovers, with per-volume replica
counts: **3** for tiny critical volumes (grafana, kuma), **2** for bulk where
loss is tolerable (opensearch, prometheus).

### How goal 2 actually works

Autoprovisioning is not a feature you switch on. It's a consequence of
**declaring resource requests and limits on every workload**, plus
`topologySpreadConstraints` and PodDisruptionBudgets. Without requests, the
scheduler cannot make placement decisions or rebalance when a node runs short.
This step must not be skipped.

---

## Goal 6 — beyond the six

Ranked by what actually bites:

1. **Secrets can't stay in GitHub Actions env — for the platform long-term.**
   Today a rotation requires a deploy, and DR requires manually re-entering
   everything. ESO + an external store is what makes goal 5 and "release as a
   product" achievable at all — and it beats the SOPS + age this plan
   originally called for on exactly the rotation point, since a SOPS rotation
   *is* a commit. GitHub Actions secrets remain correct for the bootstrap
   credential and the Phase 0 restic root of trust; the critique applies to
   everything downstream of those. Also replace
   `SecretObfuscator`'s XOR (`src/Aerie.Api/Common/SecretObfuscator.cs:10-32`) —
   it's obfuscation, not encryption, and it guards the HA token and kiosk Wi-Fi
   password.
2. **"Two local copies" is really one copy.** Two copies in the same house on the
   same circuit are a single copy against fire, flood, theft, or surge. Add a UPS
   and treat S3 as the real second copy, not the third.
3. **Image availability during DR.** GHCR is external — a rebuild during an
   internet outage can't pull anything. Run a `registry:2` pull-through cache, or
   fold `docker save` output into the restic backup.
4. **Alert on the cluster itself**: node NotReady, etcd quorum, Longhorn degraded
   volumes, CNPG replication lag — plus backup age from Phase 8.
5. **Renovate or Dependabot for image tags.** You're about to pin versions across
   a dozen Helm charts; automate the bumps or they rot.
6. **Rehearse DR or it's fiction.** Phase 9's scripts are only real once they've
   been run against empty VMs.
7. **Make the ethos mechanical.** [ci.yml](../../../.github/workflows/ci.yml) has no
   secret scanning today. Add `gitleaks` plus a path-deny check that fails on
   `*.agekey`, `*.pem`, `id_*`, `.sops.yaml`, `*.enc.yaml` — a rule enforced only
   by memory is a rule that lapses. See [`docs/ethos.md`](../../ethos.md).

---

## Verification

- **Phase 0 gate** — a real restore completed onto a scratch VM before any
  cluster work begins
- **Per phase** — `flux get all` clean; `kubectl get nodes` all Ready
- **Phase 3 gate** — [`scripts/k3s/Test-ClusterPlatform.ps1`](../../../scripts/k3s/Test-ClusterPlatform.ps1)
  exits 0 (Phase 3b.13), dispatched as *Verify: Cluster platform*
- **HA proof (the real test)** — hard-power-off one node and confirm the VIP
  moves and the site stays up, CNPG promotes a replica, API pods reschedule, and
  Longhorn volumes rebuild. Do this for **each** of the three nodes, not just one
- **Data** — `cnpg status` shows 3 instances streaming, sync replica healthy
- **Backups** — restore the newest snapshot into a scratch Postgres and query it
- **App** — all seven hostnames serve with a valid wildcard cert; kiosk tablets
  reconnect on their own after a node kill
- **Frontend** — build + lint per the usual convention; in-browser verification
  stays with the user
- **Portability** — no phase is done if a second operator couldn't run it on
  their own hardware without asking questions. Grep the phase's new files for
  hardcoded domains, IPs, and hostnames before ticking it
  ([`docs/ethos.md`](../../ethos.md))

## File impact

**New:** `deploy/` (Flux tree), `charts/aerie/`, `scripts/`,
`docs/cluster-architecture.md`, `docs/disaster-recovery.md`,
[`docs/ethos.md`](../../ethos.md),
[`docs/secrets-architecture.md`](../../secrets-architecture.md),
[`scripts/secrets/`](../../../scripts/secrets/), [`scripts/flux/`](../../../scripts/flux/),
[`.github/workflows/provision-2-seed-secrets.yml`](../../../.github/workflows/provision-2-seed-secrets.yml),
[`.github/workflows/provision-3-bootstrap-flux.yml`](../../../.github/workflows/provision-3-bootstrap-flux.yml)

*Phase 3 adds:*
[`scripts/k3s/Set-ClusterConfig.ps1`](../../../scripts/k3s/Set-ClusterConfig.ps1),
[`scripts/k3s/cluster-config.json`](../../../scripts/k3s/cluster-config.json),
[`scripts/k3s/Initialize-NodeStorage.ps1`](../../../scripts/k3s/Initialize-NodeStorage.ps1),
[`scripts/k3s/Test-ClusterPlatform.ps1`](../../../scripts/k3s/Test-ClusterPlatform.ps1),
[`scripts/secrets/New-ExternalSecrets.ps1`](../../../scripts/secrets/New-ExternalSecrets.ps1),
[`.github/workflows/provision-4-cluster-config.yml`](../../../.github/workflows/provision-4-cluster-config.yml),
[`.github/workflows/provision-5-node-storage.yml`](../../../.github/workflows/provision-5-node-storage.yml),
[`.github/workflows/verify-cluster-platform.yml`](../../../.github/workflows/verify-cluster-platform.yml),
and the `deploy/cluster/infrastructure/` tree.

**Modified:** [Program.cs](../../../src/Aerie.Api/Program.cs) (migrations → Job),
[appsettings.Docker.json](../../../src/Aerie.Api/appsettings.Docker.json) (connection
strings → env), [ci.yml](../../../.github/workflows/ci.yml) (secret scanning; the
repo-side half of 3b.13's portability check),
[containers/fluent-bit/](../../../containers/fluent-bit/),
[containers/prometheus/prometheus.yml](../../../containers/prometheus/prometheus.yml),
[containers/aerie-db/pginit.sql](../../../containers/aerie-db/pginit.sql)

**Deleted:** [containers/caddy/](../../../containers/caddy/),
[compose.prod.yml](../../../compose.prod.yml),
[compose.observability.yml](../../../compose.observability.yml),
[compose.metrics.yml](../../../compose.metrics.yml),
[.github/workflows/cd.yml](../../../.github/workflows/cd.yml), and the `aerie-caddy` job
in [publish.yml](../../../.github/workflows/publish.yml)
