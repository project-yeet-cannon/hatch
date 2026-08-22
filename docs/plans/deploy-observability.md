# Delivery Observability

**Status:** Plan, unstarted. Phases are independent and ordered by priority —
transparency first, then latency, then alerting — not by dependency. Phase 1 is
worth doing on its own even if nothing after it happens.

The cutover traded a green checkmark for a reconciliation loop.
[`delivery-architecture.md`](../delivery-architecture.md) predicted this in as
many words — *"the cost, honestly: you lose the single green checkmark that
means 'it shipped'"* — and pointed at an observability section to get it back.
This plan is that section, made concrete.

Two problems wear the same complaint and have different fixes, so they are kept
apart throughout:

- **Latency.** A push reaches the cluster on a chain of independent poll
  intervals whose worst case is ~20 minutes.
- **Visibility.** Nothing anywhere says "your commit is live." The state exists,
  in five different places, and assembling it is a manual act.

## The pipeline as it exists today

Measured from `git push` on `main`, for a change to application code:

| Hop | Mechanism | Latency |
|---|---|---|
| CI + image build | [`ci.yml`](../../.github/workflows/ci.yml) and [`publish.yml`](../../.github/workflows/publish.yml) in parallel; publish blocks on [`wait-for-ci`](../../.github/actions/wait-for-ci/action.yml) before `docker push` | build time |
| Registry scan | `ImageRepository` `interval: 5m` ([`image-repositories.yaml`](../../deploy/cluster/apps/automation/image-repositories.yaml)) | **≤5m** |
| Tag selection | `ImagePolicy` re-evaluates on scan | immediate |
| Tag commit | `ImageUpdateAutomation` — **watch-driven, not its 5m interval** (Finding 2) | immediate |
| Site repo pull | `GitRepository/aerie-site` `interval: 1m` | **≤1m** |
| ConfigMap apply | `site-config` watches its source | immediate |
| Chart re-render | `apps` `interval: 10m` — **nothing watches the ConfigMap it substitutes from** (Finding 3) | **≤10m** |
| Helm upgrade + rollout | migrate hook, then rolling update | 1–3m |

**Best case ~3 minutes after the image lands; worst case ~19; no signal at any
point.** Two of the three waits are avoidable outright, and the third is a
one-line change.

For a change to `deploy/` or `charts/` the picture is much better already — the
`flux-system` `GitRepository` polls at 1m and kustomize-controller watches it —
which is why this reads as an *app deploy* problem specifically.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Where the one-stop shop lives | Grafana, at `metrics.${DOMAIN}` | It is already the one-stop shop for everything else, already has Prometheus, already loads dashboards from labelled ConfigMaps, and is the only surface that can show *both* halves — GitHub Actions and cluster state — in one pane |
| How CI state reaches Grafana | `grafana-github-datasource` plugin, PAT via the existing SSM → External Secrets path | The alternative is scraping the Actions API into Prometheus with an exporter nobody maintains. A datasource is a config change, not a workload |
| How deploy *events* reach Grafana | Flux `Provider` type `grafana` + an `Alert` | Turns every reconcile into a dashboard annotation. Vertical lines on the timeline are what makes "did the graph move because of my deploy" answerable |
| Restoring the green check | Flux `Provider` type `github` + `Alert` → commit status on the Aerie commit | Cluster → GitHub. Inverts none of the credential direction the cutover bought, unlike a kubeconfig in Actions |
| "Is *my* commit live", precisely | A deploy-gate workflow that asserts the running image tag carries the pushed SHA | Commit status from a `Kustomization` answers "the layer reconciled," which is not the same question (Finding 5) |
| Killing the poll | Flux `Receiver`s, poked from the pipeline | Push-triggered *reconciliation*, not push-based deploy. Desired state still comes only from git; the webhook says "now", never "what" |
| Who pokes the Receiver | A self-hosted runner on the LAN | The cluster is not publicly resolvable (Finding 4), so GitHub cannot deliver a webhook to it. Also free — Actions minutes have run out on this repo before |
| Closing the 10m `apps` gap | Alert on `site-config` → generic-hmac Provider → Receiver on `Kustomization/apps` | The event-driven fix rather than a shorter interval. Dropping the interval to 2m is the interim, written down as such |
| Notification on deploy outcome | Flux `Alert` → the existing Home Assistant webhook | A rapid iterator should be told when it lands, not have to go look. The path already exists and is already trusted |
| A Kubernetes/GitOps UI (Headlamp, Weave GitOps) | Deferred, not rejected | It answers the cluster half beautifully and the GitHub half not at all, and it brings its own auth story. Revisit if the Grafana dashboard proves the wrong shape |
| A delivery page inside Aerie itself | Rejected | Aerie ships to other operators ([`ethos.md`](../ethos.md)). A CI/CD console for *this* repository is not part of the product |

