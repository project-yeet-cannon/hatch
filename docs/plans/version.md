# Aerie revision — one identity, from a commit to a wall tablet

**Status: built, except Phase 3 and the two steps that are gated on watching.**
Phases 0, 1, 2, 4, 5.1-5.2 and 6 landed on **2026-08-29**. Phase 3 (the kiosk
shell) is untouched on purpose - another session was working in `apps/kiosk` -
and 0.5 goes with it, since it is a change to that app's Gradle build.

What is left, and why each is left:

- **Phase 3 and 0.5** — the kiosk shell. Waiting on `apps/kiosk`.
- **5.3** — acting on a `behind` verdict. Gated on a week of reading 5.2's
  lines, as written. The number nobody has is how often `Ahead` really occurs
  during a rollout, and 5.2 is now producing it.
- **The gates that need production** — 1.5, 2.5's second half, 4.7, 5.4, 6.6.
  Each needs the image deployed and reconciled; every one of them has a
  local-equivalent that has passed (noted per phase below).

Three things the implementation found that the plan had wrong or missing, all
recorded in place: the SDK already stamps the sha locally (finding 9), the
four `:latest` images cannot be reached by Phase 4 at all (finding 10), and
5.2 belongs on the server rather than in the six clients (finding 11).

Eleven findings; the first eight were read from the repo and the live cluster
on **2026-08-28**, the last three came out of building it. Seven phases;
Phase 0 is the only one every other phase depends on.

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

Twelve. The first three decide the shape of the work, the next two decide the
mechanism, and 6-8 are each a piece of the job that turns out to be already
done. **9-12 came out of building it** and are the ones that changed the plan;
12 is a latent bug the work happened to trip over.

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

### 9. The SDK already stamps the sha - just not where it matters

Found by a test written to assert the opposite. The .NET SDK's built-in
SourceLink sets `SourceRevisionId` from git without being asked, so any build
made inside a checkout carries the **full** sha in
`AssemblyInformationalVersion` already. `make run` reports the commit it is
actually running, for free.

Where it does not fire is inside the container - `.dockerignore` excludes
`.git` - which is exactly the case the build arg exists for. Both paths now
land in the same reader, and the invariant worth asserting turned out to be
"either a full sha or `dev`, never a third thing" rather than anything about
which one a given build gets.

The related trap: the SDK is happy to stamp a **short** sha if something sets
`SourceRevisionId` to one, and the image tags carried exactly that until 0.4.
`ParseRevision` rejects anything that isn't 40 hex characters, so a
half-finished migration reads as `dev` instead of as a value that silently
never matches anything.

### 10. Phase 4 cannot reach the four images that deploy at `:latest`

The plan said the Lua tag-parse "covers all five built images". It covers two.
`aerie-db`, `aerie-backup` and `aerie-kuma-provision` are deployed as
`${IMAGE_REGISTRY}/<name>:latest` from raw manifests, and only `aerie-api` and
`aerie-kiosk-files` are under Flux image automation and therefore pinned to a
tag with a revision in it. A moving tag carries no revision, so those three get
no `aerie_revision` field.

Not worked around, for a stated reason: pinning them means new ImageRepository
and ImagePolicy objects **plus** `$imagepolicy` markers in the per-installation
site repo, which is not in this tree. The Lua accepts a bare 40-hex tag as well
as the stamped form, so the moment any of them is pinned it resolves with no
further change.

What they do have, already, is `org.opencontainers.image.revision` as an OCI
label - `docker/metadata-action` emits it by default and `publish.yml` passes
its labels through - so the artifacts do record their commit. It just isn't
reachable from a log line, because pod-level metadata is what fluent-bit sees
and an image label is not.

### 11. The drift measurement belongs on the server, not in six clients

5.2 was written as a client-side poll. Every web app's fetch wrapper already
sends `Aerie-Client-Revision` on **every** request, which makes the API a
strictly better place to watch from: it sees all traffic from all apps rather
than one probe per app per interval, it needs no code in any frontend, and it
cannot itself be the thing that breaks a page.

It needs a throttle to be usable. A wall tablet makes a request every few
seconds, so one stale tablet would otherwise be the loudest thing in the index
and would bury the signal it is producing. Once per distinct client revision
per five minutes, in a bounded map - the key is a client-supplied header, and
an unbounded dictionary keyed on one of those is a memory leak with an open
door.

### 12. A committed build artifact had been shadowing two apps' Vite config

