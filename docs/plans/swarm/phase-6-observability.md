[← Phase 5](phase-5-app-tier.md) · [Design & decisions](design.md) · [Phase 7 →](phase-7-cutover.md)

---

# Phase 6 — Observability

**Status: Not started**

> Re-scoped, the same way Phases 3, 4 and 5 were. The six bullets this file used
> to hold were one sentence each; working them through against the repo turned
> up five things that stop the phase dead if they are discovered mid-flight,
> and one that would have been discovered by a disaster recovery rehearsal
> instead — which is the worst possible time.
>
> - **Turning on a chart's built-in `ServiceMonitor`/`PodMonitor` flag
>   deadlocks a cold rebuild.** The obvious way to scrape CloudNativePG is
>   `monitoring.podMonitorEnabled: true` on
>   [`cloudnative-pg.yaml`](../../../deploy/cluster/infrastructure/controllers/cloudnative-pg.yaml),
>   which 3b.12 deliberately left `false` and left a note pointing here. But
>   layer 1 reconciles **before** anything this phase installs, so on an empty
>   cluster that flag renders a `monitoring.coreos.com/v1` object whose CRD does
>   not exist yet — a failed Helm install, which under `wait: true` holds every
>   other controller and `infra-config` behind it, which holds this phase's own
>   layer behind *that*. The cluster that has been running for a month never
>   notices; the rebuild in Phase 9's DR rehearsal never comes up. **Every
>   scrape object in this phase is a hand-written object in this phase's own
>   layer**, and no layer-1 chart flag that renders one is ever flipped (6b.6).
> - **k3s does not expose etcd's metrics, and three of the stack's default
>   control-plane scrapes can never come up.**
>   [`Install-K3sNode.ps1:267-270`](../../../scripts/k3s/Install-K3sNode.ps1#L267-L270)
>   runs `server --cluster-init --disable servicelb --token …` and nothing else,
>   so `--etcd-expose-metrics` is off (k3s defaults it false) and etcd's metrics
>   port is not listening. kube-prometheus-stack meanwhile ships `ServiceMonitor`s
>   for `kubeEtcd`, `kubeScheduler`, `kubeControllerManager` and `kubeProxy`,
>   all four of which bind to loopback on k3s. Installed as-is that is four
>   permanently-down targets and their `TargetDown` alerts on day one — the
>   alarm noise [3b.11](phase-3-platform-services.md) already named as the thing
>   that trains an operator to ignore real alerts. This is a **node** change and
>   it has to happen before the chart, not because of it (6b.1).
> - **`compose.observability.yml` commits a credential that reads as a secret.**
>   `AUTOKUMA__KUMA__PASSWORD` and `KUMA_PASSWORD` are the literal
>   `aerie-kuma-admin!23`, in git, three times
>   ([compose.observability.yml:41](../../../compose.observability.yml#L41),
>   [:62](../../../compose.observability.yml#L62)). What
>   [`docs/ethos.md`](../../ethos.md) rules out is shipping *one installation's*
>   password to every other operator: a string that looks bespoke, that nobody is
>   prompted to change, and that therefore silently becomes shared. The fix taken
>   here is not to route it through ESO — that buys secrecy at the price of two
>   more required repository secrets on an account nobody has logged into yet.
>   Both admin accounts instead ship a **known default**, `admin` / `password`,
>   committed as a plain Secret and changed by each operator from the UI when they
>   choose (6a.4, 6b.4). Grafana's unset admin password (`admin`/`admin`, on a host
>   that never published its port and now serves at `metrics.${DOMAIN}`) becomes
>   the same shape rather than a second special case.
> - **fluent-bit is about to ship ten times the log volume into a single-node
>   OpenSearch.** Today it tails one compose project. Under k3s
>   `/var/log/containers/*.log` is every pod on the node — k3s system pods,
>   Longhorn's six components, CNPG, Flux, the observability stack tailing
>   itself. That is the *point* (Longhorn and CNPG logs at 3am are exactly what
>   the old setup could not give you), and it is also an unbudgeted multiple on
>   a `-Xmx512m` heap and a 30-day retention written for one host. The rewrite
>   is not just a parser change; it comes with a retention decision and a
>   measurement (6b.10).
> - **OpenSearch will not start on these nodes.** `vm.max_map_count` defaults to
>   65530 on Linux and OpenSearch's bootstrap check demands 262144. Under Docker
>   Desktop the WSL2 VM happened to satisfy it, which is why this has never been
>   seen. On the Hyper-V Linux VMs it is a node-level kernel setting nothing in
>   [`Install-K3sNode.ps1`](../../../scripts/k3s/Install-K3sNode.ps1) or
>   [`Initialize-NodeStorage.ps1`](../../../scripts/k3s/Initialize-NodeStorage.ps1)
>   sets, and the pod's failure mode is a crash loop with the reason buried in
>   the container log (6b.1, alongside the k3s flags — the same file, the same
>   dispatch).
> - **`HA_HOST` and `HA_PORT` are `required: false` and this phase makes them
>   load-bearing.** Their
>   [`cluster-config.json`](../../../scripts/k3s/cluster-config.json) description
>   says "only read on a fresh install, by the one-time SiteSettings import".
>   That stops being true here: `kuma-provision` needs them to create the Home
>   Assistant notification, which is the *only* path by which anything in this
>   stack reaches a human. Unset, the phase installs a monitoring system whose
>   alerting is silently off — the single worst outcome available. They become
>   required (6b.3).
>
> Three decisions, taken deliberately and recorded here so they are not
> relitigated mid-phase. **Uptime Kuma and AutoKuma are plain manifests, not a
> community chart** — Kuma is one container, one volume and one Service, and a
> third-party chart for that is supply chain surface bought for nothing, in a
> tree whose stated rule is that `deploy/` holds objects. **Alertmanager stays,
> and Aerie now has two notification paths** — Kuma for "is it answering",
> Alertmanager for "is a PromQL expression true" — which is one more than is
> comfortable and is still the right answer, because no Kuma monitor can
> evaluate `gotk_reconcile_condition`. 6b.8 buys most of the cost back by
> routing Alertmanager's dead-man's-switch *through* Kuma, so the second path
> proves itself over the first. **Observability is not HA and this phase makes
> that mechanical**: a PriorityClass below every workload in Phase 5, so a
> single surviving node sheds Grafana rather than leaving an API replica Pending
> (6b.14). [design.md](design.md) says reschedule-on-failure is acceptable here;
> nothing in the cluster knows that until something writes it down.

---

## Sizing, from the old host

[5a.6](phase-5-app-tier.md) sampled the compose stack before Phase 7 removes it,
so these are the last real numbers this stack will produce. Requests and limits
are required here for the same reason they are in 5b.11 —
[goal 2 does not work without them](design.md#how-goal-2-actually-works).

| Workload | observed | `requests.memory` | `limits.memory` |
|---|---|---|---|
| `opensearch` | 1.48 GiB | `1.5Gi` | `2Gi` |
| `opensearch-dashboards` | 310 MiB | `384Mi` | `768Mi` |
| `grafana` | 226 MiB | `256Mi` | `512Mi` |
| `prometheus` (kube-prometheus-stack) | 141 MiB | `512Mi` | `1.5Gi` |
| `uptime-kuma` | 151 MiB | `192Mi` | `384Mi` |
| `autokuma` | 32 MiB | `64Mi` | `128Mi` |
| `fluent-bit` (DaemonSet) | 7 MiB | `64Mi` | `128Mi` |
| `node-exporter` (DaemonSet) | 3 MiB | `32Mi` | `64Mi` |

CPU requests in the 10m–50m range and **no CPU limits**, per 5a.6 — the whole
observability stack peaked at 5% of one core.

Two rows are deliberately not the observed number:

- **`prometheus`.** 141 MiB is a compose project's worth of series.
  kube-prometheus-stack additionally scrapes kube-state-metrics, kubelet
  cAdvisor, Longhorn and Flux, at several times the cardinality. Start at
  `512Mi`/`1.5Gi` and re-measure once it has a week of retention behind it;
  this is the one workload here whose request should be revisited rather than
  set once.
- **`opensearch`.** 1.48 GiB resident against `-Xms512m -Xmx512m`
  ([compose.observability.yml](../../../compose.observability.yml#L77)) is heap plus
  mmap'd Lucene segments plus JVM overhead. Size the container from the
  resident figure, not the heap — and if the heap is raised, the container
  needs roughly the increase again on top, not just the increase.

### Five workloads that were never on the old host

Nothing above covers what kube-prometheus-stack brings that compose never ran.
These are estimates from the charts' own defaults, flagged as such, and 6b.14 is
where they are checked against reality:

| Workload | `requests.memory` | `limits.memory` | Note |
|---|---|---|---|
| `prometheus-operator` | `64Mi` | `192Mi` | One reconciler, no series |
| `kube-state-metrics` | `96Mi` | `256Mi` | Scales with object count, not node count |
| `alertmanager` | `64Mi` | `128Mi` | Plus a 1Gi PVC for silences (6b.8) |
| `grafana` dashboard sidecar | `64Mi` | `128Mi` | Watches ConfigMaps cluster-wide |
| provisioning CronJobs | `64Mi` | `128Mi` | Transient; two of them, minutes apart |

**The number that matters is the single-node one.** Summing the single-instance
rows — OpenSearch, Dashboards, Grafana and its sidecar, Prometheus, Alertmanager,
Kuma, AutoKuma, kube-state-metrics, the operator — lands near **3.2 GiB of
memory requests that all land on one node**, because none of it is replicated
and nothing spreads it. Add the per-node DaemonSets (fluent-bit + node-exporter,
~96Mi each) on all three.

5b.11 already counted Phase 6 into its "9–10 GiB on one node, with little to
spare" figure, so this is not new budget — it is the phase that has to land
inside a number someone else already spent. If it does not, 6b.14's
PriorityClass is what decides *which* pods lose, and the answer has to be these
ones.

---

## Phase 6a — Manual prerequisites

*Six one-time steps, none of them code. Do these first, in order, and the whole
of 6b runs from commits and workflow dispatches.*

**[x] 1. Confirm Phase 5 actually landed.** Dispatch *Verify: App tier*
([`verify-app-tier.yml`](../../../.github/workflows/verify-app-tier.yml)) and get a
green run. Two things in that output this phase builds directly on:

- every container in `aerie` reports non-empty `resources.requests` (5b.11's
  exit). 6b.14 extends the same assertion over this phase's namespace, and an
  assertion that was already failing is not one to extend.
- `ghcr-pull` is `SecretSynced` in **both** `aerie` and `flux-system`. 6b.2 adds
  a third namespace to the same parameter pair, and the multi-namespace support
  it relies on is 5b.1's.

**[x] 2. Collect the three Windows hosts' LAN addresses, and prove
windows_exporter answers from a pod.** The exporter is installed by
[`provision-0-new-node.yml:161-182`](../../../.github/workflows/provision-0-new-node.yml#L161-L182),
not by anything in this phase — what this phase does is scrape it from *inside*
the cluster instead of from a container on the same host, which is a different
network path with a different firewall answer.

For each host, from any node:

```sh
curl -s --max-time 5 http://<host-lan-ip>:9182/metrics | head -1
```

All three must answer. **A timeout — not a refusal — is Windows Defender
Firewall**, and it was the first thing this step found: the MSI opens no port.
windows_exporter's firewall exception is an opt-in installer feature
(`ADDLOCAL=FirewallException`) the install never passed, so inbound 9182 was
being dropped silently on all three hosts. It went unseen for as long as it did
because the compose stack scraped `host.docker.internal:9182` — a path that
never left the machine. The rule is now converged by
[`provision-0-new-node.yml`](../../../.github/workflows/provision-0-new-node.yml)
alongside the install itself, scoped to `LocalSubnet`; a host provisioned before
that step existed picks it up on the next dispatch. Finding this in 6b.6 instead
would have meant diagnosing it as a Prometheus fault.

**Make the three addresses reserved before you record them**, if they are not
already — this is the step that turns them into fixed configuration. `staticConfigs`
is not a discovered set: nothing re-finds a host that moved, so a lease change
lands as a firing `up == 0` in 6b.8 and costs a Provision 4 re-dispatch to clear,
having first been read as a dead host. The VMs have had reservations since Phase
1 and the hosts under them have not needed any, because nothing before this phase
referred to a host by address.

Reserve them on pfSense rather than setting static addresses in Windows, and key
each reservation to the **`vEthernet (ExternalSwitch)` adapter's** MAC, not the
physical NIC's — `AllowManagementOS` moves the host's own traffic onto that
adapter, and it carries a MAC of its own.
[Phase 1](phase-1-node-substrate.md) holds the full note, including why these
reservations are slightly less durable than the VMs'.

Record the three as one value, in Prometheus's own list syntax, because that is
what 6b.3's ConfigMap key holds verbatim:

```text
["10.0.0.11:9182","10.0.0.12:9182","10.0.0.13:9182"]
```

**[x] 3. Create the Home Assistant webhook that alerts land on.** Alertmanager
needs somewhere to send. Kuma's existing Home Assistant notification is a Kuma
feature, not a reusable endpoint, so this is a second, separate integration —
see the re-scope preamble for why that cost is accepted.

In Home Assistant, create an automation with a **webhook** trigger, note its
webhook id, and have it fire whatever notification you already trust (the same
one Kuma uses is the right answer — one destination, two producers). Alertmanager
posts a JSON body with `status`, `alerts[]`, `commonLabels` and `commonAnnotations`;
`{{ trigger.json.commonAnnotations.summary }}` is the field worth putting in the
message.

Add `HA_ALERT_WEBHOOK_ID` as a repository **variable**. It is an opaque
per-installation string that grants the ability to fire one automation — a
capability, not an identity — but it is also useless without LAN access to Home
Assistant, and it rides the same ConfigMap as `HA_HOST` and `HA_PORT` rather
than becoming the one operator value that needs a different mechanism. If that
trade is unwanted, say so before 6b.3 and it becomes an SSM parameter instead;
the change is one entry moving between two files.

**[x] 4. Nothing to generate — know the two default logins.** Kuma and Grafana
both come up as **`admin` / `password`**, from plain Secret manifests committed
in 6b.4. There are no repository secrets to add, no SSM parameters behind them,
and no generator run. Change either from its own UI whenever you get to it.

This is the deliberate answer to the re-scope preamble's third bullet, and it is
a different answer than "route it through ESO". Two new admin accounts on
systems nobody has logged into yet do not justify two more required repository
secrets at install time; a default that is *universally known and expected to be
changed* is the router-login model, not the shipped-shared-secret one the ethos
rule is aimed at. The security implication is accepted knowingly: **anyone who
can reach `status.${DOMAIN}` or `metrics.${DOMAIN}` before you change these can
log in as admin.** Both are LAN-only hostnames on the internal VIP; if either is
ever published beyond the LAN, changing both is a prerequisite of publishing,
not a follow-up.

**Grafana's is safe to change in the UI and forget.** The chart seeds the admin
user only when the database is first created, so a password changed later
persists across restarts and across Flux re-applying the committed Secret with
the old value in it. The Secret goes stale and nothing reads it.

**Kuma's is not** — this is the one thing to know before changing it. AutoKuma
(6b.12) and the `kuma-provision` CronJob (6b.13) both log in with that password
on every reconcile, reading it from the same committed Secret. Change it in
Kuma's UI alone and AutoKuma stops converging monitors while the CronJob starts
exiting non-zero — an authentication failure that presents as monitors quietly
not appearing. So for Kuma, changing the password is two edits: the UI, and the
Secret in git. Both are yours to do at leisure; doing only the first is the
failure mode worth naming here rather than discovering in 6b.12's logs.

**The old host keeps using `aerie-kuma-admin!23` until Phase 7 deletes the
compose files** — do not edit `compose.observability.yml` to match. Rotating a
live Kuma's admin password means its SQLite database disagrees with the compose
file, and there is no reason to touch a system that is being deleted.

**[x] 5. Read the sizing section above and accept the budget.** Specifically the
"3.2 GiB on one node" figure. This is the phase where the cluster stops fitting
comfortably, and 6b.14's PriorityClass is a decision about what to lose, not a
safety net that makes the number go away. If it is not acceptable, the lever
with the best ratio is OpenSearch's retention and heap — everything else here is
already at its floor.

**[x] 6. Know how you will test.** DNS still points at the old host until Phase
7, so this phase's three hostnames are reachable only by asking the VIP:

```sh
curl -sv --resolve status.${DOMAIN}:443:${INGRESS_VIP} https://status.${DOMAIN}/
```

Same as 5a.7, same warning: do not add hosts-file entries on machines other
people use.

---

## Phase 6b — Scriptable, in this order

Steps 1–4 are plumbing with no workload behind them: the node flags, the
secrets, the config keys, the empty layer. 5–8 are metrics, one concern at a
time, and only 5 makes anything reconcile. 9–11 are logs, 12–13 are the status
page. 14 is the property the design demands of a non-HA tier, and 15 is the
gate.

- [x] **1. Two node-level settings, and a k3s reconfigure** —
      [`Install-K3sNode.ps1`](../../../scripts/k3s/Install-K3sNode.ps1),
      [`provision-1-install-k3s.yml`](../../../.github/workflows/provision-1-install-k3s.yml).
      First because both are node state, both need k3s or the kernel restarted
      to take effect, and 6b.5 and 6b.9 each fail confusingly without one.

      **`vm.max_map_count=262144`.** OpenSearch's bootstrap check refuses to
      start below it and the Linux default is 65530. Write it as a
      `/etc/sysctl.d/` drop-in (persisted across reboots) and apply it live, in
      the same place `Initialize-NodeStorage.ps1` already does node preparation
      — **not** as the OpenSearch chart's `sysctlInit` initContainer, which is a
      privileged container mutating a kernel parameter on whichever node it
      lands on. This is a property of the node, so it belongs to the code that
      builds nodes; that also means a node joined later already has it, which
      the initContainer approach only achieves by scheduling there first.

      **`--etcd-expose-metrics`.** k3s defaults it false, so etcd's metrics
      endpoint on `:2381` is not listening and 6b.8's quorum alert has nothing
      to alert on. Deliver it as `/etc/rancher/k3s/config.yaml`:

      ```yaml
      etcd-expose-metrics: true
      ```

      rather than by re-running the installer with an extra argument. k3s merges
      that file with its command line at start, so this is a file write plus a
      `systemctl restart k3s`, one node at a time — against a reinstall, which
      is the heavier operation and the one that has an opinion about etcd
      membership. **One node at a time, waiting for `kubectl get nodes` to show
      Ready in between**: with two servers, quorum is 2 of 2 until Phase 7, so
      restarting both at once is an outage and restarting one is not.

      Note what is deliberately *not* done here: `kube-scheduler`,
      `kube-controller-manager` and `kube-proxy` also bind to loopback on k3s and
      could be exposed the same way. They are not, and 6b.5 turns their scrapes
      off instead. On k3s all three run inside the same `k3s server` process,
      whose liveness the node's own `Ready` condition already reports, so the
      three extra listeners buy alerts that are either redundant or firing at the
      same instant as `KubeNodeNotReady`. etcd is the exception because its
      failure mode — quorum lost while every process is still running — is
      genuinely invisible from anywhere else.
      *Exit:* on every node, `sysctl vm.max_map_count` reads 262144 and
      `curl -s http://127.0.0.1:2381/metrics | head -1` answers. `kubectl get
      nodes` shows every node Ready and `kubectl -n kube-system get pods` shows
      nothing restarted that should not have.

- [x] **2. Two secrets changes, through the machinery 5b.1 built** —
      [`parameters.json`](../../../scripts/secrets/parameters.json),
      [`New-ExternalSecrets.ps1`](../../../scripts/secrets/New-ExternalSecrets.ps1),
      [`provision-2-seed-secrets.yml`](../../../.github/workflows/provision-2-seed-secrets.yml).
      **No new parameters** — the two admin passwords 6a.4 would have added are
      committed defaults in 6b.4 instead, and this step is only existing
      parameters gaining a namespace. No generator changes either: 5b.1 added
      everything needed, and this step is the first evidence that the
      multi-namespace support was worth building rather than worked around.

      Existing, each gaining one more `kubernetes` block:

      - **`ha/token`** — a second block targeting `observability`, secretName
        `home-assistant`, key `token`. 6b.13's `kuma-provision` CronJob reads it
        to create the notification provider, and 6b.8's Alertmanager reads it if
        the webhook route is authenticated. Its `consumedBy` currently says
        "Phase 5 Aerie.Api … as the end-to-end proof that the store works" —
        that sentence gets a second half.
      - **`registry/pull-username` / `registry/pull-token`** — a third block
        each, targeting `observability`, secretName `ghcr-pull`,
        `dockerconfigjson: ghcr.io`. 6b.13's provisioner is a first-party image
        in a private repository, so the namespace needs its own pull secret; a
        Secret is namespaced and there is no cross-namespace `imagePullSecrets`.

      The four-part `kubernetesDeferred` ordering 4b.3 established and 5b.1
      repeated does **not** apply here, and knowing why is the point: it exists
      because the generator refuses a `kubernetes` block on an unseeded
      parameter. All three of these were seeded in earlier phases, so a new block
      on each is a `parameters.json` edit plus
      `pwsh ./scripts/secrets/New-ExternalSecrets.ps1`, with no Provision 2
      re-dispatch and no intermediate commit. Dropping the two new parameters is
      what removed the only reason this phase needed the dance.

      One ordering wrinkle this phase introduces that the earlier ones did not:
      **the `observability` namespace does not exist yet**, and an
      `ExternalSecret` in a namespace that does not exist is an apply failure
      for the whole `infra-config` Kustomization these render into. 6b.4's
      first pass got this wrong by creating the namespace in its own,
      downstream layer instead - a deadlock, corrected there by putting it in
      [`namespaces.yaml`](../../../deploy/cluster/infrastructure/config/namespaces.yaml)
      alongside `aerie`, in `infra-config` itself, the same fix 3b.6 already
      used for the identical shape one phase earlier. That correction is what
      actually resolves this step's ordering wrinkle: the manifests here and
      the Namespace live in the same Kustomization, applied in one pass, no
      cross-Kustomization dependency involved. They are written in this step
      because this is where the parameters are decided; the Namespace itself
      is 6b.4's business, in `namespaces.yaml`, not in `observability/`.
      *Exit:* `New-ExternalSecrets.ps1 -Check` exits 0, and once the Namespace
      exists,
      `kubectl -n observability get externalsecret` reports `SecretSynced` for
      `home-assistant` and `ghcr-pull`, with `ghcr-pull` typed
      `kubernetes.io/dockerconfigjson`. `kuma-admin` and `grafana-admin` are in
      the same namespace but are **not** ExternalSecrets and will not appear in
      that list — `kubectl -n observability get secret` is where they show.

- [x] **3. Two new `cluster-config.json` keys, two promoted to required, and a
      Provision 4 re-dispatch** —
      [`cluster-config.json`](../../../scripts/k3s/cluster-config.json),
      [`provision-4-cluster-config.yml`](../../../.github/workflows/provision-4-cluster-config.yml).

      | Key | Required | Pattern notes | `consumedBy` |
      |---|---|---|---|
      | `WINDOWS_EXPORTER_TARGETS` | yes | A YAML/JSON flow sequence of `"host:port"` strings, brackets included — `["10.0.0.11:9182","10.0.0.12:9182"]` | 6b.6 ScrapeConfig |
      | `HA_ALERT_WEBHOOK_ID` | yes | A webhook id: url-safe, no slashes, no leading `/api/webhook/` | 6b.8 Alertmanager receiver |

      `WINDOWS_EXPORTER_TARGETS` holds punctuation, which is unusual for this
      file and is the point. Flux's `postBuild` substitution is textual
      envsubst-style expansion, so a scalar containing `["a","b"]` dropped into
      a `targets: ${WINDOWS_EXPORTER_TARGETS}` position produces a valid YAML
      flow sequence and Prometheus sees a list. The alternative — three keys
      named `_0`, `_1`, `_2` — is easier to validate and hard-codes a host count
      into the schema, which is [goal 1](design.md#goals) written backwards. Pay
      for the flexibility with a strict `pattern`: opening bracket, at least one
      double-quoted `host:port`, comma-separated, closing bracket, nothing else.
      A value that is *almost* right here becomes a Prometheus config-reload
      failure two steps away.

      **`HA_HOST` and `HA_PORT` become `required: true`.** See the re-scope
      preamble. Their `description` fields currently say the values are only
      read on a fresh install; rewrite both to name 6b.13 as the consumer that
      needs them on every install, forever. This is the change most likely to
      surprise a second operator, because it turns a previously-optional
      variable into a preflight failure — which is exactly why it is a
      `cluster-config.json` edit and not a note in this file.

      Then map all four in `provision-4-cluster-config.yml` and **re-dispatch
      Provision 4**. No commit is needed for the values, which is the whole
      reason this is a ConfigMap.
      *Exit:* `kubectl -n flux-system get configmap aerie-cluster-config -o
      jsonpath='{.data.WINDOWS_EXPORTER_TARGETS}'` prints the bracketed list
      intact, brackets and quotes included.

- [x] **4. The observability layer, empty but for two default logins** —
      `deploy/cluster/observability.yaml`, `deploy/cluster/observability/`, and one
      line in [`kustomization.yaml`](../../../deploy/cluster/kustomization.yaml). The
      commit that creates somewhere for the next ten steps to land, plus the only
      workload credential in the phase that is not an ExternalSecret.

      Two Flux Kustomizations, in the shape
      [`data.yaml`](../../../deploy/cluster/data.yaml) established:

      - **`observability-controllers`** — `dependsOn: infra-config`,
        `path: ./deploy/cluster/observability/controllers`, `prune: true`,
        `wait: true`. Holds the two default-login Secrets and the
        HelmReleases - **not** the Namespace; see below for why that turned
        out to matter. `wait: true` is what makes the next Kustomization's CRD
        assumption true rather than hopeful.
      - **`observability-config`** — `dependsOn: observability-controllers`,
        `path: ./deploy/cluster/observability/config`, `prune: true`,
        `wait: true`. Holds every instance of those CRDs — ScrapeConfigs,
        ServiceMonitors, PodMonitors, PrometheusRules — plus the Ingresses,
        dashboards and provisioning CronJobs.

      Both carry `postBuild.substituteFrom: aerie-cluster-config`. Neither names
      `aerie-image-tags`: nothing in this phase is a first-party image under
      automation, and 6b.13's provisioner is pinned by hand for the reason that
      step gives.

      **No `dependsOn` between this layer and `apps`, in either direction, and
      that is deliberate.** Nothing here needs the app tier to exist — a Kuma
      monitor pointed at a Service that is not there yet is a red monitor, which
      is a correct report — and the app tier does not need to be observed in
      order to run. Adding an edge would serialize two independent layers and
      make a Phase 5 failure also a Phase 6 failure.

      **Correction, found live on 2026-08-19 and worth recording rather than
      quietly editing away: the `observability` Namespace does NOT belong in
      this layer.** The first version of this step put it here on the theory
      that landing it in the same commit as 6b.2's ExternalSecrets was enough
      to satisfy their dependency on it. It is not. `observability-controllers`
      carries `dependsOn: infra-config` above, and 6b.2's ExternalSecrets live
      in `infra-config` (they are a rendering of `parameters.json`, which
      writes to `external-secrets/` in that layer, not this one). Landing both
      in one git commit does not make two independent Flux Kustomizations wait
      on each other: `infra-config` reconciles the ExternalSecrets regardless
      of `observability-controllers`, fails because the namespace they target
      does not exist, and never reports Ready - which means
      `observability-controllers` is never even attempted, since it depends on
      `infra-config` doing so. A deadlock, confirmed live as
      `ExternalSecret/observability/home-assistant not found: namespaces
      "observability" not found` sitting under `infra-config` indefinitely.

      The fix is the one
      [`namespaces.yaml`](../../../deploy/cluster/infrastructure/config/namespaces.yaml)
      already used for `aerie`, applied to `observability` too: put the
      Namespace directly in that file, in the same Kustomization
      (`infra-config`) as the ExternalSecrets that need it. kustomize-controller
      applies Namespaces as a first stage within one Kustomization's apply
      pass, ahead of everything else in it and with no `dependsOn` required -
      which is exactly why `aerie` already works this way, and exactly the
      reasoning this step's first pass failed to carry over. `namespaces.yaml`'s
      own comment carries the full account now. The consequence for
      `observability-controllers`'s own prune is correspondingly smaller than
      first written: deleting this layer no longer deletes the namespace (that
      is `infra-config`'s object now) - only what `observability-controllers`
      itself put in it, which as of this step is nothing but the two
      Secrets below.

      **`admin-secrets.yaml`**: two `Opaque` Secrets carrying 6a.4's defaults,
      in `stringData` so they are readable as written rather than base64 that
      has to be decoded to be reviewed.

      - `kuma-admin` — key `password`, value `password`. Read by AutoKuma
        (6b.12) and `kuma-provision` (6b.13).
      - `grafana-admin` — keys `admin-user` (`admin`) and `admin-password`
        (`password`), the pair `admin.existingSecret` expects in 6b.5.

      These are in `controllers/` and not `config/` because the HelmRelease that
      consumes one is, and a Secret that lands after the chart that mounts it is
      a pod stuck in `CreateContainerConfigError`. Put 6a.4's whole reasoning in
      a comment at the top of the file — that this is a known default, that it is
      expected to be changed from each UI, and that changing Kuma's means editing
      this file too. The file is where someone will be standing when they ask why
      a password is sitting in git, and the answer should be there rather than in
      a plan document they may never have read.
      *Exit:* `flux get kustomizations` shows both new Kustomizations Ready,
      `kubectl get ns observability` exists, and `kubectl -n observability get
      secret kuma-admin grafana-admin` returns both. Nothing runs in it yet.

- [x] **5. `kube-prometheus-stack`** —
      `deploy/cluster/observability/controllers/kube-prometheus-stack.yaml`. One
      pinned `HelmRelease` bringing Prometheus, the operator and its CRDs,
      Alertmanager, Grafana, node-exporter and kube-state-metrics. The largest
      single object in this phase, and most of its length is turning things off.

      **The four k3s disables**, per 6b.1's reasoning:

      ```yaml
      kubeScheduler:         { enabled: false }
      kubeControllerManager: { enabled: false }
      kubeProxy:             { enabled: false }
      ```

      and `kubeEtcd` left **enabled**, with its endpoint pointed at the servers'
      `:2381` that 6b.1 just exposed. Verify the chart's `kubeEtcd.endpoints`
      shape against the pinned version rather than copying a blog post — the
      chart historically generated an `Endpoints` object from a list of node IPs,
      which would be a fourth place this repo needs node addresses. If it still
      does, prefer selecting the nodes by label over pasting IPs, and if that is
      not possible, `WINDOWS_EXPORTER_TARGETS`' sibling key is cheaper than a
      hardcoded list.

      **The four selector settings, which are the ones everybody gets wrong:**

      ```yaml
      prometheus:
        prometheusSpec:
          serviceMonitorSelectorNilUsesHelmValues: false
          podMonitorSelectorNilUsesHelmValues: false
          probeSelectorNilUsesHelmValues: false
          scrapeConfigSelectorNilUsesHelmValues: false
      ```

      Left at their default `true`, Prometheus selects only objects carrying the
      chart's own release label, so every hand-written object in 6b.6 is
      **silently ignored** — no error, no event, no target, just a metric that
      never appears. Since this phase's entire scrape design is hand-written
      objects (see the re-scope preamble), these four lines are load-bearing for
      all of it.

      **Storage.** `prometheusSpec.storageSpec.volumeClaimTemplate` on
      `longhorn-r2`, 20Gi. Two facts about that:

      - A StatefulSet's `volumeClaimTemplates` is **immutable**. Getting the
        class or the size wrong is not an edit; it is deleting the StatefulSet
        with `--cascade=orphan` and letting the operator rebuild it, which is a
        procedure and not a commit. Decide the size once, here, deliberately.
      - Set **`retentionSize`** alongside `retention: 30d`, at roughly 80% of
        the volume. Retention by time alone on a fixed volume means the disk
        decides, and Prometheus's answer to a full disk is to stop ingesting.

      Alertmanager gets 1Gi on `longhorn-r3` — small and critical rather than
      bulk, because what it holds is silences, and losing those during a
      reschedule means every alert someone deliberately quieted re-fires at once.

      **Grafana**: `admin.existingSecret: grafana-admin` from 6b.4 —
      `admin`/`password` until you change it, and the chart seeds it only on a
      fresh database, so a later UI change is not undone by this value staying
      here —
      `persistence.enabled: true` on `longhorn-r3` at 2Gi, `grafana.ini`'s
      `server.root_url` set to `https://metrics.${DOMAIN}` (Grafana builds
      absolute URLs for redirects and share links from it; wrong, and OAuth-less
      logins still work while half the UI links to `localhost:3000`), and the
      dashboard sidecar enabled with `searchNamespace: ALL` so 6b.7's ConfigMaps
      are found wherever they sit. The Prometheus datasource is provisioned by
      the chart — do not port
      [`datasources/prometheus.yml`](../../../containers/grafana/provisioning/datasources/prometheus.yml),
      which points at a compose service name and would install a second,
      broken datasource beside the working one.

      Resource requests on every subchart per the sizing table, including the
      ones the chart hides — the operator, kube-state-metrics, both Grafana
      sidecars, and the `configReloader` sidecars the operator injects next to
      Prometheus and Alertmanager (`prometheusSpec.configReloader` — these are
      easy to miss because they are not in the chart's values tree where the
      main containers are, and 6b.14's sweep will find them).

      *Exit:* `kubectl -n observability get prometheus,alertmanager` Ready;
      `kubectl -n observability port-forward svc/…-prometheus 9090` and
      `/targets` shows **no down targets** — the whole point of 6b.1 and the
      disables above; `kubectl get crd | grep monitoring.coreos.com` lists
      `servicemonitors`, `podmonitors`, `scrapeconfigs`, `prometheusrules`.

- [x] **6. Every scrape target, as an object in this layer** —
      `deploy/cluster/observability/config/scrape/`. The step the re-scope
      preamble's first bullet exists for.

      **The rule, written into the directory's own kustomization.yaml so it
      survives this file:** a chart flag that *renders a `monitoring.coreos.com`
      object* is never turned on in layer 1, because layer 1 reconciles first
      and on an empty cluster the CRD does not exist yet — a failed Helm install
      that holds `infra-config` and therefore this entire layer behind it, and
      the only rebuild that discovers it is a disaster recovery rehearsal. A
      chart flag that merely *exposes a metrics port or Service* is fine and
      sometimes necessary; the distinction is whether the chart's output
      references a CRD this phase installs.

      Six things to scrape, five of them hand-written here:

      1. **windows_exporter — the three Windows hosts.** A
         `monitoring.coreos.com/v1alpha1` `ScrapeConfig` with a `staticConfigs`
         entry whose `targets:` is `${WINDOWS_EXPORTER_TARGETS}`. A ScrapeConfig
         rather than `prometheusSpec.additionalScrapeConfigs`: the latter is a
         blob inside the HelmRelease's values, so adding a host means editing the
         release and re-rolling Prometheus, while this is one small object in the
         layer that already holds objects. These are the physical machines —
         still worth scraping after everything else moved into VMs, because a
         failing disk or a thermal problem is a property of the host, and the
         cluster's view of it is a VM that got slow.
      2. **Flux.** `PodMonitor`s over `flux-system`, selecting
         `app.kubernetes.io/part-of: flux` on the `http-prom` port. Nothing
         installs these: [`Bootstrap-Flux.ps1`](../../../scripts/flux/Bootstrap-Flux.ps1)
         runs `flux install`, and Flux ships its monitoring manifests as a
         separate kustomize bundle that a bare install does not include. Without
         this, 6b.8's `gotk_reconcile_condition` alert — the durable half of
         3b.14 and arguably the single most valuable thing in this phase — has
         no metric to evaluate.
      3. **CloudNativePG.** A hand-written `PodMonitor` over the `aerie` namespace
         matching CNPG's instance pods on their metrics port. 3b.12 left
         `monitoring.podMonitorEnabled: false` with a note pointing here; **that
         note is now answered with "no", not "yes"**, and 3b.12's comment should
         be amended to say so rather than left looking unfinished. The ~480 lines
         of default queries are installed either way — the chart plants them in
         `cnpg-default-monitoring` and every `Cluster` inherits them — so this
         object is the only thing that was ever missing.
      4. **Longhorn.** A `ServiceMonitor` over `longhorn-system`, against the
         `longhorn-backend` Service's manager metrics. Volume health, replica
         counts and rebuild progress live here, and 6b.8's degraded-volume alert
         reads them.
      5. **Traefik.** Two halves, and only one of them is here. The
         `ServiceMonitor` is this layer's; **turning the metrics endpoint on is
         an edit to
         [`traefik-helmchartconfig.yaml`](../../../deploy/cluster/infrastructure/config/traefik-helmchartconfig.yaml)**
         in layer 2, and it is allowed under the rule above because
         `metrics.prometheus` renders a port and a Service, not a CRD instance —
         provided the chart's own `metrics.prometheus.serviceMonitor` sub-key
         stays off. Check the rendered output rather than trusting that
         sentence; the k3s-bundled Traefik chart has moved this key before.
      6. **The kubelet, cAdvisor, kube-state-metrics and node-exporter** are
         wired by kube-prometheus-stack itself and need nothing here. Noted so
         the absence reads as a decision rather than an omission. The standalone
         cadvisor container from
         [compose.metrics.yml](../../../compose.metrics.yml#L16) has no successor and
         needs none — the kubelet has exposed `/metrics/cadvisor` all along.

      *Exit:* Prometheus `/targets` shows every job **up**, including all three
      windows_exporter targets; `count(gotk_reconcile_condition)`,
      `count(cnpg_collector_up)` and `count(longhorn_volume_state)` all return
      non-zero.

- [ ] **7. Grafana dashboards, as labelled ConfigMaps** —
      `deploy/cluster/observability/config/dashboards/`. The sidecar 6b.5
      enabled watches for ConfigMaps labelled `grafana_dashboard: "1"` and loads
      whatever JSON they hold, which replaces
      [`dashboards.yml`](../../../containers/grafana/provisioning/dashboards/dashboards.yml)'s
      file provider without needing a volume.

      Of the two dashboards that exist today, **one survives and one does not**:

      - [`windows-exporter.json`](../../../containers/grafana/provisioning/dashboards/windows-exporter.json)
        moves across unchanged. Same exporter, same metric names, same three
        machines — only the scrape path changed, and a dashboard does not know
        about scrape paths. Its datasource is referenced by the fixed uid
        `prometheus`, so **either set the stack's datasource uid to `prometheus`
        or rewrite the dashboard's references**; the uid is the one thing the
        move can break, and it breaks as an empty panel rather than an error.
      - [`docker-cadvisor.json`](../../../containers/grafana/provisioning/dashboards/docker-cadvisor.json)
        does not. It groups by compose container name against a standalone
        cadvisor that no longer exists. kube-prometheus-stack ships better
        replacements for every panel on it, by namespace, workload and pod.
        Delete rather than port.

      Then add what the old host could not show: **Longhorn**, **CloudNativePG**
      and **Flux**. Each project publishes a dashboard; take the JSON, pin it by
      copying it into the repo rather than by `gnetId` — a dashboard fetched at
      runtime is an outbound dependency during a rebuild, which is
      [goal 6.3](design.md#goal-6--beyond-the-six)'s complaint about GHCR in a
      smaller form.

      One sizing note that is easy to trip over: a ConfigMap is capped at ~1 MiB
      and community dashboard JSON gets close. If one does not fit, that is the
      signal to trim its panels, not to reach for a volume.
      *Exit:* `metrics.${DOMAIN}` (via `--resolve`) lists every dashboard, each
      renders with data, and Grafana's log shows no "datasource not found".

- [ ] **8. Alerts, and the two things that make them trustworthy** —
      `deploy/cluster/observability/config/alerts/`. Two `PrometheusRule`s and
      the Alertmanager configuration.

      **The Flux alert, which is why this phase's bullet list named it
      specifically:**

      ```yaml
      - alert: FluxReconciliationFailing
        expr: gotk_reconcile_condition{type="Ready",status="False"} == 1
        for: 15m
        labels: { severity: warning }
        annotations:
          summary: '{{ $labels.kind }} {{ $labels.exported_namespace }}/{{ $labels.name }} has not reconciled for 15m'
      ```

      `for: 15m` is the whole design, not a default: Flux retries on its own
      `retryInterval`, and an alert without the hold fires on every transient
      registry timeout until it is ignored. 3b.14's two checks both run *before*
      a merge; this is the only thing in the system that notices a `HelmRelease`
      which breaks its own upgrade at 3am with no commit involved. Verify the
      label names against the running controllers — `exported_namespace` is what
      Prometheus renames `namespace` to when a metric already carries the pod's
      one, and a `summary` templated on the wrong label renders `<no value>` in
      the notification, which is discovered at exactly the moment it matters
      least.

      **The cluster alerts** [goal 6.4](design.md#goal-6--beyond-the-six) asks for,
      beyond the stack's own defaults (which already cover node NotReady, pod
      crash-looping, PVC filling and kubelet health — do not re-implement those):

      - **etcd quorum** — `etcd_server_has_leader == 0`, and members down. The
        reason 6b.1 exposed the port. On two servers this is `for: 1m`, not
        longer: with quorum 2 of 2, a lost member is an outage now.
      - **Longhorn degraded volumes** — `longhorn_volume_robustness == 2`
        (degraded), `for: 30m`. Longer than it looks like it should be, on
        purpose: Phase 1's staggered Windows Update reboots produce exactly this
        state, and the `staleReplicaTimeout: 30` in
        [`longhorn-storageclasses.yaml`](../../../deploy/cluster/infrastructure/config/longhorn-storageclasses.yaml)
        is the number this must not undercut. **Expect this one to fire until
        Phase 7** — `longhorn-r3` cannot be satisfied on two nodes, which the
        storage class file says out loud. Either accept it, or leave the rule
        out of the commit and add it in Phase 7 alongside the replica-count
        raise. Deciding *now* is the point; discovering it as noise later is how
        alerting dies.
      - **CNPG replication lag and instance count** — `cnpg_pg_replication_lag`
        above a threshold, and fewer ready instances than expected. Same Phase 7
        caveat if the threshold is written against three.

      **The receiver.** One Alertmanager `receiver` posting to 6a.3's webhook at
      `http://${HA_HOST}:${HA_PORT}/api/webhook/${HA_ALERT_WEBHOOK_ID}`,
      with a `route` that groups by `alertname` and `severity` and a
      `repeat_interval` measured in hours rather than minutes. Configure it as
      the chart's `alertmanager.config` in 6b.5's values rather than as a
      separate `AlertmanagerConfig` CRD — the CRD is for *additional*
      namespaced routes and does not replace the top-level config, which is
      where a default receiver has to live.

      **The dead-man's switch, which is the part worth stealing.**
      kube-prometheus-stack ships a `Watchdog` alert that always fires, existing
      only to prove the pipeline works. Route it to its own receiver: an
      **Uptime Kuma push monitor** (6b.12), whose URL Kuma generates and which
      goes red if nothing pushes for the interval. So:

      ```text
      Prometheus fires Watchdog → Alertmanager → Kuma push URL → Kuma stays green
      anything in that chain dies → Kuma goes red → Kuma's HA notification fires
      ```

      Two notification paths, and the newer one now proves itself over the older
      one — which is most of the argument the re-scope preamble owed for keeping
      both. Nothing else in the design notices a dead Prometheus.
      *Exit:* `kubectl -n observability get prometheusrule` lists both rules and
      Prometheus's `/rules` shows them loaded with no evaluation errors;
      deliberately break something small (suspend a Kustomization, or scale a
      Deployment to zero) and confirm a notification arrives in Home Assistant
      after the hold; the Watchdog monitor in Kuma is green.

- [ ] **9. OpenSearch and OpenSearch Dashboards, single-node** —
      `deploy/cluster/observability/controllers/opensearch.yaml`,
      `.../config/ingress-logs.yaml`. Two pinned `HelmRelease`s from the
      `opensearch-project` chart repository — a new `HelmRepository`, like 5b.3's.

      **Single-node is a decision, not a limitation.** It is the biggest RAM
      consumer in the cluster and observability was explicitly scoped out of HA
      ([design.md](design.md#decisions)). `replicas: 1`, `singleNode: true`, and
      the index settings that follow from it: a template with
      `number_of_replicas: 0`, because a one-node cluster cannot allocate a
      replica shard and every index otherwise sits permanently **yellow** — which
      is not a warning about anything and is the first thing anyone will chase.
      6b.11's index template is where that lands.

      Carry across from
      [compose.observability.yml](../../../compose.observability.yml#L71-L87):
      `DISABLE_SECURITY_PLUGIN=true`, `OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx512m`,
      the `nofile` ulimit, and the data volume as a 20Gi PVC on **`longhorn-r2`**
      (bulk, loss tolerable — logs age out by design). Drop the loopback port
      publish; `kubectl port-forward` is the successor and needs nothing
      declared.

      **What disabling the security plugin now means, which is more than it did.**
      On the old host OpenSearch sat on a private compose network with one
      loopback port. In the cluster, an unauthenticated OpenSearch is reachable
      by every pod in every namespace, and its data now includes every system
      pod's logs. The LAN-trust reasoning in
      [`docs/monitoring-alerting-architecture.md`](../../monitoring-alerting-architecture.md)
      still holds for the *humans* on this network; it was not written about pod
      -to-pod reachability. Two mitigations, neither of which is turning the
      security plugin on (which brings a certificate lifecycle this phase should
      not own): a `NetworkPolicy` admitting only fluent-bit and Dashboards, and
      writing the exposure down. Do the first; the second is this paragraph.

      Dashboards gets `OPENSEARCH_HOSTS` pointed at the Service,
      `DISABLE_SECURITY_DASHBOARDS_PLUGIN=true`, and an Ingress at
      `logs.${DOMAIN}` in the 5b.9 shape — **no `tls:` block**, because
      [`traefik-tlsstore.yaml`](../../../deploy/cluster/infrastructure/config/traefik-tlsstore.yaml)'s
      default certificate covers every hostname from one Secret in `kube-system`.
      That is the entire argument for having bought a wildcard, and it is what
      lets an Ingress in a namespace that has never seen the cert Secret serve
      valid TLS.
      *Exit:* `vm.max_map_count` having been set in 6b.1, the pod reaches Ready
      on the first try; `curl .../_cluster/health` reports **green** (not
      yellow — see above); `logs.${DOMAIN}` via `--resolve` serves the
      Dashboards UI over a valid certificate.

- [ ] **10. The fluent-bit rewrite** —
      `deploy/cluster/observability/controllers/fluent-bit.yaml` and the config
      alongside it. Finding 4, and the only step in this phase that rewrites
      rather than re-hosts.

      A pinned `HelmRelease` of the `fluent-bit` chart (DaemonSet, plus the
      `ClusterRole` the kubernetes filter needs to read pods and namespaces — the
      chart handles RBAC, which is most of why it is a chart here and Kuma is
      not).

      **The pipeline, part by part against
      [fluent-bit.conf](../../../containers/fluent-bit/fluent-bit.conf):**

      | Today | Becomes | Why |
      |---|---|---|
      | `Path /var/lib/docker/containers/*/*.log` | `Path /var/log/containers/*.log` | containerd, not docker |
      | `Parser docker` | `multiline.parser cri` | CRI log lines are `<time> <stream> <P\|F> <log>`, and the `P`/`F` flag means a long line arrives **split**. A plain `cri` parser reassembles nothing and ships the halves as separate records |
      | *(nothing)* | `[FILTER] kubernetes` | Attaches pod name, namespace, container name and labels — the replacement for the compose label |
      | `[FILTER] parser … dotnet_json` | unchanged | Aerie.Api's structured console lines still need decoding out of `log`; [custom_parsers.conf](../../../containers/fluent-bit/custom_parsers.conf) moves across as-is |
      | `[FILTER] lua service_tag` | half rewritten | Below |
      | `[OUTPUT] opensearch Host opensearch` | the Service name | Otherwise unchanged: `Logstash_Format On`, prefix `aerie-logs`, `Suppress_Type_Name On` |

      **The Lua, which Finding 4 is emphatic about.**
      [service_tag.lua](../../../containers/fluent-bit/service_tag.lua) has two
      branches and they have different fates. The compose-label branch —
      `record["attrs"]["com.docker.compose.service"]` — has no meaning under
      containerd and is replaced by reading
      `record["kubernetes"]["container_name"]`, which the kubernetes filter has
      just populated. **The `State.Service` branch survives untouched**, and it
      is the reason this file exists at all: it is what attributes a
      `UiLogsController` line to `dashboard` or `admin` rather than lumping it
      under `aerie-api`
      ([UiLogsController.cs:30-34](../../../src/Aerie.Api/Controllers/UiLogsController.cs#L30-L34)),
      and what puts a Hyper-V console line under `vm-console.<VMName>`
      ([VmConsoleLogsController.cs:44-49](../../../src/Aerie.Api/Controllers/VmConsoleLogsController.cs#L44-L49)).
      Precedence is unchanged: the container-level default first, `State.Service`
      overriding it. The chart takes the script through its `luaScripts` value,
      so it stays a real `.lua` file in the repo rather than a string embedded in
      YAML.

      **Three mount details that each fail silently:**

      - `/var/log/containers/*.log` are **symlinks** into `/var/log/pods/`.
        Mount `/var/log` whole (which covers both) — mounting only
        `/var/log/containers` gives a DaemonSet that finds files and reads
        nothing from them.
      - Set `DB` to a path on a **hostPath**, not an emptyDir. That file holds
        the tail offsets; on an emptyDir a pod restart re-ships everything the
        node has retained, which is a duplicate-log flood arriving exactly when
        something is already wrong.
      - `Mem_Buf_Limit` was 10MB against one compose project. Against a whole
        node's logs with OpenSearch briefly unavailable, that is where records
        get dropped. Raise it, and know that the honest fix for a longer outage
        is filesystem buffering (`storage.type filesystem`), not a bigger number.

      **The volume reckoning, which is a decision this step must make rather
      than defer.** This DaemonSet now tails every pod on every node. Keep the
      broad tail — Longhorn, CNPG, k3s and Flux logs in the same index as the
      app's is the upgrade, and searching them at 3am is what
      `logs.${DOMAIN}` is for. Pay for it in three places: measure index growth
      after a week (`_cat/indices`), set 6b.11's ISM retention from that
      measurement rather than inheriting the 30 days written for one host, and
      add a `grep` filter excluding anything found to be pure noise —
      health-check access logs are the usual first offender. Write the measured
      number into 6b.11's script comment so the next person knows it was
      measured.
      *Exit:* the DaemonSet is Ready on every node;
      `curl .../aerie-logs-*/_count` climbs; a query for
      `service: dashboard` returns UI lines and a query for
      `service: longhorn-manager` returns cluster ones — one assertion for each
      half of the Lua.

- [ ] **11. The three OpenSearch provisioning scripts, as CronJobs** —
      `deploy/cluster/observability/config/provisioning/`. Finding 6's first
      half.

      [`apply-ism-policy.sh`](../../../containers/opensearch-provision/apply-ism-policy.sh),
      [`apply-index-template.sh`](../../../containers/opensearch-provision/apply-index-template.sh)
      and [`create-index-pattern.sh`](../../../containers/opensearch-provision/create-index-pattern.sh)
      move into a ConfigMap and run under `curlimages/curl` — a public image, so
      unlike 6b.13 this one needs no pull secret. Their own retry loops
      (30 attempts, 5s apart) come across unchanged and are what makes the
      startup ordering a non-problem.

      **A CronJob, not a Job, and the reason generalizes.** A `Job`'s pod
      template is immutable, so Flux re-applying an unchanged Job is a no-op and
      it never runs again — which loses the property the compose comments call
      out three times, that these converge on **every** deploy. Phase 4 solved
      the same tension differently
      ([restore-job.yaml](../../../deploy/cluster/data/schema/restore-job.yaml), re-run
      by hand with `kubectl create job --from=`), and that is right for a restore
      — a one-shot with consequences, invoked deliberately. It is wrong for
      these: `create-index-pattern.sh` cannot succeed until fluent-bit has
      shipped a log and the `time` field is discoverable, so a single run at
      install time is a coin flip. Hourly, `concurrencyPolicy: Forbid`,
      `successfulJobsHistoryLimit: 1`, `ttlSecondsAfterFinished` so finished pods
      do not accumulate.

      **Two edits to the scripts themselves**, both consequences of decisions
      above rather than of the move:

      - the index template gains `number_of_replicas: 0` (6b.9 — single node,
        or every index sits yellow forever)
      - the ISM policy's `min_index_age` is set from 6b.10's measurement rather
        than left at the 30 days written for one compose project

      *Exit:* `_plugins/_ism/policies/aerie-log-retention` and
      `_index_template/aerie-logs` both return 200; the `aerie-logs` index
      pattern exists in Dashboards **with a populated field list** — the failure
      [create-index-pattern.sh](../../../containers/opensearch-provision/create-index-pattern.sh)'s
      long header exists to prevent — and Discover renders its histogram.

- [ ] **12. Uptime Kuma and AutoKuma, as plain manifests** —
      `deploy/cluster/observability/controllers/uptime-kuma.yaml`,
      `.../autokuma.yaml`, `.../config/static-monitors-configmap.yaml`,
      `.../config/ingress-status.yaml`.

      Plain objects rather than a community chart — see the re-scope preamble.
      Kuma is a Deployment, a Service, a PVC and an Ingress, and a chart for that
      is dependency surface bought for nothing.

      **Two things that are not obvious and both bite once:**

      - **`strategy: Recreate`, not the default RollingUpdate.** Kuma's state is
        SQLite on one RWO Longhorn volume. A rolling update starts the new pod
        before the old one releases the volume, so the new pod hangs on attach
        until the rollout times out — presenting as an upgrade that never
        finishes rather than as the storage conflict it is. Same reasoning
        applies to Grafana's PVC in 6b.5 if its replica count is ever raised.
      - `UPTIME_KUMA_DB_TYPE=sqlite` carries across, for the reason
        [compose.observability.yml:16-19](../../../compose.observability.yml#L16-L19)
        already documents: without it the first-run setup HTTP server blocks
        until a human picks a database in a browser, and the Socket.IO API
        6b.13's provisioner talks to never starts.

      AutoKuma reads the admin password from 6b.4's `kuma-admin` Secret —
      `aerie-kuma-admin!23` does not move across; the cluster's default is
      `password` — and its static monitors come from a ConfigMap rather than a
      bind mount. This is the client 6a.4 warns about: if the Kuma password has
      been changed in the UI and the Secret has not, this Deployment is where it
      shows up, as authentication failures in AutoKuma's log and monitors that
      never converge. **Every one of the four monitors changes:**

      | Today | Becomes |
      |---|---|
      | `api.toml` → `http://api:8080/health` | `http://aerie-api.aerie.svc:8080/health/ready` — the Service name, and 5b.2's split probe, since `/health` reported healthy with the database down |
      | `caddy.toml` → `http://caddy:80` | **deleted.** Finding 7: Caddy does not exist. Replaced by a monitor on the VIP through Traefik, which is what actually fronts everything now |
      | `db.toml` → port monitor on `db:5432` | `aerie-pg-rw.aerie.svc:5432` — the CNPG read-write Service, which follows a failover, unlike a pod |
      | `ha.toml` → `http://homeassistant.local:8123` | `http://${HA_HOST}:${HA_PORT}` — a hardcoded hostname in a redeployable repo is exactly what [ethos](../../ethos.md) rules out, and 6b.3 made both keys required |

      Then add what did not exist to monitor: `files` and `share` from Phase 5,
      the Kuma **push** monitor 6b.8's watchdog targets, and an external
      reachability check if one is wanted. Note the capability being lost with
      no replacement: AutoKuma's Docker-label discovery has no k8s equivalent
      here, so from now on monitors are the ones in this ConfigMap and nothing
      appears by itself. That is a fair trade for a declarative file, and it is
      a trade.

      Ingress at `status.${DOMAIN}`, same shape as 6b.9's, no `tls:` block.
      Kuma uses WebSockets for its live UI — confirm Traefik passes them (it
      does by default; the failure mode is a status page that renders once and
      then never updates, which reads as a broken monitor rather than a broken
      proxy).
      *Exit:* `status.${DOMAIN}` via `--resolve` serves the dashboard over a
      valid certificate, every static monitor is present and green except any
      pointed at something legitimately down, and the page updates live.

- [ ] **13. `kuma-provision` as a built image and a CronJob** —
      `containers/kuma-provision/Dockerfile`,
      [publish.yml](../../../.github/workflows/publish.yml),
      `deploy/cluster/observability/config/provisioning/kuma-provision.yaml`.
      Finding 6's second half.

      Today this runs `pip install --quiet --no-cache-dir -r requirements.txt &&
      python provision.py` at every deploy
      ([compose.observability.yml:66](../../../compose.observability.yml#L66)), which
      makes a PyPI outage a deploy failure — and worse in the cluster, where the
      pod restarts on failure and the outage becomes a crash loop. Build it:
      `python:3.12-slim`, `pip install -r requirements.txt`, copy
      [provision.py](../../../containers/kuma-provision/provision.py), and **pin
      `uptime-kuma-api`** in
      [requirements.txt](../../../containers/kuma-provision/requirements.txt), which is
      currently unpinned — a floating dependency in an image built once is a
      build that is not reproducible, which is the same complaint one line up.

      A new `publish.yml` job alongside the existing ones. It does **not** get an
      `ImageRepository`/`ImagePolicy` — 5b.12's automation exists for images that
      change with the app, and a provisioning script that changes twice a year is
      pinned by hand in the CronJob. Say so in the manifest, or the next person
      reasonably assumes it was forgotten.

      A CronJob for the same reason as 6b.11, with one extra: this script
      attaches the Home Assistant notification to **every existing monitor**, and
      AutoKuma creates monitors continuously. A one-shot run at install time
      attaches the notification to whatever existed in that instant and nothing
      after; hourly convergence means a monitor added next month gets alerting
      without anyone remembering this step exists. Env from 6b.4's `kuma-admin`
      for the password, 6b.2's `home-assistant` for the token, and 6b.3's
      ConfigMap (`HA_HOST`, `HA_PORT`), with the `ghcr-pull` secret in
      `imagePullSecrets` — 6b.2's third namespace, and the only reason it was
      needed.
      *Exit:* the CronJob's first run exits 0; Kuma's Settings > Notifications
      lists "Home Assistant" as default; every monitor shows it attached; and
      6b.8's watchdog notification actually arrives on a phone.

- [ ] **14. PriorityClasses, and the resource sweep** —
      `deploy/cluster/observability/controllers/priorityclass.yaml` plus requests
      on everything above. The property [design.md](design.md#decisions)'s "HA
      *not* required — observability" line has been asserting since Phase 0 and
      nothing has yet implemented.

      **The mechanism.** A `PriorityClass` with a **negative** value —
      `aerie-observability`, `value: -10`, `globalDefault: false` — set on every
      workload in this phase. Kubernetes schedules and preempts by priority, so
      on a surviving node that cannot fit everything, the scheduler evicts these
      pods to make room for higher-priority ones rather than leaving an API
      replica Pending. Negative rather than merely low, so it sits below the zero
      that every Phase 5 workload gets by default without any of them having to
      opt in — the alternative is annotating the entire rest of the cluster to
      out-rank this phase, which is the same statement made in twenty more
      places.

      This is the concrete answer to 5a.5's budget: the phase is *allowed* to be
      the thing that does not fit, as long as the cluster knows it. Two
      consequences to write into the manifest, because both look like bugs later:
      an evicted Grafana is normal during a node failure and comes back on its
      own, and a permanently Pending OpenSearch after a node loss means the
      remaining node genuinely cannot hold it — which is a sizing answer, not a
      scheduling one.

      **The sweep**, extending 5b.11's over this phase's namespace: every
      container in `observability` reports non-empty `resources.requests`. The
      ones that will be missing are the ones the charts hide — the operator's
      injected `config-reloader` sidecars, Grafana's two sidecars, the chart's
      own init containers, and the CronJob pods, which are containers the same
      way anything else is and are invisible between runs.

      Then check the total against **one node**, not the cluster, exactly as
      5b.11 says: the failure this phase must survive is one node taking its
      staggered reboot while the other holds everything.
      *Exit:* the check 6b.15 automates — every container in `observability` has
      requests, every pod in it carries `priorityClassName: aerie-observability`,
      and the sum of requests across all namespaces fits one node's allocatable
      with the app tier still schedulable.

- [ ] **15. Phase gate as a command** — `scripts/k3s/Test-Observability.ps1`,
      `.github/workflows/verify-observability.yml`, dispatched as *Verify:
      Observability*. The shape 3b.13, 4b.11 and 5b.14 established: a read-only
      script over SSH, a thin workflow wrapper that hands it every decision, and
      `runs-on: [self-hosted, …]` so it never consumes Actions minutes and can
      actually reach the nodes.

      What it asserts, one per step above:

      - `sysctl vm.max_map_count` is 262144 on every node and `:2381` answers
        (6b.1)
      - every `ExternalSecret` in `observability` is `SecretSynced` (6b.2)
      - `WINDOWS_EXPORTER_TARGETS` parses as a list and every host in it answers
        from inside a pod (6b.3, 6a.2)
      - both Kustomizations Ready, and `kuma-admin` and `grafana-admin` present
        in the namespace — **existence only, never the value** (6b.4). An
        operator who has changed either password must not fail this gate.
      - **Prometheus reports zero down targets** — the single most informative
        assertion in the script, covering 6b.1, 6b.5 and 6b.6 at once
      - the six scrape jobs each return a non-zero series count (6b.6)
      - both `PrometheusRule`s loaded with no evaluation errors, and
        Alertmanager shows the Watchdog alert firing to a receiver (6b.8)
      - OpenSearch cluster health is **green**, not yellow (6b.9)
      - `aerie-logs-*` doc count is climbing, and both halves of the Lua produce
        results (6b.10)
      - the ISM policy, the index template and the index pattern all exist
        (6b.11)
      - all three hostnames — `status`, `logs`, `metrics` — serve a valid
        certificate through the VIP (6b.9, 6b.12, and 6b.5's Grafana)
      - every container in `observability` has requests and every pod carries
        the PriorityClass (6b.14)

      Leave this box unticked until the workflow has actually been dispatched
      and passed, for the same reason 3b.13, 4b.11 and 5b.14 stayed unticked: a
      gate that has never run has proved nothing.

---

## Where these files live

```text
deploy/cluster/
  kustomization.yaml              # + observability.yaml
  observability.yaml              # 6b.4, two Kustomizations:
                                  #   observability-controllers -> -config
  observability/
    controllers/
      namespace.yaml              # 6b.4
      admin-secrets.yaml          # 6b.4, kuma-admin + grafana-admin defaults
      kube-prometheus-stack.yaml  # 6b.5, the largest object in the phase
      opensearch.yaml             # 6b.9, two HelmReleases + HelmRepository
      fluent-bit.yaml             # 6b.10, DaemonSet via chart
      fluent-bit-service-tag.lua  # 6b.10, the surviving half
      uptime-kuma.yaml            # 6b.12, plain manifests
      autokuma.yaml               # 6b.12
      priorityclass.yaml          # 6b.14
      kustomization.yaml
    config/
      scrape/                     # 6b.6, every ServiceMonitor/PodMonitor/
        windows-exporter.yaml     #   ScrapeConfig in the cluster
        flux.yaml
        cloudnative-pg.yaml
        longhorn.yaml
        traefik.yaml
      dashboards/                 # 6b.7, one ConfigMap per dashboard
      alerts/
        flux.yaml                 # 6b.8, the gotk_reconcile_condition rule
        cluster.yaml              # 6b.8, etcd / Longhorn / CNPG
      provisioning/
        opensearch-scripts.yaml   # 6b.11, the three scripts as a ConfigMap
        opensearch-provision.yaml # 6b.11, CronJob
        kuma-provision.yaml       # 6b.13, CronJob
      static-monitors.yaml        # 6b.12, the four rewritten monitors
      ingress-logs.yaml           # 6b.9
      ingress-status.yaml         # 6b.12
      networkpolicy-opensearch.yaml  # 6b.9
      kustomization.yaml

containers/kuma-provision/
  Dockerfile                      # 6b.13, so a PyPI outage is not a deploy

scripts/k3s/
  Test-Observability.ps1          # 6b.15
.github/workflows/
  verify-observability.yml        # 6b.15
```

Two structural notes. **Everything that instantiates a `monitoring.coreos.com`
CRD is under `config/`, and nothing outside this phase does** — that is the
re-scope preamble's first bullet expressed as a directory, and the reason
`config/scrape/` is its own subtree rather than five files scattered among the
Ingresses. **`controllers/` and `config/` mirror
[`infrastructure/`](../../../deploy/cluster/infrastructure/)'s split for the same
reason it has one**: layer 1 registers types, layer 2 creates instances of them,
and `wait: true` on the first is what makes the second's assumption true.

## What Phase 6 deliberately does not do

- **It does not delete `compose.observability.yml` or `compose.metrics.yml`.**
  The old host serves production until Phase 7, and both files are still
  deploying it. The bind-mounted sources under
  [`containers/`](../../../containers/) stay with them — this phase adds copies
  under `deploy/`, and the duplication is deliberate and temporary, exactly as
  5b.13 argued for `cd.yml`.
- **It does not remove `cd.yml`'s Prometheus reload step.** Finding 5 says it
  disappears, and it does — in Phase 7, with the compose file it reloads. The
  old host's [prometheus.yml](../../../containers/prometheus/prometheus.yml) is
  untouched by this phase.
- **It does not rotate the old host's Kuma admin password.** The cluster gets
  its own default (6a.4); `aerie-kuma-admin!23` keeps working on a system being
  deleted. Changing it means a live SQLite database that disagrees with git.
- **It does not put the two admin passwords behind ESO, and does not change them
  for you.** 6a.4 ships `admin`/`password` on Kuma and Grafana as a known
  default, committed, with the security implication accepted deliberately. The
  first operator to log into either is expected to change it; nothing in this
  phase enforces that, checks for it, or expires the default — 6b.15's verify
  does not assert on it, precisely so that changing it does not turn the
  verification red.
- **It does not turn on the security plugin in OpenSearch.** A `NetworkPolicy`
  and a written-down exposure, per 6b.9 — the certificate lifecycle that plugin
  brings is not this phase's to own.
- **It does not make observability HA.** 6b.14 makes the *opposite* mechanical.
  Single-node OpenSearch, one Prometheus, one Grafana, one Kuma, all
  reschedule-on-failure.
- **It does not flip `monitoring.podMonitorEnabled` on CloudNativePG**, or any
  other layer-1 chart flag that renders a CRD instance. 3b.12 left that question
  open pointing here; 6b.6 answers it "no" and says why.
- **It does not consolidate the two notification paths.** Kuma and Alertmanager
  both reach Home Assistant, by different routes, and 6b.8 buys the redundancy
  back by routing one through the other. Merging them is a Phase 9 question.
- **It does not raise `POSTGRES_INSTANCES` or `LONGHORN_REPLICA_COUNT`.** Phase
  7, both, unchanged from what Phase 5 said.

## Additions this phase makes to other phases

Recorded here, to be written into the phases that own them:

- **Phase 3b.12's note needs closing.**
  [`cloudnative-pg.yaml`](../../../deploy/cluster/infrastructure/controllers/cloudnative-pg.yaml)
  says `monitoring.podMonitorEnabled` "cannot be flipped yet" and points at this
  phase. It is not "yet" — it is never, for the cold-rebuild reason in 6b.6, and
  the comment should say that instead of reading like unfinished business.
- **Phase 7 inherits three deletions this phase made possible**:
  `compose.observability.yml`, `compose.metrics.yml`, and everything under
  `containers/` that only they bind-mount —
  [`fluent-bit/`](../../../containers/fluent-bit/),
  [`prometheus/`](../../../containers/prometheus/),
  [`grafana/provisioning/`](../../../containers/grafana/provisioning/),
  [`opensearch-provision/`](../../../containers/opensearch-provision/),
  [`autokuma/static-monitors/`](../../../containers/autokuma/static-monitors/) — plus
  `cd.yml`'s Prometheus reload step.
  `containers/kuma-provision/` **survives**, because 6b.13 turned it into a built
  image the cluster uses.
- **Phase 7 must revisit two alert thresholds it will make wrong.** The Longhorn
  degraded-volume rule and the CNPG instance-count rule in 6b.8 are written
  against a two-node cluster; joining the third node and raising both replica
  counts is what makes the three-replica expectations correct. Same sentence as
  5b.11's `whenUnsatisfiable` note and 4b.6's `dataDurability` note, for the same
  reason — and this one is more urgent, because a threshold that is wrong in the
  quiet direction stops alerting rather than starting.
- **Phase 7's hostname check gains nothing and loses nothing.** `status`, `logs`
  and `metrics` are three of the seven it already lists; this phase is what makes
  them serve something.
- **Phase 8 gets its first alert consumer.** [goal 6.4](design.md#goal-6--beyond-the-six)
  asks for backup age alongside the cluster alerts, and 6b.8 builds the
  `PrometheusRule` + receiver path it needs. Phase 8 supplies the metric — a
  restic snapshot timestamp exposed for scraping — and adds one rule to
  `config/alerts/`. Worth deciding there whether that metric comes from a
  textfile-collector-style sidecar or a small exporter, because the answer
  shapes Phase 8's backup job.
- **Phase 9 owns consolidating the notification paths**, and owns the two
  dashboards-and-alerts documents this phase makes stale:
  [`docs/metrics-architecture.md`](../../metrics-architecture.md) (whose "Windows
  Server detour" reasoning is still correct about *why* windows_exporter is
  native, and wrong about every scrape path around it) and
  [`docs/monitoring-alerting-architecture.md`](../../monitoring-alerting-architecture.md)
  (whose LAN-trust argument was written about humans on a network, not pods in a
  cluster — see 6b.9). Both are candidates for folding into the
  `cluster-architecture.md` Phase 9 calls for, rather than being patched.