## Findings from the repo

Verified before writing; each one changes a step below.

1. **notification-controller is already installed and completely unused.**
   [`Bootstrap-Flux.ps1`](../../scripts/flux/Bootstrap-Flux.ps1) installs the
   default component set, which includes it. There is not one `Alert`,
   `Provider` or `Receiver` object anywhere in `deploy/`. Every push-side item
   in this plan is configuration against a controller that is already running.

2. **`ImageUpdateAutomation`'s 5m interval is not in the critical path.**
   image-automation-controller `Watches(&reflectorv1.ImagePolicy{}, ...)` and
   enqueues every automation referencing a policy that moved. Poking the
   `ImageRepository` therefore cascades scan → policy → commit with no waiting
   in between, which is what makes a single webhook worth so much.

3. **kustomize-controller does not watch `postBuild.substituteFrom` ConfigMaps.**
   Its only ConfigMap RBAC is `get;list;watch` for reading them at render time;
   there is no watch wired to a reconcile request. So when `site-config` writes
   a new `aerie-image-tags`, nothing tells `apps` — it finds out on its next
   10m tick. **This is the single largest source of deploy latency**, and it is
   invisible in `flux get` output because every object involved reports Ready
   the whole time.

4. **The cluster has no public DNS and no public address.** TLS is DNS-01
   against Route53 precisely so nothing needs a public A record
   ([`reverse-proxy-architecture.md`](../reverse-proxy-architecture.md)). GitHub
   cannot POST a webhook to it. Anything that pokes the cluster runs on the LAN
   — which the `hyperv-host-*` self-hosted runners already do, with the
   `verify-*.yml` workflows as the worked precedent.

5. **A `Kustomization` commit status can go green before the code ships.** The
   `apps` Kustomization's revision is the *Aerie* `main` SHA, so a status posts
   against the right commit — but `apps` reconciles on its own interval for
   unrelated reasons too, and one of those reconciles landing before the image
   tag does would post a premature success under the same status context. The
   status is a good signal for "the layer is healthy" and a bad one for "my
   binary is running." Hence both it and the gate workflow, doing different
   jobs.

6. **`gotk_reconcile_condition` is live and carries a label trap.** The
   `FluxReconciliationFailing` rule
   ([`alerts/flux.yaml`](../../deploy/cluster/observability/config/alerts/flux.yaml))
   already reads it. The reconciled object's namespace arrives as
   **`exported_namespace`**, not `namespace` — the PodMonitor's own `namespace`
   label wins the collision. Every panel query in Phase 1 has to use the
   exported name.

7. **Dashboard JSON must have `${` escaped to `$${`.** `observability-config`
   runs under `StrictPostBuildSubstitutions`, and an unescaped Grafana macro or
   datasource variable fails the whole Kustomization — twice, live, on
   2026-08-19. See the header of
   [`dashboards/kustomization.yaml`](../../deploy/cluster/observability/config/dashboards/kustomization.yaml),
   which carries the full account. `kubectl kustomize` does not catch it.

8. **Secrets have exactly one path in.** `scripts/secrets/parameters.json` →
   SSM → External Secrets → a Kubernetes Secret, seeded by
   `provision-2-seed-secrets.yml`. Three new parameters below follow it
   unchanged; none of them is a new *kind* of credential.