`admin` and `docs` built cleanly and produced no stamp at all, while the other
four worked. Both carry a **committed `vite.config.js`** — `tsc -b` output from
their `composite` project, checked in at some point — and Vite's config lookup
tries `vite.config.js` *before* `vite.config.ts`.

So those two apps had not been building from the TypeScript config anyone
edits. Any change to `admin/vite.config.ts` or `docs/vite.config.ts` since
those files were committed would have built successfully and done nothing,
which is the worst available failure mode: no error, no warning, and a config
file that reads correctly.

It surfaced here only because this plan is the first thing in a while to edit
those two configs *and* have a visible consequence when the edit is dropped —
and it briefly looked like a bug in the plugin rather than in what was loading
it. The artifacts are gone (a concurrent commit removed them independently, so
two people found this the same afternoon), the `outDir` from 2.1 stops `tsc -b`
regenerating them in place, and `.gitignore` now names them so they cannot
return by hand.

---


## Phase 0 — the sha reaches every artifact

The dependency of everything else. Nothing here is observable on its own, which
is why 0.7 is a gate rather than a hope.

- [x] **0.1** — `publish.yml`: `fetch-depth: 0` on the API build job, and a
      `version` step emitting `sha` (full) and `sequence`
      (`git rev-list --count HEAD`). Finding 4's trap; copy the kiosk job's
      comment, it explains the failure better than a fresh one would.
- [x] **0.2** — `Dockerfile.api`: `ARG AERIE_REVISION` / `ARG AERIE_SEQUENCE`
      in the six SPA stages and the SDK stage. The publish becomes
      `dotnet publish … -p:SourceRevisionId=$AERIE_REVISION`, which appends
      `+<sha>` to `AssemblyInformationalVersion` — the .NET-native mechanism, in
      the binary rather than in the environment, per this plan's framing.
      `AERIE_SEQUENCE` needs its own property; a `<Version>` suffix or an
      `AssemblyMetadata` item, whichever reads more plainly.
- [x] **0.3** — Each SPA's `index.html` gets
      `<meta name="aerie-revision" content="%VITE_AERIE_REVISION%">` and a
      sequence sibling, with `VITE_AERIE_REVISION` supplied to `npm run build`.
      **Finding 3 is the whole point of this step** — a `define` here would look
      identical and quietly cost every tablet a reload per deploy. Leave a
      comment saying so, next to the meta tag.
- [x] **0.4** — Tag format `{{date 'YYYYMMDDHHmmss'}}-<full sha>` for all five
      images, replacing `{{sha}}`'s 7 characters. `ImagePolicy`'s
      `filterTags` already accepts it (finding 2); confirm rather than assume,
      it is one `kubectl get imagepolicy -o yaml` after the first push.
- [ ] **0.5** — *Deferred with Phase 3 — a change to `apps/kiosk`'s Gradle
      build, and that app is being worked on elsewhere.* Kiosk:
      `-PkioskRevision=${{ github.sha }}` →
      `buildConfigField("String", "AERIE_REVISION", …)`. `versionCode` is
      already the sequence and does not change. `version.json` gains
      `revision`, so the update check can log what it is moving to.
- [x] **0.6** — Local builds: absent the build arg, the value is the literal
      `dev` and the sequence `0`, everywhere. Not a git call at build time —
      `make build` must not require a repository, and a dev machine's sha in a
      dev machine's artifact answers nobody's question.
- [x] **0.7** — **Gate — passed locally.** Built with
      `-p:SourceRevisionId=<sha> -p:AerieSequence=4127` and read both back out
      of the assembly. All six SPAs stamp their `index.html`. **And the check
      finding 3 is really about:** two builds an arbitrary revision apart emit
      byte-identical asset filenames (`index-DGKf0NkE.js`,
      `index-DraR7aiR.css`), so `AppVersionService`'s token does not move and
      no tablet reloads for a backend-only deploy.
      *Still to confirm in production:* that the published tag carries 40 hex
      characters and that `ImagePolicy` still selects it.

*Exit: a built artifact can be asked what commit produced it, by six different
routes, and answers the same thing six times.*

## Phase 1 — the API says so

- [x] **1.1** — `IAerieRevision` singleton: `Revision`, `Sequence`, `BuiltAt`,
      read once from the assembly attribute at startup. A singleton because it
      is immutable for the process's whole life and re-parsing it per request
      is work with no possible new answer.
