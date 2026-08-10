# Cluster

Moving Aerie from one Windows Docker host to a resilient 3-node cluster.

> Originally scoped as "should we use Docker Swarm?" — Swarm was evaluated and
> rejected. See [Why not Swarm](#why-not-swarm).

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
| Deploy model | **Flux** GitOps, reconciling from this repo |
| Postgres | **CloudNativePG** — 3 instances, synchronous replication |
| Volumes | **Longhorn**, except Postgres (see [Storage split](#storage-split)) |
| Ingress IP | **kube-vip** ARP-mode floating VIP |
| Backup | **restic** → local repo + **AWS S3**; CNPG WAL archiving to S3 |
| Secrets | **SOPS + age**, encrypted in git (Phase 0's restic secrets are a scoped exception — see Phase 0) |
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

---

## Findings — what actually breaks

Verified against the repo, not assumed.

### 1. EF migrations at startup — the one real code change

[Program.cs:143-153](src/Aerie.Api/Program.cs#L143-L153) runs `MigrateAsync()`,
then `SeedAsync()`, then `haConnection.ApplyAsync()` inline at boot, unguarded.
Three replicas starting at once means three concurrent migration attempts
against one database.

Move migrations into a Helm `pre-install`/`pre-upgrade` hook Job that runs
exactly once per deploy, and gate the API pods behind it. `SeedAsync` and
`ApplyAsync` either move to the same Job or get verified idempotent under
concurrency.

`JobsInit.WireUpJobs()` ([Program.cs:156-161](src/Aerie.Api/Program.cs#L156-L161))
also runs per replica. Quartz clustering handles *execution*, but concurrent
`ScheduleJob`/`StoreJob` calls need a confirmed `replace: true` path.

**Also verify:** no ASP.NET DataProtection key persistence is configured
anywhere. Invisible with one replica; with three, each pod generates its own
ephemeral key ring. Confirm nothing (antiforgery, cookies, TempData) depends on
it, or persist keys to Postgres.

### 2. Postgres credentials and exposure

[compose.prod.yml:19-20](compose.prod.yml#L19-L20) sets `POSTGRES_USER: user` /
`POSTGRES_PASSWORD: password`, matched by hardcoded connection strings in
[appsettings.Docker.json:10-11](src/Aerie.Api/appsettings.Docker.json#L10-L11).
[compose.prod.yml:14](compose.prod.yml#L14) publishes `5432:5432` on **all
interfaces** — anything on the LAN has full database access today.

Phase 4 removes both: CNPG generates credentials into a Secret, and nothing is
published. Connection strings become env vars (`ConnectionStrings__Aerie`,
`ConnectionStrings__Quartz`) sourced from CNPG's `<cluster>-app` Secret, with
host `aerie-pg-rw`.

### 3. `pginit.sql` doesn't survive CNPG as-is

[containers/aerie-db/pginit.sql](containers/aerie-db/pginit.sql) creates **two**
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

[fluent-bit.conf](containers/fluent-bit/fluent-bit.conf) tails
`/var/lib/docker/containers/*/*.log` with the `docker` parser.
[service_tag.lua](containers/fluent-bit/service_tag.lua) derives the `service`
field from `record["attrs"]["com.docker.compose.service"]`, which the
`x-logging` anchor ([compose.prod.yml:4-6](compose.prod.yml#L4-L6)) attaches.

Under k3s, containerd writes CRI-format logs to `/var/log/containers/*.log`.
Input path and parser both change; the compose-label branch of the Lua dies and
is replaced by fluent-bit's `kubernetes` filter.

**The `State.Service` branch survives and still matters** — that's what
attributes `UiLogsController` lines to the specific frontend (dashboard vs
admin) instead of lumping them under the API. Keep that half.

### 5. Prometheus scrape topology

[prometheus.yml](containers/prometheus/prometheus.yml) has three static targets:
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
([cd.yml:101-103](.github/workflows/cd.yml#L101-L103)) disappears — the operator
handles config reload.

### 6. Provisioning one-shots become Jobs

`kuma-provision`, `opensearch-provision`, and `autokuma` are one-shot compose
services today. They become k8s Jobs with their scripts in ConfigMaps.

`kuma-provision` currently runs `pip install ... && python provision.py` at every
deploy ([compose.observability.yml:66](compose.observability.yml#L66)) — make it
a built image so a PyPI outage can't block a deploy.

### 7. Things that simply disappear

- `containers/caddy/` and the `aerie-caddy` image — one fewer image to build and
  publish. `caddy_data` goes with it: cert-manager stores certs as Secrets, so
  it's no longer a volume needing replication.
- The `edge` external network and its manual `docker network create` prerequisite.
- `DOCKER_HOST: tcp://localhost:2375` ([cd.yml:22](.github/workflows/cd.yml#L22))
  — an unauthenticated, root-equivalent Docker API. Retiring the self-hosted
  runner removes it. Genuine security fix.
- The implicit compose project name, derived from the runner's checkout
  directory, which made volume names path-dependent.
- All `./containers/*` relative bind mounts → ConfigMaps.

### 8. Already in your favor

Quartz **already** runs `UseClustering()` on Postgres
([Program.cs:70](src/Aerie.Api/Program.cs#L70)), so multiple API replicas won't
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

## Implementation plan

### Phase 0 — Backup + DR on the current host

*No cluster involved. Delivers goals 4 and 5 immediately and de-risks everything
after it.*

- [x] restic repos: local (second disk) + AWS S3 — `containers/backup/`
      (built on `postgres:18.4-alpine` for a version-matched `pg_dumpall`),
      wired in as `compose.backup.yml`. `cd.yml` inits both repos idempotently
      on every deploy (`restic snapshots` fails → `restic init`)
- [x] Generate the repo password, store as a **GitHub Actions secret**
      (`RESTIC_PASSWORD`), and **print it once for offline storage** — a
      backup you can't decrypt isn't one. *(Scoped deviation from the SOPS +
      age decision above: Phase 0 needed a secret store before Phase 2 exists
      to provide one. GitHub Actions secrets match the pattern already used
      for the Route53/HA credentials — `RESTIC_PASSWORD` /
      `RESTIC_AWS_ACCESS_KEY_ID` / `RESTIC_AWS_SECRET_ACCESS_KEY`, kept
      separate from caddy's Route53 credentials via a dedicated `aerie-restic`
      IAM user, scoped to only the backup bucket — and get replaced by SOPS +
      age when Phase 2 lands.)*
- [x] Back up correctly per service, not by copying volume directories:
      `pg_dumpall` for Postgres, `sqlite3 .backup` for Grafana and Kuma, the
      OpenSearch snapshot API, the Prometheus TSDB snapshot endpoint —
      `containers/backup/scripts/backup.sh`, daily via cron (supercronic)
- [x] Retention `--keep-daily 7 --keep-weekly 4 --keep-monthly 12`, scheduled —
      same script, run against both repos after every backup
- [x] Restore-verification job: restore the newest snapshot into a scratch
      Postgres and run a sanity query. Untested backups are not backups —
      `containers/backup/scripts/verify-restore.sh`, weekly via cron, using
      `initdb`/`pg_ctl` from the image's own postgres install (no extra
      container or docker-socket access needed)
- [x] Write `docs/disaster-recovery.md`
- [x] **Perform one full restore onto a scratch VM**.

> **Gate:** do not start Phase 1 until a restore has actually been performed.
> The weekly verification job above proves the backup *contents* are valid —
> it restores into a throwaway Postgres inside the backup container, not a
> standalone VM, so it does not by itself satisfy this gate.

### Phase 1 — Node substrate

> Provisioning is scripted in [`scripts/hyperv/`](scripts/hyperv/): run
> `Initialize-AerieNode.ps1` in an elevated session on each host, or dispatch
> the **Provision node VM** workflow at that host's runner. Both are the same
> code path.
>
> **`[x]` here means the script does it, not that a machine exists yet** —
> unlike Phase 0, where every tick describes something already running. These
> stop being aspirational once the script has been run once per host; the
> unchecked items below are the ones that stay manual no matter how many times
> it runs.

- [x] One Hyper-V Linux VM per host (Debian 13 / Ubuntu 24.04 LTS), **external
      virtual switch** so each VM gets its own LAN IP; DHCP reservations on the MACs
      — *VM, distro and switch attachment are scripted; **the pfSense
      reservation and the one-time switch creation are not**. Preflight refuses
      to run against an Internal/Private switch, and verification fails the run
      if the reserved address doesn't answer as the node just built — so a
      wrong reservation surfaces immediately instead of during Phase 2*
- [x] **Enable MAC address spoofing on each vNIC** — without it the CNI silently
      drops pod traffic. Classic Hyper-V failure, painful to diagnose after the fact
      — *`New-AerieVM.ps1`: `Set-VMNetworkAdapter -MacAddressSpoofing On`*
- [x] Second fixed-size VHDX per VM (or physical disk passthrough) on the TB
      storage, for Longhorn
      — *`New-AerieVM.ps1`: `New-VHD -Fixed`, sized by `-DataDiskSizeGB`.
      Passthrough is still manual if you'd rather give Longhorn a whole disk*
- [x] VM auto-start on host boot; **Automatic Stop Action = Shut Down, not Save
      State** — saved-state VMs resume with clock skew that confuses etcd and certs
      — *`New-AerieVM.ps1`: `Set-VM -AutomaticStartAction Start
      -AutomaticStopAction ShutDown`*
- [x] NTP from pfSense on all three
      — *cloud-init installs chrony pointed at `-NtpServer`, and Hyper-V's own
      Time Synchronization integration service is disabled so it can't fight
      it. `chronyc sources` is in the post-boot report*
- [x] **Stagger Windows Update reboots across the three hosts** — quorum of 3
      tolerates one node down; two at once freezes the cluster
      — *scripted: `.github/workflows/stagger-update-reboots.yml` takes a
      typed-in list of `hyperv-host-*` runners, spreads them evenly across
      the week, and `scripts/hyperv/Set-UpdateRebootSchedule.ps1` upserts
      each host's Windows Update AU registry policy. Rerun with an updated
      `hosts` input whenever the host topology changes.*
- [x] apply staggering actions to all cluster servers
- [x] undo temporary dynamic disk sizing in New-AerieVM.ps1

### Phase 2 — k3s + Flux + secrets

> Needs Phase 1's two available-host VMs actually built and reachable over
> SSH first — the third host is still running prod and doesn't get a node
> until Phase 7.

- [ ] `scripts/k3s/Install-K3sNode.ps1` — new script, same shape as
      `Initialize-AerieNode.ps1` (reuses `lib/AerieSsh.ps1` to connect, takes
      `-VMName`/`-IPAddress` plus `-ClusterInit` or `-JoinServer <node1-ip>`),
      so both nodes are one repeatable command each rather than hand-typed
      SSH sessions:
      - node 1: `curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION=<pin> sh -s - server --cluster-init --disable servicelb --token <token>`
      - node 2: same, with `--server https://<node1-ip>:6443` in place of `--cluster-init`
      - **pin `INSTALL_K3S_VERSION`** — don't track the latest/stable channel,
        for the same reproducibility reason as Goal 6.5's Renovate ask, which
        should cover this pin too once it exists
- [ ] Generate the shared cluster token before either install (`openssl rand
      -hex 32`); store it exactly like the SSH keys — never in git
- [ ] Confirm node-to-node ports are open before the second node joins: TCP
      6443 (apiserver), 2379-2380 (etcd), 10250 (kubelet), UDP 8472 (flannel
      VXLAN). Debian/Ubuntu cloud images ship with no firewall active, so
      this is a no-op today — worth a one-line check, not a real risk, unless
      that default changes
- [ ] `age-keygen` for the SOPS key. Commit the **public** key and a
      `.sops.yaml` creation rule (e.g. `deploy/**/secrets/*.yaml`) to git.
      Get the **private** key into the Phase 0 restic repos (extend
      `containers/backup/scripts/backup.sh` to pick it up) **and** print it
      for offline storage — the same two-step pattern as the Phase 0 restic
      password, and for the same reason: a key that only lives on the node it
      protects isn't a secret store
- [ ] `flux bootstrap github --owner=<owner> --repository=Aerie --branch=main
      --path=deploy/cluster --personal`, run once from an operator machine
      with `kubectl` pointed at node 1, using a scoped PAT (contents +
      workflows) — the last imperative step; everything Flux manages after
      this is a git commit to `deploy/`, no more manual `kubectl apply`
- [ ] Note the gap, don't solve it here: `kubectl`/Flux target node 1's IP
      directly — there's no VIP in front of the apiserver itself (kube-vip in
      Phase 3 fronts *ingress* traffic only). Losing node 1 means manually
      repointing the kubeconfig context at node 2 until Phase 7 restores a
      third node. Acceptable for a home cluster; call it out if that changes

> During the parallel build only the two new servers exist as nodes. Two-node
> etcd has *worse* availability than one, so treat the build window as
> non-production and rebuild the old prod box as the third server immediately
> after cutover (Phase 7).

### Phase 3 — Platform services

- [ ] Flux `HelmRelease`s: Longhorn, cert-manager, kube-vip, CNPG operator
- [ ] `ClusterIssuer` with Route53 DNS-01, reusing the existing AWS creds
- [ ] `HelmChartConfig` customizing k3s's bundled Traefik
- [ ] One **wildcard** `*.${DOMAIN}` certificate — a single DNS-01 challenge
      covering all six hostnames instead of six

### Phase 4 — Data tier

- [ ] CNPG `Cluster`: 3 instances, `minSyncReplicas: 1`, anti-affinity, `local-path`
- [ ] `quartz` database via the `Database` CRD
- [ ] Quartz DDL via a one-shot Job — mind the missing `IF NOT EXISTS` (Finding 3)
- [ ] WAL archiving + base backups to S3 → continuous PITR, a strictly better
      copy #3 for the most important data than nightly dumps
- [ ] Restore the Phase 0 backup into it and validate against real data

### Phase 5 — App tier

- [ ] First-party Helm chart: `api` (3 replicas) + `files` (2 replicas)
- [ ] Migration Job as a Helm hook (Finding 1)
- [ ] Ingress resources replacing the Caddy labels
- [ ] The `kiosk` host's `/` → `/apps/dashboard/` rewrite as a Traefik `Middleware`
- [ ] **Resource requests and limits on every workload**
- [ ] Flux image-update-automation watching GHCR and committing tag bumps —
      which removes `cd.yml` entirely rather than rewriting it

### Phase 6 — Observability

- [ ] fluent-bit rewrite (Finding 4), preserving the `State.Service` attribution
- [ ] `kube-prometheus-stack` with windows_exporter as additional targets (Finding 5)
- [ ] Provisioning Jobs (Finding 6)
- [ ] Longhorn PVCs for Grafana / Kuma / OpenSearch / Prometheus
- [ ] OpenSearch stays **single-node** — biggest RAM consumer, and observability
      was explicitly scoped out of HA

### Phase 7 — Cutover

- [ ] Point pfSense Unbound's `local-data` at the kube-vip VIP — a one-line
      change to the existing zone redirect
- [ ] Verify all six hostnames
- [ ] Rebuild the old prod box as the third k3s server and join it, restoring
      proper 3-node quorum

### Phase 8 — Backup v2 + rehearsal

- [ ] Migrate to cluster-native backup: CNPG/S3 for Postgres, Longhorn backup
      target → S3 for volumes, restic CronJob for the rest plus the local copy
- [ ] **Alert on backup age and backup-job failure** — the single most valuable
      alert that doesn't exist today
- [ ] Schedule a quarterly DR rehearsal onto throwaway VMs

### Phase 9 — Productization + docs

- [ ] `scripts/bootstrap-node.sh` and `scripts/restore.sh`
- [ ] Helm `values.yaml` holding domain / HA / seed data, so someone else can run
      Phases 1-2 and get a working stack
- [ ] `docs/cluster-architecture.md`, in the existing phase-doc style

---

## Goal 6 — beyond the six

Ranked by what actually bites:

1. **Secrets can't stay in GitHub Actions env — for the platform long-term.**
   Today a rotation requires a deploy, and DR requires manually re-entering
   everything. SOPS + age in git is what makes goal 5 and "release as a
   product" achievable at all. (Phase 0's restic secrets are a deliberate,
   scoped exception — see Phase 0 — this critique still fully applies from
   Phase 2 onward.) Also replace
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

---

## Verification

- **Phase 0 gate** — a real restore completed onto a scratch VM before any
  cluster work begins
- **Per phase** — `flux get all` clean; `kubectl get nodes` all Ready
- **HA proof (the real test)** — hard-power-off one node and confirm the VIP
  moves and the site stays up, CNPG promotes a replica, API pods reschedule, and
  Longhorn volumes rebuild. Do this for **each** of the three nodes, not just one
- **Data** — `cnpg status` shows 3 instances streaming, sync replica healthy
- **Backups** — restore the newest snapshot into a scratch Postgres and query it
- **App** — all six hostnames serve with a valid wildcard cert; kiosk tablets
  reconnect on their own after a node kill
- **Frontend** — build + lint per the usual convention; in-browser verification
  stays with the user

## File impact

**New:** `deploy/` (Flux tree), `charts/aerie/`, `scripts/`,
`docs/cluster-architecture.md`, `docs/disaster-recovery.md`

**Modified:** [Program.cs](src/Aerie.Api/Program.cs) (migrations → Job),
[appsettings.Docker.json](src/Aerie.Api/appsettings.Docker.json) (connection
strings → env), [containers/fluent-bit/](containers/fluent-bit/),
[containers/prometheus/prometheus.yml](containers/prometheus/prometheus.yml),
[containers/aerie-db/pginit.sql](containers/aerie-db/pginit.sql)

**Deleted:** [containers/caddy/](containers/caddy/),
[compose.prod.yml](compose.prod.yml),
[compose.observability.yml](compose.observability.yml),
[compose.metrics.yml](compose.metrics.yml),
[.github/workflows/cd.yml](.github/workflows/cd.yml), and the `aerie-caddy` job
in [publish.yml](.github/workflows/publish.yml)
