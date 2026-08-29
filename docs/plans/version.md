# Aerie revision — one identity, from a commit to a wall tablet

**Status: designed, nothing built.** Eight findings, all read from the repo and
from the live cluster on **2026-08-28**. Seven phases; Phase 0 is the only one
every other phase depends on.

Every piece of the ecosystem should be able to say which commit it came from,
in the same words, wherever you happen to be looking — an HTTP response, a log
line in the aggregator, an API call, an admin screen. Today four different
components answer that question four different ways and two of them cannot
answer it at all.

The framing that decides most of this plan: **the revision is a property of the
build, not of the deployment.** It is stamped into the artifact at the moment
that artifact is produced and is thereafter read, never supplied. A pod that can
be told its own revision by its environment is a pod that can be told the wrong
one, and every story below is a story about trusting the answer.

Per [`ethos.md`](../ethos.md), the base domain is written `<domain>` here.
Node names and cluster readings are observations of one installation.

---

## The brief, as decisions

| Question | Answer |
|---|---|
| What is the value | The **full 40-character git sha** of the commit that built the artifact |
| What is it called | **`aerie-revision`**. Scoped to the ecosystem, not to this repo — as Aerie spans more repositories they all report into the same name, and `service` says which codebase a given sha belongs to |
| Ordering | A companion **`sequence`** = `git rev-list --count HEAD`. A sha has no order, and two of the four user stories are ordering questions. Already the established convention here — it is the kiosk's `versionCode` |
| Response header | `Aerie-Revision: <sha>` on every response. Identity only; ordering lives in the endpoint, not in a header on every byte |
| Request header | `Aerie-Client-Revision` from a web app, `Aerie-Shell-Revision` from the native kiosk shell — a distinct header, per the brief, because they are distinct artifacts |
| No `X-` prefix | RFC 6648 deprecated it in 2012, and the repo has no existing `X-Aerie-*` convention to match except `X-Aerie-Grant`, which is Traefik's |
| One log field | **`aerie_revision`**, top level, one path for every source. Never nested, never per-source |
| Whose revision, on a proxied line | **The emitter's.** `aerie_revision` always means "the revision of whatever `service` says produced this line". The relay's own goes in `aerie_relay_revision`, present only on proxied lines |
| Third-party workloads | `aerie_revision` **absent**. A field that means "our commit" on one row and "upstream chart version" on the next is the ambiguity this plan exists to remove. `NOT _exists_:aerie_revision` reads as "not ours" |
| Relationship to `AppVersionService` | **Coexist.** Different questions — see finding 3, which is also the trap this plan has to route around |
| Flux's reconciled revision | Read on demand by `GET /api/aerie-revision`, for an admin caller. Deferred to Phase 6, because it is the only piece needing new cluster access |
| Scope | Aerie.Api + the six SPAs, the kiosk Android shell, the four other built images, and what Flux has reconciled |

### The names, in one place

| Surface | Name | Value |
|---|---|---|
| HTTP response, every request | `Aerie-Revision` | full sha |
| HTTP request, web app → API | `Aerie-Client-Revision` | full sha |
| HTTP request, kiosk shell | `Aerie-Shell-Revision` | full sha of the APK's commit |
| Endpoint | `GET /api/aerie-revision` | `{ revision, sequence, builtAt, cluster? }` |
| OpenSearch, every source | `aerie_revision` | full sha, or absent |
| OpenSearch, ordering | `aerie_sequence` | integer, or absent |
| OpenSearch, proxied lines only | `aerie_relay_revision` | full sha of the relaying API |
| SPA, in the served document | `<meta name="aerie-revision">` | full sha — **not** a bundled constant, see finding 3 |
| .NET, in the binary | `AssemblyInformationalVersion` | `<version>+<sha>`, via `SourceRevisionId` |
| Android, in the APK | `BuildConfig.AERIE_REVISION` | full sha; `versionCode` is already the sequence |

`/api/app-version/{app}` keeps its name and its meaning. It is not this.

---

## Findings

Eight. The first three decide the shape of the work, the next two decide the
mechanism, and the last three are each a piece of the job that turns out to be
already done.

### 1. Nothing in the running system can name its own commit

[`Dockerfile.api`](../../src/Aerie.Api/Dockerfile.api) never receives the sha,
and [`.dockerignore`](../../.dockerignore) excludes `.git`, so it could not
derive one even if it tried. `publish.yml` knows `github.sha` and spends it
entirely on **image tags** — the value reaches the registry and stops there.