## Phase 1 — The dashboard

The one-stop shop, and the thing that actually answers the question. Depends on
nothing else here — 1.3 is what turns it from a status board into a timeline,
and it lives inside this phase for exactly that reason.

- [ ] **1.1 The GitHub datasource.** Add `grafana-github-datasource` to
      `grafana.plugins` in
      [`kube-prometheus-stack.yaml`](../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml)
      and provision the datasource with a read-only PAT (new parameter
      `github/read-token`; `actions:read`, `contents:read`).

      Two things to get right: the chart's `sidecar.datasources` already owns
      datasource provisioning, so this is an `additionalDataSources` entry, not
      a second mechanism; and the plugin caches API responses for up to five
      minutes, which is a *floor* on CI panel freshness. That is acceptable for
      CI history and is not acceptable for "is it live yet" — which is why the
      live half of the dashboard reads Prometheus, not GitHub.

      *Exit:* a Grafana Explore query against the datasource returns recent
      workflow runs.

- [ ] **1.2 The `Delivery` dashboard**, as
      `deploy/cluster/observability/config/dashboards/delivery.json` plus one
      generator entry in that directory's `kustomization.yaml`. **Escape every
      `${` to `$${` before committing** (Finding 7).

      Four rows, top to bottom, reading as one story:

      | Row | Panels | Source |
      |---|---|---|
      | Now | Latest `main` SHA; image tag `ImagePolicy` selected; tag in `aerie-image-tags`; tag actually running; agree/disagree stat | Prometheus + GitHub |
      | CI | Last 20 runs of *Build and test* and *Build and publish containers* — status, duration, actor | GitHub datasource |
      | CD | `gotk_reconcile_condition{type="Ready"}` per object, as a state timeline; suspended objects; reconcile duration | Prometheus (**`exported_namespace`**, Finding 6) |
      | Rollout | `kube_deployment_status_replicas_{updated,available}` for the `aerie` namespace; restarts; the running image from `kube_pod_container_info` | Prometheus |

      Deploy annotations from 1.3 cross all four rows, so a CI run, a
      reconcile and a pod roll line up on one time axis.

      *Exit:* `metrics.${DOMAIN}` answers "did my last push ship, and when"
      without opening GitHub, `flux`, or `kubectl`.

- [ ] **1.3 Deploy events as annotations.** A `Provider` type `grafana` and an
      `Alert` whose `eventSources` name the `apps`, `infra-config`,
      `data-schema` and `site-config` Kustomizations and the `aerie`
      HelmRelease, at `eventSeverity: info` so successes annotate too, using a
      Grafana service-account token. Every reconcile becomes a vertical line on every dashboard in the
      instance — which is worth as much on the Longhorn and CNPG dashboards as
      on this one, since "it started at 4:02" and "we deployed at 4:01" is the
      correlation those dashboards cannot currently make.

      *Exit:* an annotation appears within a minute of a reconcile and hovering
      it names the object and revision.

## Phase 2 — Kill the poll

Turns three waits into zero. Order matters: 2.3 is the biggest single win and
does not depend on 2.1 or 2.2 being finished.

- [ ] **2.1 A `Receiver` and its ingress.** `Receiver` type `generic-hmac` in
      `flux-system` naming `ImageRepository/aerie-api` and
      `ImageRepository/kiosk-files`, with a token from a new
      `delivery/receiver-token` parameter (mint with
      [`new-shared-secret.sh`](../../scripts/secrets/new-shared-secret.sh)). One
      `Ingress` for the `webhook-receiver` Service at
      `flux-webhook.${DOMAIN}`, alongside 5b.9's and 6b.9's — no `tls:` block,
      per the wildcard/TLSStore convention every other Ingress here follows.

      The path is `/hook/sha256(token+name+namespace)` and is published in
      `.status.webhookPath`; it is not knowable from the manifest, so the poking
      step reads it rather than hardcoding it.

      *Exit:* `curl` from a LAN host with a correct `X-Signature` returns 200 and
      the `ImageRepository`'s `lastScanResult` timestamp moves within seconds.
      A wrong signature returns 401.