- [x] **1.2** — Middleware setting `Aerie-Revision` on every response.
      Placement: **before** `AuthMiddleware`, so a 401 and a 302 carry it too —
      "which replica refused me" is a question worth being able to answer.
      Confirm it survives Traefik rather than assuming it; a response header is
      passed through by default, and this takes one `curl -I` to know.
- [x] **1.3** — `GET /api/aerie-revision` returning
      `{ revision, sequence, builtAt }`, `Cache-Control: no-store` for the same
      reason `AppVersionController` sets it. Exempt from the auth wall — it
      names a commit, which is about to be public in a GitHub repository, and
      an unauthenticated client needs it to know it should re-authenticate
      against a newer build.
- [x] **1.4** — Unit tests: the assembly-attribute parse (including the `dev`
      fallback and a malformed attribute, which must degrade to `dev` rather
      than throw at startup), and the middleware's presence on a refused
      request.
- [ ] **1.5** — **Gate — passed locally, pending in production.** Against a
      local run: `Aerie-Revision` on the response, the endpoint reporting its
      own stamp, and the drift verdict correct in all five cases over real HTTP
      (`Behind`, `Ahead`, `Current`, and `Unknown` both for divergent history
      and for an unstamped build). Ten identical stale requests produced one log
      line, so the throttle holds. *Production:* `curl -I https://<domain>/`
      shows the header, and the endpoint matches the running pod's image tag.

*Exit: `curl -sI https://<domain>/ | grep Aerie-Revision` is how you learn what
production is running.*

## Phase 2 — the web apps say so

- [x] **2.1** — **Resolved as: no runtime module at all.** A workspace package
      would have meant one root `package.json`, one lockfile and a Dockerfile
      rewrite; a path alias would have fought two different `moduleResolution`
      settings and Vite's dev-server `fs.allow`. Instead the *build-time* plugin
      owns both the value and the behaviour, and emits them into `index.html` —
      so there is nothing per-app to keep in step. What the six apps share is
      one `.mts` file above them, imported only by their Vite configs, which are
      Node-side and so free of all of the above.
      *Two tsconfig generations live here* — `admin`/`docs` on `bundler`, the
      rest on `nodenext`. `.mts` imported as `.mjs` is the one spelling both
      accept. The two legacy `composite` projects also needed an `outDir`, or
      `tsc -b` drops a `.mjs` next to the source that Vite would resolve *ahead*
      of it — see finding 12, which is that same hazard already sprung.
- [x] **2.2** — Installed by the plugin as an inline `head-prepend` script, not
      at each app's bootstrap: that puts it in front of the ui-logs ping
      `index.html` itself fires, so even the first request of a page load
      carries the header. **Same-origin only**, and that is correctness rather
      than caution — a custom header on a cross-origin request triggers a CORS
      preflight, turning one round trip into two on a request that was working
      fine.
- [x] **2.3** — `clientLogger` includes `revision` and `sequence` on every
      entry. `UiLogEntry` gains the matching nullable fields —
      nullable because an old bundle in a browser that has not reloaded yet is
      the normal case for months after this ships, and it is precisely the case
      the administrator story wants to see.
- [x] **2.4** — All six apps wired. `docs` and `modeler` included: an app too
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

## Phase 3 — the kiosk shell says so, separately

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

## Phase 4 — one field in OpenSearch

- [x] **4.1** — `service_tag.lua` gains `set_aerie_revision`, in the shape
      finding 5 describes: default from the `<14 digits>-<40 hex>` tag in
      `kubernetes.container_image`, overridden by `State.AerieRevision`. Match
      on tag **shape**, not registry prefix — the registry is a per-installation
      parameter and the tag format is structural.
- [x] **4.2** — `aerie_sequence` by the same rules. It has no container-level
      default (the tag carries a build timestamp, not a commit count, and
      quietly mixing the two scales would make the field a lie), so it is
      present only where an application emits it. Absent is correct.
- [x] **4.3** — `aerie_relay_revision`, set only when `State.AerieRelayRevision`
      is present — that is, only on lines through `/api/ui-logs`.
- [x] **4.4** — `UiLogsController` logs the client's revision as
      `State.AerieRevision` and its own as `State.AerieRelayRevision`, plus the
      **actor**: `HttpContext.GetAuthGrant()` gives `Id` and `Label`. Log the
      **id**, not the label —
      [`AuthContextExtensions`](../../src/Aerie.Api/Common/AuthMiddleware.cs)'s
      remarks are explicit that a label is free text an administrator typed,
      and free text is not what you want flowing into a log field people will
      later filter on.
