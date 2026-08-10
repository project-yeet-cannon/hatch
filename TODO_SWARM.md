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
repo. **That is rejected**, per [`docs/ethos.md`](docs/ethos.md): Aerie is meant
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
      backup you can't decrypt isn't one. *(No longer an exception — this **is**
      the permanent root of trust. GitHub Actions secrets match the pattern
      already used for the Route53/HA credentials — `RESTIC_PASSWORD` /
      `RESTIC_AWS_ACCESS_KEY_ID` / `RESTIC_AWS_SECRET_ACCESS_KEY`, kept
      separate from caddy's Route53 credentials via a dedicated `aerie-restic`
      IAM user, scoped to only the backup bucket. Phase 2 puts ESO downstream
      of these rather than replacing them; the printed copy is what the whole
      bootstrap chain hangs from.)*
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

> Provisioning is scripted in [`scripts/hyperv/`](scripts/hyperv/): dispatch
> the **Provision 0: New node VM** workflow at that host's runner — the
> primary path, and the auditable one. Running `Initialize-AerieNode.ps1` by
> hand in an elevated session on the host is the same code path, kept only as
> a fallback for when the runner isn't up yet.
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
>
> **`[x]` here means the script/workflow does it, not that the cluster's
> etcd has actually formed yet** — same convention as Phase 1.

- [x] `scripts/k3s/Install-K3sNode.ps1` — new script, same shape as
      `Initialize-AerieNode.ps1` (reuses `hyperv/lib/AerieSsh.ps1` to connect,
      takes `-VMName`/`-IPAddress` plus `-ClusterInit` or `-JoinServer
      <node1-ip>`), wrapped by the **Provision 1: Install k3s** workflow
      (`.github/workflows/provision-1-install-k3s.yml`) so both nodes are one
      dispatched run each rather than hand-typed SSH sessions:
      - node 1: `curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION=<pin> sh -s - server --cluster-init --disable servicelb --token <token>`
      - node 2: same, with `--server https://<node1-ip>:6443` in place of `--cluster-init`
      - **pin `INSTALL_K3S_VERSION`** — don't track the latest/stable channel,
        for the same reproducibility reason as Goal 6.5's Renovate ask, which
        should cover this pin too once it exists. The workflow reads it from
        the `K3S_VERSION` repository variable, so it's bumped in one place
- [x] Generate the shared cluster token before either install (`openssl rand
      -hex 32`); store it as the `K3S_CLUSTER_TOKEN` repository secret —
      exactly like the SSH keys, never in git
- [x] Confirm node-to-node ports are open before the second node joins: TCP
      6443 (apiserver), 2379-2380 (etcd), 10250 (kubelet), UDP 8472 (flannel
      VXLAN). Debian/Ubuntu cloud images ship with no firewall active, so
      this is a no-op today — worth a one-line check, not a real risk, unless
      that default changes
      — *`Install-K3sNode.ps1`'s join preflight now checks the four TCP
      ports against `-JoinServer` before installing. UDP 8472 is left
      unchecked on purpose: a TCP connect can't probe a connectionless port,
      and the no-firewall default above is what makes that an acceptable gap
      rather than a real one*