- [ ] **2.2 Poke it from `publish.yml`.** A final job, `needs` the image jobs,
      `runs-on: [self-hosted, ...]` — this is why it must be self-hosted
      (Finding 4), and it also keeps the step off the Actions minutes budget.
      Signs the payload with the receiver token and POSTs.

      Per [the automation-first rule](README.md), this ships with the workflow,
      not as a documented `curl`.

      *Exit:* the `ImageRepository` scan timestamp moves within seconds of the
      `docker push` step finishing, instead of up to five minutes later.

- [ ] **2.3 Close the `apps` gap (Finding 3).** A second `Receiver`, type
      `generic-hmac`, naming `Kustomization/apps`; an `Alert` on
      `Kustomization/site-config` at `eventSeverity: info`; and a `Provider` type
      `generic-hmac` addressed at the in-cluster
      `http://webhook-receiver.flux-system/hook/<path>` and sharing the
      Receiver's token Secret. Entirely inside the cluster — no ingress, no
      external caller.

      When `site-config` writes a new `aerie-image-tags`, `apps` is told, instead
      of finding out up to ten minutes later. There is no feedback loop to
      guard against: `apps` reconciling does not produce a `site-config` event.

      **Interim, if this stalls:** drop `apps.interval` from `10m` to `2m` in
      [`apps.yaml`](../../deploy/cluster/apps.yaml). One line, ~80% of the win,
      and still polling — take it as a stopgap, not as the answer.

      *Exit:* an image-tag bump reaches a rolling pod without any ten-minute
      wall in the middle. Measure it: 2.4 is what makes that a number rather
      than an impression.

- [ ] **2.4 Re-measure the table at the top of this file** and replace it with
      observed numbers. The estimates above are the *reason* for this phase and
      make a poor record of its result.

## Phase 3 — Say something (no latency change)

Last by priority, not by cost — every item here is cheap, and 3.3 in particular
is an afternoon with no cluster change at all. Nothing in this phase touches the
reconciliation path. 3.1 reuses the `eventSources` list 1.3 already established;
if Phase 1 is done, this is a second `Alert` beside it.

- [ ] **3.1 A `Provider`/`Alert` pair posting commit statuses to GitHub.**
      `Provider` type `github`, `address` the Aerie repo, `secretRef` a Secret
      holding a fine-grained PAT scoped to this repo with `commit statuses:
      write` and nothing else. `Alert` with `eventSources` naming the `apps`,
      `infra-config`, `data-schema` and `site-config` Kustomizations and the
      `aerie` HelmRelease, at `eventSeverity: info` so successes post too.

      New parameter `github/status-token` in
      [`parameters.json`](../../scripts/secrets/parameters.json), phase `9`,
      `githubKind: secret`, landing in `flux-system` as `github-status/token`.

      *Exit:* pushing a change to `charts/aerie` puts a status check on the
      commit in GitHub within two minutes, and it goes red when the chart is
      broken. Read Finding 5 before trusting it for app code.

- [ ] **3.2 Deploy outcome as a Home Assistant notification.** A second `Alert`
      on the same event sources to a `Provider` type `generic` pointed at
      `http://${HA_HOST}:${HA_PORT}/api/webhook/${HA_ALERT_WEBHOOK_ID}` — the
      webhook the Alertmanager receiver already uses
      ([`kube-prometheus-stack.yaml`](../../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml)).

      Decide deliberately whether this shares
      `HA_ALERT_WEBHOOK_ID` with alerting or gets its own id. Sharing means a
      routine deploy and a 3am Longhorn failure arrive down the same automation;
      the phone cannot tell them apart, and the alert path is worth more than
      the deploy path. **Prefer a second webhook id** — one new optional key in
      [`cluster-config.json`](../../scripts/k3s/cluster-config.json),
      `HA_DEPLOY_WEBHOOK_ID`, defaulting to empty with the `Alert` omitted when
      unset.

      *Exit:* a phone notification naming the object and the revision, within a
      minute of a reconcile finishing, and a distinguishable one when it fails.