- [x] **4.5** — **Already done, by other work.**
      [`create-index-pattern.sh`](../../deploy/cluster/observability/config/provisioning/create-index-pattern.sh)
      re-fetches the live field list from the cluster on every provisioning run
      and re-posts it with `overwrite=true`, so a new field becomes queryable on
      its own. That behaviour arrived with the fix for the E2BIG cliff on that
      POST, which had nothing to do with this plan. No change needed.
- [x] **4.6** — A saved search: revisions in the fleet, last 24h, grouped by
      `service` and `aerie_revision`. This is the SRE story, and it is a saved
      object rather than a documented query because a query nobody saved is a
      query nobody runs.
- [ ] **4.7** — **Gate — the Lua half passed, the cluster half is pending.**
      [`service_tag_test.lua`](../../deploy/cluster/observability/controllers/fluent-bit/service_tag_test.lua)
      covers 15 cases against the real script, including every case that looks
      right and is wrong: a digest read as a git sha, a registry port read as a
      tag, a Hyper-V console line inheriting a revision, and above all a relayed
      browser line falling back to the relay's image. *In OpenSearch, once
      deployed:* an `aerie-api` line carrying `aerie_revision` from its tag with
      no application change; a `dashboard` line carrying the browser's and an
      `aerie_relay_revision` that differs; a `traefik` line carrying neither and
      still carrying `kubernetes.container_image`.

*Exit: `aerie_revision` is one field, one meaning, one path — and
`NOT _exists_:aerie_revision` is a working definition of "not ours".*

## Phase 5 — the drift answer

The self-update story, built beside `AppVersionService` rather than on top of
it. Read finding 3 before starting.

- [x] **5.1** — `GET /api/aerie-revision` accepts the client's revision and
      sequence and answers `behind` / `current` / `ahead`. **`ahead` is the
      case that earns this step**: mid-rolling-deploy, a browser loaded from a
      new replica can ask an old one, and a client that reloads on any
      difference will thrash between two answers until the rollout finishes.
      Only `behind` may trigger anything.
- [x] **5.2** — **Moved to the server** — finding 11. `AerieRevisionMiddleware`
      compares every request's `Aerie-Client-Revision` against its own and logs
      `Behind`/`Ahead` at warning, throttled to once per client revision per
      five minutes. **Logging only, in this step.** The measurement comes before
      the action, and a week of seeing how often `Ahead` actually occurs is what
      tells you whether 5.3 is safe.
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

## Phase 6 — what the cluster has reconciled

The only phase needing access the API does not have today. Separated for that
reason, and can ship long after Phase 5.

- [x] **6.1** — A ServiceAccount with a `ClusterRole` scoped to `get`/`list` on
      `gitrepositories` and `kustomizations` in `source.toolkit.fluxcd.io` /
      `kustomize.toolkit.fluxcd.io`, and **nothing else**. The API has no
      Kubernetes access at all right now; this is the step that changes that,
      and it is worth the narrowest possible grant.
- [x] **6.2** — `/api/aerie-revision` gains a `cluster` block for an
      authenticated admin caller: per source, the artifact revision and the
      `lastAppliedRevision` of each Kustomization reading from it, parsed out of
      `main@sha1:<sha>` (finding 8). Both repositories.
- [x] **6.3** — Cached, short TTL. This is a Kubernetes API call behind an HTTP
      endpoint, and an endpoint an admin screen polls must not become a way to
      generate load against the control plane.
- [x] **6.4** — Absent or degraded when Flux is unreachable — an API that fails
      its own version endpoint because the cluster is unhealthy has failed at
      the moment it was most needed.
- [x] **6.5** — An admin app screen: every revision in the system on one page,
      app tier and cluster, with the gap called out where it exists.
- [ ] **6.6** — **Gate — the parser is verified, the screen is pending.** Every
      field path the reader reads was confirmed against live objects before it
      was written: `status.artifact.revision`, `spec.sourceRef.name`,
      `status.lastAppliedRevision`, and the `Ready` condition, including Flux's
      `main@sha1:<sha>` form. *Once deployed:* push a commit and watch the
      screen show the source ahead of `lastAppliedRevision` and converge — the
      exact gap finding 8 caught by accident, now visible on purpose.

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