The consequence is narrower than it sounds and worse than it sounds: the sha is
visible to anyone holding a kubeconfig and invisible to the API itself, to every
SPA, to every log line the API writes, and to every response it sends. A
build-time injection is therefore the first step of every other step.

### 2. The short sha is already in every log record

Read live from OpenSearch on 2026-08-28 — an actual `aerie-logs-*` document:

```text
kubernetes.container_image: ghcr.io/…/aerie-api:20260828025358-b465267
kubernetes.container_hash:  ghcr.io/…/aerie-api@sha256:8fbda8a4…
kubernetes.labels:          { app.kubernetes.io/component: api, … }
```

fluent-bit's `kubernetes` filter already attaches the running image reference,
its digest, and the pod's labels to **every record from every pod**, ours and
third-party alike. The tag format `<14-digit timestamp>-<short sha>` is Aerie's
own, set by `docker/metadata-action` in
[`publish.yml`](../../.github/workflows/publish.yml).

Two things follow, and both save a phase's worth of work:

- A **tag-shape match in Lua** derives `aerie_revision` for every image Aerie
  builds, with no application code, no chart change, and no new pod label — and
  it covers the containers that could never self-report anyway: `aerie-db` is
  Postgres, `aerie-backup` is a shell script, `aerie-kuma-provision` is Python.
  Matching the *tag shape* rather than a registry prefix also keeps this
  portable, which a `${IMAGE_REGISTRY}` comparison would not be.
- The separate field for "what image is this pod running" — asked about as
  `image_aerie_revision` — **already exists**, as `kubernetes.container_image`,
  populated for every workload including third-party ones. Adding a second
  field would duplicate it. *This is a call made on evidence after the question
  was asked; overrule it if the nesting under `kubernetes.` is the objection,
  in which case the fix is a one-line Lua lift to a top-level alias, not a new
  source of truth.*

The tag carries only 7 hex characters. Phase 0.4 widens it to the full sha; the
existing `ImagePolicy` pattern `^(?P<ts>\d{14})-[0-9a-f]+$` already accepts any
hex length, so nothing downstream needs editing.

### 3. `AppVersionService` answers a different question, and the obvious implementation breaks it

[`AppVersionService.cs`](../../src/Aerie.Api/Services/AppVersionService.cs)
derives a frontend's identity from its **content-hashed asset filenames**, and
its header is explicit about why: a backend-only deploy — by far the common case
— must leave every app's token byte-identical, so the wall tablets do not reload
for a change they cannot see.

Now consider the obvious way to give a SPA its revision: a Vite `define` that
inlines `__AERIE_REVISION__` into the bundle. The bundle's content then changes
on **every commit**, its hash changes, and `AppVersionService` reports drift
every time — turning every backend-only deploy into a full-tablet reload. The
one property that module was written to preserve, destroyed by the feature meant
to sit beside it.

So: **the SPA's revision goes in `index.html`, as a meta tag, and never into a
hashed asset.** `index.html` is already served `no-cache`
([`Program.cs`](../../src/Aerie.Api/Program.cs)), is already the file
`AppVersionService` reads, and adding a `<meta>` to it perturbs neither the
server's extraction (which matches `/apps/<app>/assets/…` URLs) nor the client's
(which reads `script[src]`/`link[href]`). Vite's `%VITE_*%` substitution in
`index.html` is the supported mechanism.

This finding is the reason the two concepts coexist cleanly rather than
uneasily: they now live in the same file and cannot collide, because one is the
asset URLs and the other is a meta tag.

### 4. A sha has no order, and half the brief is an ordering question

> *roll out a breaking change incrementally … monitoring the version of the
> system in production to know when it is safe*
>
> *ensures that the web app is **behind** the api and not ahead of it*

Neither is answerable from two shas. Nothing at runtime has a clone of the
repository to ask, and nothing should.

The repo already solved this once: the kiosk's `versionCode` is
`git rev-list --count HEAD`, monotonic on a linear `main`, and `UpdateManager`
compares it numerically. Reusing it makes the two mechanisms one mechanism.

It carries a trap the kiosk job already documents in a load-bearing comment:
`rev-list --count` on a default shallow checkout returns **1**. The API build
job checks out shallow today, so Phase 0 must set `fetch-depth: 0` on it — and
the failure if it doesn't is silent, a sequence of 1 forever and a drift check
that never fires.