- [ ] **3.3 One command that answers "where is my commit."** A
      `scripts/k3s/Show-Delivery.ps1` alongside the `Test-*.ps1` family, taking
      the same `-IPAddress`/`-SshPrivateKey` shape: prints the local HEAD, the
      `flux-system` source revision, the `ImagePolicy`'s latest tag, the tag in
      `aerie-image-tags`, and the image actually running on each `aerie` pod, as
      five lines that either agree or visibly don't.

      *Exit:* run it after a push and the stuck hop is named by reading it, with
      no second command.

## Phase 4 — The green check, precisely

Optional, and the honest answer to "did *my* commit ship" that Finding 5 says a
commit status cannot give. Do it if Phase 3's status check proves too loose in
practice; skip it if it doesn't.

- [ ] **4.1 A `deploy.yml` gate.** `workflow_run` on *Build and publish
      containers*, `runs-on: [self-hosted, ...]`, one concurrency group. Same
      SSH-to-a-node shape as every `verify-*.yml`, using the
      `NODE_SSH_PRIVATE_KEY` secret that already exists — no new credential
      class, and nothing is applied from the runner. Drives the chain in order,
      each step blocking on the last:

      ```
      flux reconcile image repository aerie-api
      flux reconcile image update aerie
      flux reconcile source git aerie-site
      flux reconcile kustomization site-config --with-source
      flux reconcile kustomization apps --with-source
      kubectl -n aerie rollout status deploy/aerie-api
      ```

      then asserts the running image tag ends in the pushed short SHA. Green
      means the binary from this commit is serving traffic — not that a layer
      reconciled.

      **Be clear about what this is and is not.** It holds no kubeconfig and
      applies nothing; it says "reconcile now" and "tell me when you're done."
      Desired state still comes only from git, so
      [`delivery-architecture.md`](../delivery-architecture.md)'s argument for
      pull survives intact. Its cost is real and worth naming: an SSH key with
      root on a node is reachable from a workflow, which was already true of
      Provision 0–5 and is not made more true here.

      *Exit:* one check on the commit, in Actions, whose duration is the actual
      convergence time and whose failure names the hop that stalled.

- [ ] **4.2 Retire what 4.1 subsumes.** If the gate lands, 3.1's status check
      becomes redundant for `apps` specifically. Keep it for `infra-config` and
      `data-schema`, which no workflow gates.

## What this does not do

- **It does not gate the deploy.** Nothing here stops a bad manifest from
  reaching the cluster; `flux diff` and the dry-run checks from the cluster
  plan's 3b.14 are that, and they are separate work.
- **It does not consolidate the notification paths.** Alertmanager → HA and Kuma
  → HA already coexist by design, and 3.2 makes a third. Phase 9 of
  [the cluster plan](swarm/phase-9-productization.md) owns consolidating them;
  this plan should not pre-empt that decision by inventing a fourth convention.
- **It does not make rollback faster.** Reverting is still a commit, and
  everything above makes that commit land faster without making it easier to
  decide to write.
- **It adds three GitHub credentials to the cluster** (statuses:write,
  actions:read, and the receiver token). All are scoped to one repository, all
  point cluster → GitHub, and none of them grants anything inbound. Rotating
  them is re-running Provision 2.

## See also

- [`delivery-architecture.md`](../delivery-architecture.md) — the pull model, the
  timeline of a push, and the paragraph this plan is the answer to
- [`monitoring-alerting-architecture.md`](../monitoring-alerting-architecture.md)
  — the notification paths 3.2 joins
- [`phase-6-observability.md`](swarm/phase-6-observability.md) — how the Grafana,
  dashboard-ConfigMap and alerting plumbing this plan reuses got there
- [Flux Receivers](https://fluxcd.io/flux/components/notification/receivers/) and
  [Providers](https://fluxcd.io/flux/components/notification/providers/) — the
  upstream specs for every object in Phases 1–3