- [x] ~~`age-keygen` for the SOPS key~~ — **cut.** No secret bytes in git,
      encrypted or otherwise; see [Secrets](#secrets--no-bytes-in-git) and
      [`docs/ethos.md`](docs/ethos.md). Replaced by the three items below
- [x] Dedicated `aerie-eso` IAM user — `ssm:GetParameter*` /
      `ssm:GetParametersByPath` scoped to `/aerie/*` only, plus `kms:Decrypt`
      on the default `aws/ssm` key. Separate from `aerie-restic` and the
      Route53 user, same isolation discipline as Phase 0
- [x] Define and commit the parameter naming convention
      (`/aerie/<component>/<key>`, e.g. `/aerie/ha/token`,
      `/aerie/cert-manager/route53-secret-access-key`). This is the *pointer*
      half — structural, identical for every installation, and belongs in git
      — *[`scripts/secrets/parameters.json`](scripts/secrets/parameters.json)
      is the single source of truth (paths, which env var supplies each, which
      phase consumes it — no values, ever), read by the seeding script today
      and by the Phase 3 `ExternalSecret`s next. Convention, IAM split,
      rotation story and what deliberately stays **out** of the store are in
      [`docs/secrets-architecture.md`](docs/secrets-architecture.md). The
      prefix is a parameter (`-ParameterPrefix`), so two installations can
      share one AWS account*
- [x] **Provision 2: Seed secrets** workflow
      (`.github/workflows/provision-2-seed-secrets.yml`) — pushes the existing
      GitHub Actions secrets into the `/aerie/*` tree
      (`aws ssm put-parameter --type SecureString --overwrite`), then creates
      the single ESO bootstrap Secret on the cluster over SSH. Idempotent and
      re-runnable, and the automation-first counterpart to doing it by hand —
      same convention as Provision 0 and 1. This is the **one** imperative
      secret injection the design allows
      — *[`scripts/secrets/Sync-AerieSecrets.ps1`](scripts/secrets/Sync-AerieSecrets.ps1).
      Three refinements on the line above: it **reads before writing**, so an
      unchanged secret doesn't burn one of SSM's 100 versions per run;
      it **verifies as the `aerie-eso` identity**, listing *and* decrypting
      one value, since `GetParametersByPath` never touches KMS and would hide
      a missing `kms:Decrypt` until ESO failed on it in production; and no
      secret ever reaches a command line — values go to AWS via
      `--cli-input-json file://` and to the cluster over SSH **stdin**, which
      is why `Invoke-NodeSsh` grew `-StdIn` rather than using a heredoc*
- [x] ~~`flux bootstrap ...`, run once from an operator machine~~ — **scripted
      instead.** `Provision 3: Bootstrap Flux`
      ([`.github/workflows/provision-3-bootstrap-flux.yml`](.github/workflows/provision-3-bootstrap-flux.yml)
      → [`scripts/flux/Bootstrap-Flux.ps1`](scripts/flux/Bootstrap-Flux.ps1))
      runs `flux bootstrap github` *on the node* over SSH, so no cluster
      credential is copied onto a runner, and the PAT arrives on stdin rather
      than in argv. Leaving this one imperative would have made it the only
      provisioning step with no repeatable path — and it's the one a rebuilt
      control plane most needs to re-run. Owner/repo come from the run's
      context, so it can only ever be pointed at the repo it was dispatched
      from. Everything Flux manages after this is a git commit to `deploy/`
- [x] Note the gap, don't solve it here: `kubectl`/Flux target node 1's IP
      directly — there's no VIP in front of the apiserver itself (kube-vip in
      Phase 3 fronts *ingress* traffic only). Losing node 1 means manually
      repointing the kubeconfig context at node 2 until Phase 7 restores a
      third node. Acceptable for a home cluster; call it out if that changes
      — *written up in
      [`docs/secrets-architecture.md`](docs/secrets-architecture.md#known-gap-no-vip-in-front-of-the-apiserver),
      with the recovery (both workflows are idempotent — re-run against a
      survivor) and the two ways out if it ever costs more than that. Both
      provisioning scripts print the warning at the end of a successful run,
      so it can't quietly become a surprise*

> During the parallel build only the two new servers exist as nodes. Two-node
> etcd has *worse* availability than one, so treat the build window as
> non-production and rebuild the old prod box as the third server immediately
> after cutover (Phase 7).


### Phase 3 — Platform services

> Re-scoped. The first pass listed seven bullets that were really one sentence
> each; working them through against the repo turned up five things that would
> have stopped a run dead, and one dependency claim that was simply wrong.
>
> - **Operator values have no path into a Flux-reconciled manifest.** Phases 0-2
>   pass `${DOMAIN}` and friends as `vars.*` at deploy time, but Flux reconciles
>   from git, where [ethos](docs/ethos.md) forbids them. Nothing in the plan
>   bridged that. Flux's answer is `postBuild.substituteFrom` against an
>   in-cluster ConfigMap — which has to exist *before* the first commit under
>   `deploy/`, making it the true first step of the phase (3b.1).
> - **The Longhorn disk is raw.** Phase 1 attaches a fixed VHDX and explicitly
>   leaves it unformatted — "Longhorn claims it in Phase 3"
>   ([Initialize-AerieNode.ps1:494](scripts/hyperv/Initialize-AerieNode.ps1#L494)).
>   Longhorn's v1 engine wants a *filesystem path*, not a block device, and
>   defaults to `/var/lib/longhorn` on the root disk. Install it before the disk
>   is mounted there and it quietly fills the OS disk instead — 32 GB by
>   default, against a 200 GB data disk it never touches (3b.2).
> - **There is no LoadBalancer implementation in the cluster.** Phase 2 installs
>   k3s with `--disable servicelb` and the plan called kube-vip one bullet in a
>   list of four `HelmRelease`s. It is two components, not one, and until both
>   land Traefik's Service sits at `<pending>` and ingress has no address (3b.8).
> - **A wildcard cert that nothing serves.** Traefik falls back to its built-in
>   self-signed certificate for any route that doesn't name a Secret. Without a
>   `TLSStore`, the wildcard is issued, stored, and never presented (3b.10d).
> - **Split-horizon DNS breaks the DNS-01 self-check.** pfSense Unbound answers
>   for `${DOMAIN}` on the LAN, so cert-manager's propagation check — which
>   resolves through CoreDNS → the node's resolver → pfSense — never sees the
>   public `_acme-challenge` TXT it just wrote to Route53. The challenge hangs
>   and reports what looks like an AWS failure (3b.7).
> - **The `dependsOn` reasoning was wrong.** "cert-manager and CNPG both need
>   AWS credentials that only ESO can supply" — neither *controller* needs a
>   credential. The `ClusterIssuer` does, and the Phase 4 CNPG `Cluster` does.
>   Serializing controller installs behind ESO buys nothing and hides the real
>   constraint, which is that ESO's **CRDs** must exist before any
>   `ExternalSecret` applies. A two-layer Kustomization split expresses that
>   correctly; a chain of `dependsOn` between releases does not — and a Ready
>   `HelmRelease` never did imply a synced Secret (3b.3).

#### Phase 3a — Manual prerequisites

*Six one-time steps. None of them are code, all of them block something below.
Do these first, in order, and the whole of 3b runs unattended.*

**1. Confirm Phase 2 actually landed.** Phase 2's `[x]`s mean the scripts exist,
not that a cluster does. On any node, as `aerie`:

```sh
sudo k3s kubectl get nodes -o wide                    # 2 nodes, both Ready
sudo k3s kubectl -n external-secrets get secret aerie-eso-bootstrap
sudo env KUBECONFIG=/etc/rancher/k3s/k3s.yaml flux check
```

and confirm `deploy/cluster/flux-system/` exists on `main` — Flux commits it
during bootstrap. Any of the four failing means re-running the matching
Provision workflow; all of them are idempotent, so a re-run is the fix rather
than a repair.

**2. Pick and reserve the ingress VIP.** This is the floating address kube-vip
answers ARP for, and the one pfSense will eventually point `*.${DOMAIN}` at in
Phase 7.

  1. pfSense → **Services → DHCP Server → LAN** — note the pool's start and end
     address.
  2. Choose an address on the node subnet that is **outside that pool** and is
     not one of the nodes' DHCP reservations from Phase 1.
  3. If the only convenient address is inside the pool, shrink the pool instead
     of taking it. kube-vip answers for the VIP unconditionally; a DHCP client
     later handed the same address is an intermittent outage that presents as a
     networking bug and costs a day.
  4. Do **not** create a reservation or static mapping for it. The VIP has no
     MAC of its own — it moves between nodes, which is the point.
  5. Verify it's genuinely free, from a LAN machine: `ping -c3 <vip>` gets no
     reply, and `arp -n <vip>` shows no entry.
  6. Record it. It becomes the `INGRESS_VIP` repository variable in step 5.

  Do not point any DNS at it yet — that's Phase 7, after the cluster serves.

**3. Read the node's network interface name.** kube-vip's ARP mode advertises on
a named interface.

```sh
ssh aerie@<node-ip> "ip -o -4 addr show scope global | awk '{print \$2, \$4}'"
```

Expect one line, e.g. `eth0` or `ens18`. It will be identical on every node —
they're built from one golden image — and if it isn't, stop and find out why
before continuing. Record it as `NODE_INTERFACE`.

**4. Collect the Route53 facts, and confirm the split-horizon problem is real.**

  1. AWS console → **Route53 → Hosted zones →** the zone for `${DOMAIN}` → copy
     the **Hosted zone ID** (`Z...`). Record it as `ROUTE53_HOSTED_ZONE_ID`.
  2. Confirm the zone is publicly delegated:
     `dig +short NS ${DOMAIN} @1.1.1.1` must return four `awsdns` nameservers.
     If it doesn't, DNS-01 cannot work — and neither can Caddy's certificates
     today, so this should already be true.
  3. Confirm the internal override, from a LAN machine:
     `dig +short A home.${DOMAIN}` returns an internal address while
     `dig +short A home.${DOMAIN} @1.1.1.1` returns nothing. That difference is
     expected and is exactly what 3b.7's resolver override exists to survive.

**5. Set the new repository variables.** Settings → Secrets and variables →
Actions → **Variables**. Provision 4 (3b.1) reads these and nothing else.

| Variable | Value | New? |
|---|---|---|
| `DOMAIN` | base domain | existing |
| `ACME_EMAIL` | Let's Encrypt account address | existing |
| `AWS_REGION` | region holding the SSM tree | existing |
| `INGRESS_VIP` | from step 2 | **new** |
| `NODE_INTERFACE` | from step 3 | **new** |
| `ROUTE53_HOSTED_ZONE_ID` | from step 4 | **new** |
| `LONGHORN_REPLICA_COUNT` | **`2`** — see 3b.11 | **new** |

**6. Look up and record the chart versions to pin.** Every `HelmRelease` below
pins an exact chart version, for the same reproducibility reason as
`K3S_VERSION`. Unlike everything else in 3a these are *structural* — identical
for every installation — so they are committed in the manifests, not entered as
variables. Collect them once so 3b is a straight line:

| Component | Chart repository |
|---|---|
| external-secrets | `https://charts.external-secrets.io` |
| cert-manager | `https://charts.jetstack.io` |
| longhorn | `https://charts.longhorn.io` |
| kube-vip / kube-vip-cloud-provider | `https://kube-vip.github.io/helm-charts` |
| cloudnative-pg | `https://cloudnative-pg.github.io/charts` |

> **Gate:** the seven variables in step 5 are all set, and step 2's address
> answers nothing on the LAN. 3b assumes both.

#### Phase 3b — Scriptable, in this order

*Each step's inputs come from the repo or from the ConfigMap planted in step 1 —
nothing below waits on a human except the one explicit stop in step 10.*

- [ ] **1. Provision 4: cluster configuration.**
      `.github/workflows/provision-4-cluster-config.yml` →
      `scripts/k3s/Set-ClusterConfig.ps1`. Renders ConfigMap
      `aerie-cluster-config` in `flux-system` from the 3a.5 variables and
      applies it over SSH stdin — same shape as Provision 2's bootstrap Secret,
      minus the secrecy, since every one of these is an operator *value*.
      Every Kustomization in `deploy/` then reaches them via
      `postBuild.substituteFrom`.
      **Runs before the first commit under `deploy/`**: a Kustomization whose
      substitution source is missing fails to reconcile rather than degrading
      gracefully. Re-runnable — changing a value and re-running is how the VIP
      or domain gets changed later, with no commit.
      *Exit:* `kubectl -n flux-system get cm aerie-cluster-config -o yaml` lists
      all seven keys.
- [ ] **2. Provision 5: node storage prep.**
      `.github/workflows/provision-5-node-storage.yml` →
      `scripts/k3s/Initialize-NodeStorage.ps1`, run once per node. Over SSH:
      - install `open-iscsi`, `nfs-common`, `cryptsetup`; `systemctl enable
        --now iscsid`. Longhorn attaches volumes over iSCSI to the *host*, so
        these are node packages, not container ones
      - confirm `multipath-tools` is absent (it is, on a Debian cloud image) or
        blacklist Longhorn's devices — multipathd claiming them is the classic
        "volume stuck in Attaching" failure
      - identify the data disk **by being the unpartitioned one of the expected
        size**, never by hardcoding `/dev/sdb`; refuse ambiguity and refuse a
        disk that already holds a filesystem unless `-Force`
      - `mkfs.ext4`, label it, and mount at `/var/lib/longhorn` from `/etc/fstab`
        **by UUID** — Hyper-V device ordering isn't stable across reboots, and a
        `/dev/sdb` fstab entry is a node that boots with Longhorn's data path
        pointing at the wrong disk
      **Before step 11**, unavoidably: Longhorn's default data path is on the
      root filesystem, so installing it first means silently filling the OS disk.
      *Exit:* `findmnt /var/lib/longhorn` on every node, with capacity matching
      `-DataDiskSizeGB`.
- [ ] **3. The Flux tree skeleton** — commit only, no cluster access:
      - `deploy/cluster/flux-system/` — Flux's own, committed by the bootstrap
      - `deploy/cluster/infrastructure.yaml` — two Kustomizations,
        `infra-config` `dependsOn` `infra-controllers`
      - `deploy/cluster/infrastructure/controllers/` — one `HelmRepository` +
        `HelmRelease` per component (steps 4, 7, 8, 11, 12)
      - `deploy/cluster/infrastructure/config/` — the objects those controllers'
        CRDs define (steps 5, 6, 9, 10, and Longhorn's StorageClasses)

      Both Kustomizations carry `postBuild.substituteFrom` the step-1 ConfigMap.
      The layer boundary is what enforces ordering: CRDs and controllers
      converge first, then everything that needs them. This replaces the
      original plan's release-to-release `dependsOn` chain, for the reason in
      the preamble.
- [ ] **4. External Secrets Operator** — `controllers/external-secrets.yaml`,
      pinned, `installCRDs: true`. Commit the `external-secrets` Namespace too
      even though Provision 2 already created it: applying an existing namespace
      is a no-op, and a rebuilt cluster shouldn't depend on which of the two ran
      first.
      *Exit:* `kubectl get crd externalsecrets.external-secrets.io`.
- [ ] **5. `ClusterSecretStore`** — `config/cluster-secret-store.yaml`, **alone
      in its own file, nothing else beside it**. AWS provider, service
      `ParameterStore`, region `${AWS_REGION}`, authenticating by `secretRef` to
      `aerie-eso-bootstrap`. This is the single file that changes when the
      provider is swapped for in-cluster OpenBao before open-sourcing, which is
      the only reason it's isolated.
      *Exit:* the store reports `Ready=True`.
- [ ] **6. `ExternalSecret`s** — `config/external-secrets/`, one per entry in
      [`parameters.json`](scripts/secrets/parameters.json) that is
      **`required: true`**. That rule is the correction to the original list,
      which named the kiosk Wi-Fi password and CNPG's S3 WAL credentials: both
      are `required: false`, neither is seeded, and an `ExternalSecret` pointing
      at an absent parameter sits in `SecretSyncError` indefinitely and poisons
      the phase gate. They move to the phases that create their IAM users and
      consume them — 5 and 4.
      Phase 3 therefore creates: `cert-manager/route53-*` (consumed here),
      plus `ha/token` and `logging/vm-log-shipper-token` — not consumed until
      Phase 5, but created now as the end-to-end proof that the store works
      while there's still nothing depending on it.
      Worth a small generator (`scripts/secrets/New-ExternalSecrets.ps1`) plus a
      CI check that the tree matches `parameters.json`: the docs already promise
      both halves read one file, and hand-maintained duplication is how that
      stops being true.
- [ ] **7. cert-manager** — controller only; the issuer comes in step 10.
      Pinned, `crds.enabled: true`, and the two `extraArgs` that 3a.4 exists to
      justify: `--dns01-recursive-nameservers-only` and
      `--dns01-recursive-nameservers=1.1.1.1:53,8.8.8.8:53`.
      Without them the propagation self-check resolves through CoreDNS → the
      node's resolver → pfSense Unbound, which answers authoritatively for the
      *internal* view of `${DOMAIN}` and will never return the public TXT
      record cert-manager just wrote. The order then hangs until timeout and
      reports what reads like a Route53 permissions failure.
- [ ] **8. kube-vip** — two components, which the original single bullet hid:
      - **`kube-vip-cloud-provider`**, which assigns addresses to
        `Service type=LoadBalancer`. Phase 2's `--disable servicelb` means the
        cluster has no such implementation at all right now
      - **`kube-vip`** DaemonSet in **ARP** mode — `vip_interface:
        ${NODE_INTERFACE}`, `svc_enable: true`, hostNetwork, plus its RBAC
      - the pool: ConfigMap `kubevip` in `kube-system` with
        `range-global: ${INGRESS_VIP}-${INGRESS_VIP}`, a deliberate
        one-address pool
      Still **not** an apiserver VIP — the Phase 2 gap
      ([docs](docs/secrets-architecture.md#known-gap-no-vip-in-front-of-the-apiserver))
      is unchanged by this and stays open until Phase 7.
      *Exit:* `ping ${INGRESS_VIP}` answers from the LAN, and
      `ip addr show ${NODE_INTERFACE}` on the elected leader shows it.
- [ ] **9. Traefik `HelmChartConfig`** — `config/traefik-helmchartconfig.yaml`,
      `helm.cattle.io/v1`, named `traefik` in `kube-system`. k3s's bundled
      Traefik is a `HelmChart` CR owned by k3s's own helm-controller;
      `HelmChartConfig` merges values into it, which is what keeps Flux and k3s
      from fighting over one release. Sets the Service's requested
      `${INGRESS_VIP}`, `websecure` TLS, and `publishedService`.
      *Exit:* `kubectl -n kube-system get svc traefik` shows
      `EXTERNAL-IP = ${INGRESS_VIP}`, and `curl -k https://${INGRESS_VIP}` from
      the LAN returns Traefik's 404 — the correct answer with no routes defined.
- [ ] **10. The wildcard certificate — staging, then prod.** The one step in 3b
      with a human in the middle, deliberately:
      1. Both `ClusterIssuer`s (`letsencrypt-staging`, `letsencrypt-prod`),
         identical but for the ACME server URL. Route53 DNS-01 solver,
         `hostedZoneID: ${ROUTE53_HOSTED_ZONE_ID}`, `email: ${ACME_EMAIL}`,
         both halves of the credential by `secretRef` to step 6's Secret —
         the access key ID isn't sensitive, but splitting one credential across
         two delivery mechanisms is how drift starts.
      2. `Certificate` `aerie-wildcard` in `kube-system`, `secretName:
         aerie-wildcard-tls`, `dnsNames: ["*.${DOMAIN}", "${DOMAIN}"]` —
         **the apex is listed explicitly because a wildcard does not match it**.
      3. **Stop here and read `kubectl describe certificate aerie-wildcard`.**
         A stuck `DNS01` challenge is either 3a.4's split-horizon problem or a
         hosted-zone-ID / IAM mismatch. Diagnose it against staging, which has
         no meaningful rate limit. Then flip `issuerRef` to `letsencrypt-prod`
         and delete the staging Secret so a fresh order runs. Let's Encrypt
         allows five duplicate certificates per week; debugging DNS-01 against
         prod burns that in an afternoon and then you wait for it.
      4. Traefik `TLSStore` named `default` in `kube-system`, with
         `defaultCertificate.secretName: aerie-wildcard-tls`. **Missing from the
         original plan, and without it the certificate is issued and never
         served** — Traefik presents its built-in self-signed cert to any route
         that doesn't name a Secret, so every Phase 5 Ingress would need its own
         `tls:` block. One `TLSStore` covers all seven hostnames and everything
         added later, which is the entire argument for a wildcard.
      *Exit:* `openssl s_client -connect ${INGRESS_VIP}:443 -servername
      home.${DOMAIN} </dev/null | openssl x509 -noout -issuer -ext
      subjectAltName` shows a Let's Encrypt production issuer and `*.${DOMAIN}`.
- [ ] **11. Longhorn** — `defaultDataPath: /var/lib/longhorn` matching step 2,
      and two settings that are easy to get wrong:
      - `defaultReplicaCount: ${LONGHORN_REPLICA_COUNT}` — **2 during the build
        window.** Only two nodes exist until Phase 7; a three-replica volume on
        a two-node cluster is permanently Degraded, and its alarms are noise
        that trains you to ignore the real ones. Phase 7 raises it
      - `persistence.defaultClass: false` — k3s already ships `local-path` as
        the default StorageClass, and the storage split deliberately puts
        Postgres on it. Two default StorageClasses make any PVC that omits a
        class undefined
      Then explicit `longhorn-r3` / `longhorn-r2` StorageClasses in `config/`,
      the per-volume counts the [storage split](#storage-split) calls for, so
      Phase 6 chooses per volume rather than inheriting a global default.
      Defining `longhorn-r3` now is not a contradiction of the paragraph above:
      nothing binds to it until Phase 6, and anything that does will read
      Degraded until Phase 7 joins the third node. Expected, and the reason
      Phase 6's critical volumes are the ones worth deferring if that noise
      matters more than the ordering.
      *Exit:* `kubectl -n longhorn-system get nodes.longhorn.io -o wide` shows
      each disk schedulable at the data disk's capacity — which is what actually
      proves step 2 worked, more than `df` does.
- [ ] **12. CloudNativePG operator** — pinned `HelmRelease`, no credentials, no
      configuration. Last because nothing else waits on it and Phase 4 is what
      makes it do anything.
      *Exit:* `kubectl get crd clusters.postgresql.cnpg.io`.
- [ ] **13. Phase gate as a command** — `scripts/k3s/Test-ClusterPlatform.ps1`,
      asserting every *Exit* above in one run, in the same verification-stage
      shape as the other scripts. "Phase 3 is done" should be something that
      exits 0, not something remembered — and it doubles as the smoke test after
      a node rebuild.
      Run the [portability check](#verification) over `deploy/` before ticking:
      grep the new tree for the base domain, any LAN address, and the VIP. Every
      one of them should appear as a `${...}` substitution and nowhere else.

### Phase 4 — Data tier

- [ ] CNPG `Cluster`: 3 instances, `minSyncReplicas: 1`, anti-affinity, `local-path`
      — note the same two-node caveat as Longhorn (Phase 3b.11): three instances
      with anti-affinity across two nodes leaves one permanently Pending until
      Phase 7 joins the third. Start at 2 and scale, or accept the Pending pod
- [ ] The CNPG WAL `ExternalSecret`, deferred here from Phase 3 — its IAM user
      doesn't exist yet, which is why `postgres/wal-s3-*` is `required: false`
      in [`parameters.json`](scripts/secrets/parameters.json). Create the user,
      seed via Provision 2, flip the entries to required, then add the manifest
- [ ] `quartz` database via the `Database` CRD
- [ ] Quartz DDL via a one-shot Job — mind the missing `IF NOT EXISTS` (Finding 3)
- [ ] WAL archiving + base backups to S3 → continuous PITR, a strictly better
      copy #3 for the most important data than nightly dumps
- [ ] Restore the Phase 0 backup into it and validate against real data

### Phase 5 — App tier

- [ ] First-party Helm chart: `api` (3 replicas) + `files` (2 replicas)
- [ ] Migration Job as a Helm hook (Finding 1)
- [ ] Ingress resources replacing the Caddy labels — no per-Ingress `tls:`
      block needed, the Phase 3b.10d `TLSStore` serves the wildcard to all of them
- [ ] The `kiosk` host's `/` → `/apps/dashboard/` rewrite as a Traefik `Middleware`
- [ ] The kiosk Wi-Fi `ExternalSecret`, deferred here from Phase 3 — it stays
      `required: false` until the value moves out of `SiteSettings`, and an
      `ExternalSecret` for an unseeded parameter never reaches `SecretSynced`
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

- [ ] Point pfSense Unbound's `local-data` at the kube-vip VIP (`INGRESS_VIP`,
      set in Phase 3a.2) — a one-line change to the existing zone redirect
- [ ] Verify all seven hostnames — `home`, `kiosk`, `files`, `share`, `status`,
      `logs`, `metrics`. The plan said six before `share` existed; the wildcard
      makes the count irrelevant to the certificate but not to this check
- [ ] Rebuild the old prod box as the third k3s server and join it, restoring
      proper 3-node quorum
- [ ] **Raise the replica counts the two-node build window forced down**: the
      `LONGHORN_REPLICA_COUNT` variable to `3` and re-run Provision 4 (no commit
      — that's the point of the ConfigMap), the per-volume Phase 6 classes to
      the [storage split](#storage-split)'s 3-and-2, and CNPG to 3 instances.
      Confirm Longhorn actually rebuilds onto the new node rather than reporting
      Degraded, which is the first real proof the third node is carrying load

### Phase 8 — Backup v2 + rehearsal

- [ ] Migrate to cluster-native backup: CNPG/S3 for Postgres, Longhorn backup
      target → S3 for volumes, restic CronJob for the rest plus the local copy
- [ ] **Alert on backup age and backup-job failure** — the single most valuable
      alert that doesn't exist today
- [ ] Export the `/aerie/*` parameter tree into the restic repos on the same
      schedule. The secret store is now off-site, but "AWS account is gone" is
      the one failure mode ESO introduces, and `RESTIC_PASSWORD` is already
      printed offline — that's what closes the loop
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
7. **Make the ethos mechanical.** [ci.yml](.github/workflows/ci.yml) has no
   secret scanning today. Add `gitleaks` plus a path-deny check that fails on
   `*.agekey`, `*.pem`, `id_*`, `.sops.yaml`, `*.enc.yaml` — a rule enforced only
   by memory is a rule that lapses. See [`docs/ethos.md`](docs/ethos.md).

---

## Verification

- **Phase 0 gate** — a real restore completed onto a scratch VM before any
  cluster work begins
- **Per phase** — `flux get all` clean; `kubectl get nodes` all Ready
- **Phase 3 gate** — `scripts/k3s/Test-ClusterPlatform.ps1` exits 0 (Phase 3b.13)
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
  ([`docs/ethos.md`](docs/ethos.md))

## File impact

**New:** `deploy/` (Flux tree), `charts/aerie/`, `scripts/`,
`docs/cluster-architecture.md`, `docs/disaster-recovery.md`,
[`docs/ethos.md`](docs/ethos.md),
[`docs/secrets-architecture.md`](docs/secrets-architecture.md),
[`scripts/secrets/`](scripts/secrets/), [`scripts/flux/`](scripts/flux/),
[`.github/workflows/provision-2-seed-secrets.yml`](.github/workflows/provision-2-seed-secrets.yml),
[`.github/workflows/provision-3-bootstrap-flux.yml`](.github/workflows/provision-3-bootstrap-flux.yml)

*Phase 3 adds:* `scripts/k3s/Set-ClusterConfig.ps1`,
`scripts/k3s/Initialize-NodeStorage.ps1`,
`scripts/k3s/Test-ClusterPlatform.ps1`,
`scripts/secrets/New-ExternalSecrets.ps1`,
`.github/workflows/provision-4-cluster-config.yml`,
`.github/workflows/provision-5-node-storage.yml`, and the
`deploy/cluster/infrastructure/` tree.

**Modified:** [Program.cs](src/Aerie.Api/Program.cs) (migrations → Job),
[appsettings.Docker.json](src/Aerie.Api/appsettings.Docker.json) (connection
strings → env), [ci.yml](.github/workflows/ci.yml) (secret scanning),
[containers/fluent-bit/](containers/fluent-bit/),
[containers/prometheus/prometheus.yml](containers/prometheus/prometheus.yml),
[containers/aerie-db/pginit.sql](containers/aerie-db/pginit.sql)

**Deleted:** [containers/caddy/](containers/caddy/),
[compose.prod.yml](compose.prod.yml),
[compose.observability.yml](compose.observability.yml),
[compose.metrics.yml](compose.metrics.yml),
[.github/workflows/cd.yml](.github/workflows/cd.yml), and the `aerie-caddy` job
in [publish.yml](.github/workflows/publish.yml)