### 5. `service_tag.lua` already implements exactly the precedence this needs

[`service_tag.lua`](../../deploy/cluster/observability/controllers/fluent-bit/service_tag.lua)
sets `service` from a **container-level default** (`kubernetes.container_name`),
then lets an **application-level override** (`State.Service`) win. That is
precisely the shape `aerie_revision` requires:

- default from `kubernetes.container_image`'s tag → covers all five built images
- overridden by `State.AerieRevision` → makes a proxied UI log line carry the
  *browser's* revision, which is the "emitter owns the field" decision

And it is why the decision is implementable at all. A live document confirms the
problem it solves: a `service: dashboard` line carries
`kubernetes.container_image: …/aerie-api:…` — the **relay's** image, on a line
the dashboard emitted. Without the override, every UI log line would be tagged
with the API's revision and the stale-device story would be unanswerable.

Same file, same function, one more pair of branches. Its header is emphatic
about precedence; this plan does not change the existing rules, it adds a
parallel set beside them.

### 6. `clientLogger.ts` exists six times

`admin`, `auth`, `dashboard`, `docs`, `family`, `modeler` each carry their own
copy, diverged in size (4.6–6.0 KB) but not in structure, alongside a
byte-identical `deviceMetadata.ts` in all six. There is no shared frontend
package and no shared `fetch` wrapper — `family/src/lib/http.ts` is the only
wrapper anywhere, and only `family` uses it.

Every SPA-side change in this plan therefore lands six times, or lands once and
introduces the shared module that does not exist yet. Phase 2 takes the second
option **for the new code only** — a new `aerieRevision.ts` copied per app is
six future divergences — and deliberately does not refactor the existing six
loggers, which is its own change with its own risk and no relationship to this
one.

The revision reaches outbound requests through a **`fetch` interceptor installed
at bootstrap**, not through edits to call sites. There are too many call sites,
they are unaudited, and a call site added next month would silently lack the
header.

### 7. The kiosk shell already has a git version and no way to tell the page

`versionCode` = commit count, `versionName` = `<count>-<short sha>`, both
supplied by CI, both already used by `UpdateManager` for silent self-update. The
sequence half of finding 4 is done for this artifact.

What is missing is a channel. GeckoView's `loadUri` additional headers apply to
the initial document request only, never to the `fetch` calls the page then
makes, so the shell cannot inject a header into the dashboard's API traffic.
The page has to be told, and then send it itself.

`MainActivity` controls the URL it loads, which makes a query parameter the
smallest sufficient bridge — read once at startup, held in `sessionStorage`,
sent thereafter. It is client-supplied and therefore spoofable, at exactly the
trust level `UiLogsController`'s remarks already establish for every field on
`UiLogEntry`.

### 8. Flux already publishes full shas, for two repositories

Read live, 2026-08-28:

```text
GitRepository  flux-system   main@sha1:2d868dcb2fb27834c99fbb1c3805c6e7f882f170
GitRepository  aerie-site    main@sha1:4ce5fc691def1f1aad8f89b36bba495756da3c23
Kustomization  apps          lastAppliedRevision: main@sha1:b465267d5809…
Kustomization  observability-config                  main@sha1:b465267d5809…
```

Full 40-character shas, already, with no work at all — and the reading is itself
a demonstration of the thing this plan is for: at that moment the source was at
`2d868dc` while most Kustomizations were still applied at `b465267`, one commit
behind, mid-reconcile. That gap is invisible today to everything except a
kubeconfig.

Two repositories, so the endpoint reports a list rather than a value: this repo
under `flux-system`, and the per-installation site repo under `aerie-site`.

---

## Phases

### Phase 0 — the sha reaches every artifact `[ ]`

The dependency of everything else. Nothing here is observable on its own, which
is why 0.7 is a gate rather than a hope.

- [ ] **0.1** — `publish.yml`: `fetch-depth: 0` on the API build job, and a
      `version` step emitting `sha` (full) and `sequence`
      (`git rev-list --count HEAD`). Finding 4's trap; copy the kiosk job's
      comment, it explains the failure better than a fresh one would.
- [ ] **0.2** — `Dockerfile.api`: `ARG AERIE_REVISION` / `ARG AERIE_SEQUENCE`
      in the six SPA stages and the SDK stage. The publish becomes
      `dotnet publish … -p:SourceRevisionId=$AERIE_REVISION`, which appends
      `+<sha>` to `AssemblyInformationalVersion` — the .NET-native mechanism, in
      the binary rather than in the environment, per this plan's framing.
      `AERIE_SEQUENCE` needs its own property; a `<Version>` suffix or an
      `AssemblyMetadata` item, whichever reads more plainly.
- [ ] **0.3** — Each SPA's `index.html` gets
      `<meta name="aerie-revision" content="%VITE_AERIE_REVISION%">` and a
      sequence sibling, with `VITE_AERIE_REVISION` supplied to `npm run build`.
      **Finding 3 is the whole point of this step** — a `define` here would look
      identical and quietly cost every tablet a reload per deploy. Leave a
      comment saying so, next to the meta tag.
- [ ] **0.4** — Tag format `{{date 'YYYYMMDDHHmmss'}}-<full sha>` for all five
      images, replacing `{{sha}}`'s 7 characters. `ImagePolicy`'s
      `filterTags` already accepts it (finding 2); confirm rather than assume,
      it is one `kubectl get imagepolicy -o yaml` after the first push.
- [ ] **0.5** — Kiosk: `-PkioskRevision=${{ github.sha }}` →
      `buildConfigField("String", "AERIE_REVISION", …)`. `versionCode` is
      already the sequence and does not change. `version.json` gains
      `revision`, so the update check can log what it is moving to.
- [ ] **0.6** — Local builds: absent the build arg, the value is the literal
      `dev` and the sequence `0`, everywhere. Not a git call at build time —
      `make build` must not require a repository, and a dev machine's sha in a
      dev machine's artifact answers nobody's question.
- [ ] **0.7** — **Gate.** `docker build` the API image locally with
      `--build-arg AERIE_REVISION=<a real sha>`, run it, and read the sha back
      out of the binary and out of all six `index.html` files. Then push and
      confirm the published tag carries 40 hex characters.

*Exit: a built artifact can be asked what commit produced it, by six different
routes, and answers the same thing six times.*

### Phase 1 — the API says so `[ ]`

- [ ] **1.1** — `IAerieRevision` singleton: `Revision`, `Sequence`, `BuiltAt`,
      read once from the assembly attribute at startup. A singleton because it
      is immutable for the process's whole life and re-parsing it per request
      is work with no possible new answer.
- [ ] **1.2** — Middleware setting `Aerie-Revision` on every response.
      Placement: **before** `AuthMiddleware`, so a 401 and a 302 carry it too —
      "which replica refused me" is a question worth being able to answer.
      Confirm it survives Traefik rather than assuming it; a response header is
      passed through by default, and this takes one `curl -I` to know.
- [ ] **1.3** — `GET /api/aerie-revision` returning
      `{ revision, sequence, builtAt }`, `Cache-Control: no-store` for the same
      reason `AppVersionController` sets it. Exempt from the auth wall — it
      names a commit, which is about to be public in a GitHub repository, and
      an unauthenticated client needs it to know it should re-authenticate
      against a newer build.
- [ ] **1.4** — Unit tests: the assembly-attribute parse (including the `dev`
      fallback and a malformed attribute, which must degrade to `dev` rather
      than throw at startup), and the middleware's presence on a refused
      request.
- [ ] **1.5** — **Gate.** `curl -I https://<domain>/` shows `Aerie-Revision`;
      `curl https://<domain>/api/aerie-revision` matches the sha in the running
      pod's image tag.

*Exit: `curl -sI https://<domain>/ | grep Aerie-Revision` is how you learn what
production is running.*

### Phase 2 — the web apps say so `[ ]`

- [ ] **2.1** — `aerieRevision.ts`, written **once** and shared, not copied six
      times (finding 6). Reads the meta tags, exports `{ revision, sequence }`.
      Where "shared" lives is the open question — a workspace package is the
      right answer and the largest change; a path alias into a common directory
      is the smaller one. Decide it here, in this step, rather than letting six
      copies happen by default while the question stays open.
- [ ] **2.2** — A `fetch` interceptor installed at each app's bootstrap, adding
      `Aerie-Client-Revision` to **same-origin requests only**. The
      same-origin restriction is not caution, it is correctness: a cross-origin
      request with a custom header triggers a CORS preflight, which turns one
      round trip into two on a request that was working fine.
- [ ] **2.3** — `clientLogger` includes `revision` and `sequence` on every
      entry. `UiLogEntry` gains the matching nullable fields —
      nullable because an old bundle in a browser that has not reloaded yet is
      the normal case for months after this ships, and it is precisely the case
      the administrator story wants to see.
- [ ] **2.4** — All six apps wired. `docs` and `modeler` included: an app too
      minor to version is an app that will be the one confusing outlier during
      an incident.
- [ ] **2.5** — **Gate.** Load each app; confirm the request header on a network
      call and the field on a log line reaching OpenSearch. Then confirm the
      thing finding 3 is about: deploy a **backend-only** change and check that
      `/api/app-version/dashboard` is byte-identical across it. If it moved,
      0.3 was implemented as a `define` and the tablets are now reloading on
      every deploy.

*Exit: a log line from a browser names the bundle that browser is running, and
2.5's second half proves the kiosks did not become chattier.*

### Phase 3 — the kiosk shell says so, separately `[ ]`

- [ ] **3.1** — `MainActivity` appends `?aerieShellRevision=<BuildConfig…>` to
      the URL it loads.
- [ ] **3.2** — The dashboard reads it once at startup into `sessionStorage`
      and strips it from the visible URL, then sends `Aerie-Shell-Revision`
      alongside `Aerie-Client-Revision` on every same-origin request. Two
      headers, per the brief: the APK and the bundle are separate artifacts
      that drift independently, and collapsing them loses the drift.
- [ ] **3.3** — `KioskLogger` sends `revision`/`sequence` for the **shell**;
      its `app` is already `kiosk-android`, so `service` disambiguates it from
      the dashboard's own lines with no further work (finding 5).
- [ ] **3.4** — `UpdateManager` logs the revision it is updating *from* and
      *to*, so a self-update is one searchable line rather than an inference
      from a restart.
- [ ] **3.5** — **Gate.** On a tablet: a dashboard log line carries the bundle's
      revision, a `kiosk-android` line carries the APK's, and they differ.
      *They must differ* — if they match, 3.2 is reading the wrong value.

*Exit: the administrator story is answerable — one OpenSearch query, grouped by
`aerie_revision`, listing every `deviceId` on an old shell.*

### Phase 4 — one field in OpenSearch `[ ]`

- [ ] **4.1** — `service_tag.lua` gains `set_aerie_revision`, in the shape
      finding 5 describes: default from the `<14 digits>-<40 hex>` tag in
      `kubernetes.container_image`, overridden by `State.AerieRevision`. Match
      on tag **shape**, not registry prefix — the registry is a per-installation
      parameter and the tag format is structural.
- [ ] **4.2** — `aerie_sequence` by the same rules. It has no container-level
      default (the tag carries a build timestamp, not a commit count, and
      quietly mixing the two scales would make the field a lie), so it is
      present only where an application emits it. Absent is correct.
- [ ] **4.3** — `aerie_relay_revision`, set only when `State.AerieRelayRevision`
      is present — that is, only on lines through `/api/ui-logs`.
- [ ] **4.4** — `UiLogsController` logs the client's revision as
      `State.AerieRevision` and its own as `State.AerieRelayRevision`, plus the
      **actor**: `HttpContext.GetAuthGrant()` gives `Id` and `Label`. Log the
      **id**, not the label —
      [`AuthContextExtensions`](../../src/Aerie.Api/Common/AuthMiddleware.cs)'s
      remarks are explicit that a label is free text an administrator typed,
      and free text is not what you want flowing into a log field people will
      later filter on.
- [ ] **4.5** — Refresh the Dashboards index pattern
      ([`create-index-pattern.sh`](../../deploy/cluster/observability/config/provisioning/create-index-pattern.sh))
      so the new fields are queryable rather than merely present.
- [ ] **4.6** — A saved search: revisions in the fleet, last 24h, grouped by
      `service` and `aerie_revision`. This is the SRE story, and it is a saved
      object rather than a documented query because a query nobody saved is a
      query nobody runs.
- [ ] **4.7** — **Gate.** In OpenSearch: an `aerie-api` line carries
      `aerie_revision` from its tag with no application change; a `dashboard`
      line carries the browser's and an `aerie_relay_revision` that differs; a
      `traefik` line carries neither and still carries
      `kubernetes.container_image`.

*Exit: `aerie_revision` is one field, one meaning, one path — and
`NOT _exists_:aerie_revision` is a working definition of "not ours".*

### Phase 5 — the drift answer `[ ]`

The self-update story, built beside `AppVersionService` rather than on top of
it. Read finding 3 before starting.

- [ ] **5.1** — `GET /api/aerie-revision` accepts the client's revision and
      sequence and answers `behind` / `current` / `ahead`. **`ahead` is the
      case that earns this step**: mid-rolling-deploy, a browser loaded from a
      new replica can ask an old one, and a client that reloads on any
      difference will thrash between two answers until the rollout finishes.
      Only `behind` may trigger anything.
- [ ] **5.2** — A client-side check on the existing poll cadence, logging drift
      at `warn`. **Logging only, in this step.** The measurement comes before
      the action, and a week of seeing how often `ahead` actually occurs is
      what tells you whether 5.3 is safe.
- [ ] **5.3** — *Gated on 5.2's week.* A `behind` verdict, sustained across two
      consecutive checks, triggers the same reload path `AppVersionService`
      drift already uses. Two consecutive checks because one is a race with a
      rollout, and the reload path is shared because two ways to reload one page
      is one way too many.
- [ ] **5.4** — **Gate.** Deploy twice in quick succession with a tab open and
      confirm no reload loop. Then confirm the architect story end to end: with
      two replicas deliberately on different images, `/api/aerie-revision`
      returns both shas across repeated calls, and the fleet query in 4.6 shows
      the split.

*Exit: the architect story is answerable — "are all replicas and all clients on
the new revision yet" has a screen, and rolling out a breaking change stops
being a guess.*

### Phase 6 — what the cluster has reconciled `[ ]`

The only phase needing access the API does not have today. Separated for that
reason, and can ship long after Phase 5.

- [ ] **6.1** — A ServiceAccount with a `ClusterRole` scoped to `get`/`list` on
      `gitrepositories` and `kustomizations` in `source.toolkit.fluxcd.io` /
      `kustomize.toolkit.fluxcd.io`, and **nothing else**. The API has no
      Kubernetes access at all right now; this is the step that changes that,
      and it is worth the narrowest possible grant.
- [ ] **6.2** — `/api/aerie-revision` gains a `cluster` block for an
      authenticated admin caller: per source, the artifact revision and the
      `lastAppliedRevision` of each Kustomization reading from it, parsed out of
      `main@sha1:<sha>` (finding 8). Both repositories.
- [ ] **6.3** — Cached, short TTL. This is a Kubernetes API call behind an HTTP
      endpoint, and an endpoint an admin screen polls must not become a way to
      generate load against the control plane.
- [ ] **6.4** — Absent or degraded when Flux is unreachable — an API that fails
      its own version endpoint because the cluster is unhealthy has failed at
      the moment it was most needed.
- [ ] **6.5** — An admin app screen: every revision in the system on one page,
      app tier and cluster, with the gap called out where it exists.
- [ ] **6.6** — **Gate.** Push a commit, then watch the screen show the source
      ahead of `lastAppliedRevision` and converge — the exact gap finding 8
      caught by accident, now visible on purpose.

*Exit: the SRE story is answerable without a kubeconfig.*

---

## What is deliberately not here

- **A refactor of the six `clientLogger.ts` copies.** Real, unrelated, and its
  own risk. Phase 2 introduces one shared module for new code and leaves the
  existing six alone.
- **Replacing `AppVersionService`.** Finding 3. The two answer different
  questions and the asset-hash mechanism is the better answer to its own.
- **A revision for third-party workloads.** `kubernetes.container_image` is
  already there and already correct.
- **Semantic versioning, release tags, changelogs.** A revision is a commit.
  Anything that maps commits to human-meaningful releases is a different plan
  that could sit on top of this one.

## The one decision most worth overruling

**Ordering by commit count.** It is right for a linear `main` and this
repository has one. It is wrong the first time history is not linear — a merge
commit's count is not a meaningful position, and a second repository's counts
are not comparable to this one's at all. The alternative is the build timestamp,
which is already in every image tag (finding 2), orders across repositories for
free, and costs the numeric simplicity `UpdateManager` currently depends on.

Commit count wins here because it is what the kiosk already does and unifying
two mechanisms is worth more than the generality — but if Aerie spans repos
sooner than expected, this is the decision that will need to be made again.
