# Apps

Turning Aerie into a platform for small family apps, and building the first one
(Storage Helper).

> Scope discipline: this plan builds **the module seam plus one app**. Chores,
> grocery, and chat were motivating examples, not commitments. Every future app
> is decided the day it's wanted. The measure of success here is not "Storage
> Helper works" but "app #2 is a folder and an afternoon."

## Goals

1. **Zero infra per app** — a new micro-app adds no container, database,
   ingress, backup entry, or uptime monitor. It inherits all of it.
2. **Cohesive feel** — the suite looks and behaves like one product, and stays
   that way at ten apps without anyone maintaining that by hand.
3. **Integrable data** — apps can read each other's data without an integration
   project, because it's one database.
4. **Fast to start** — the cost of a new app is a folder, a `DbContext`, and a
   route. Upfront platform cost is acceptable; per-app cost is not.

## Decisions

| Question | Decision |
|---|---|
| Backend shape | **Modular monolith** in `Aerie.Api` — one process, one deploy |
| Data isolation | **Schema + `DbContext` per module**, one Postgres database |
| Module layout | **Module-first folder** (`Modules/<Name>/`), not layer-first |
| Frontend shape | **One shell SPA**, micro-apps as lazily-loaded route modules |
| Client platform | **PWA** (installed to home screen). Native iOS deferred, not rejected |
| Access | **Tailnet only** — status quo, no public DNS, no public ingress |
| Auth | **None for now.** Seam preserved; tripwire defined below |
| Search | **Postgres full-text**, not OpenSearch |
| Blobs / photos | **Deferred** until after the k3s cutover — see [Deferred](#deferred) |
| First app | **Storage Helper** |

### Why a modular monolith

The alternative — a service per app — costs a Deployment, Service, Ingress,
database, backup entry, and uptime monitor *per app*. That is the exact opposite
of goal 1, and it's worse than usual right now with the k3s migration in flight
([`TODO_SWARM.md`](TODO_SWARM.md)). As a module, a new app rides the existing
pod, inherits CNPG backups automatically, and appears in the existing
observability pipeline with no new configuration.

Splitting one out later is a connection string change, not a rewrite. The values
in play — "a bucket of functionality for my family" — make that trade obvious.

### Why schema-per-module and not one shared context

[`Ef/AerieContext.cs`](src/Aerie.Api/Ef/AerieContext.cs) today is one class
holding every `DbSet` in the system, and every feature's migration edits one
shared model snapshot. That is fine at one domain (home automation) and becomes
a merge magnet and an unreadable file at ten. A context per module means a
Storage migration never touches the climate model, and the module folder is a
real boundary rather than a naming convention.

They stay in **one database**, so cross-app queries are still just SQL, and one
backup still covers everything.

### Why module-first folders here, but not everywhere

The existing layout is layer-first — `Models/ClimateControl`,
`Services/DeviceMapping`, flat `Controllers/`. That's correct for what it holds:
*one* cohesive domain sliced by layer. The new apps are N independent domains
where the app is the boundary, so the folder should be the app. New modules go
in `Modules/<Name>/`; existing home-automation code stays where it is. Two
layouts, each matching its own shape.

### Why the shell SPA removes the shared-package problem

Worth stating explicitly, because it reverses an earlier instinct to build
`@aerie/ui` and `@aerie/client` workspace packages up front.

The current per-app duplication is real — `deviceMetadata.ts` is byte-identical
in four apps, `clientLogger.ts` exists as four drifted copies (3883 / 4401 /
4552 / 4704 bytes), `theme.css` has diverged across all four. But that's a
symptom of apps being **separate Vite builds**. If every family app is a route
module inside one SPA, they share theme, components, and API client by simply
importing them. There is nothing to package.

So: no npm workspace, no shared packages, no changes to the four existing
standalone apps. Extracting `@aerie/ui` becomes worthwhile only if the family
shell and `dashboard`/`admin` need to share components — a separate, deferrable
question. Not today's problem.

### Why PWA now, native later

An installed-to-home-screen PWA on iOS covers every capability Storage Helper
needs: camera access for scanning, offline reads via service worker, home-screen
presence, and Web Push (iOS 16.4+, install required) when notifications
eventually matter. A native app would add Swift, Xcode, a paid developer
account, and a release cycle *per micro-app* — directly against goal 4 — and
would make a second operator's install strictly worse per
[`docs/ethos.md`](docs/ethos.md).

The long-term want (a buttoned-up native entry point) is preserved: when it's
built, it should be a **shell** — auth, nav, push, native camera — hosting the
web modules, so adding an app never requires an App Store release.

There is one free UX win worth designing for now: because the QR encodes a URL,
the stock iOS camera opens a crate page directly. No app required to look
something up.

### Auth: none now, and the tripwire

Two trusted adults, tailnet-only access, no sensitive data. Building auth today
is speculative work against goal 4, so it's out of scope.

What this plan does owe is *not painting into a corner*. Adding auth later
should be middleware plus a `Person` table, not a refactor — which holds as long
as no module invents its own notion of a user in the meantime. No module in this
plan does.

**Tripwire — build real auth before any of these land:** chat, documents or
anything scanned, anything with financial data, or the first non-adult account.
Note for honesty rather than alarm: a full index of what's in your house and
where is a burglary aid, so the tailnet boundary is doing real work here, not
just convenience.

## Target architecture

```
Aerie.Api (one process, one deploy)
├── Controllers/, Services/, Ef/        existing home-automation domain, unchanged
├── Modules/
│   └── Storage/                        ← the whole app, one folder
│       ├── StorageContext.cs           schema "storage", own migration history
│       ├── Entities.cs
│       ├── StorageController.cs        /api/storage/*
│       ├── StorageService.cs
│       └── Migrations/
└── wwwroot/apps/
    ├── dashboard/ admin/ docs/ modeler/    existing standalone SPAs, untouched
    └── family/                             ← the shell PWA

Postgres (one database, one backup)
├── public.*      existing tables
└── storage.*     locations, crates, items
```

Adding app #2 = one folder under `Modules/`, one folder under the shell's
`src/modules/`, one nav entry. No changes to `Program.cs`, the Dockerfile, CI,
or any deployment manifest — that property is what Phase 0 buys, and it is the
point of the whole exercise.

## Implementation plan

### Phase 0 — Module seam

The one-time platform work. Nothing user-visible; everything after this is cheap.

- [x] `Modules/IModuleContext.cs` — marker interface for module `DbContext`s.
      Declares `Database`, which `DbContext` already satisfies, so the migration
      loop needs no cast and a module needs no method bodies.
- [x] Replace the hardcoded migration block in
      [`Program.cs:157`](src/Aerie.Api/Program.cs#L157) with a loop over every
      registered `IModuleContext`, so module #2 never edits `Program.cs`.
- [x] `Modules/ModuleRegistration.cs` — one extension method registering a
      module context with the shared connection string, its schema, and its own
      `MigrationsHistoryTable`. Plus `AddAerieModules`, the one-line-per-module
      registry, so the list of apps lives in `Modules/` and `Program.cs` holds a
      single call that never changes.
- [x] Generic design-time factory for module contexts
      ([`ModuleDesignTimeFactory.cs`](src/Aerie.Api/Modules/ModuleDesignTimeFactory.cs)).
      [`DesignTimeDbContextFactory.cs`](src/Aerie.Api/Ef/DesignTimeDbContextFactory.cs)
      is typed to `AerieContext`; `dotnet ef` needs one per context type. The EF
      CLI only finds concrete non-generic factories, so a module still writes a
      three-line subclass — that's the whole per-module cost.
- [x] `Modules/README.md` — how to add a module, in ten lines. This is the
      artifact that makes app #2 an afternoon.

**Verify:** `dotnet ef migrations add Init --context StorageContext` scaffolds
into the module folder; app starts; `\dn` in `make db-shell` shows the new
schema; `public.__EFMigrationsHistory` is untouched.

*Verified* against a throwaway `Scratch` module (since Storage doesn't exist
yet), then removed: migration scaffolded to `Modules/Scratch/Migrations/` with
`Migrations/` untouched, startup logged `Migrating module context
ScratchContext`, `\dn` showed `scratch` holding both `Things` and its own
`__EFMigrationsHistory`, and `public.__EFMigrationsHistory` stayed at 11 rows.
Note for Phase 1: `dotnet ef migrations add` builds *before* writing the
migration, so a `--no-build` run right after it won't have the new migration in
the assembly.

### Phase 1 — Storage Helper backend

```
storage.locations  id, name, description, created_at
storage.crates     id, code (unique), label, location_id → locations (SET NULL),
                   notes, created_at, updated_at
storage.items      id, crate_id → crates (CASCADE), name, quantity, notes,
                   search_vector (generated), created_at, updated_at
```

- [x] Entities + `StorageContext` with `HasDefaultSchema("storage")`.
      `search_vector` lands with the rest of search in Phase 5, as its own
      migration.
- [x] **Crate codes** — 6 chars, Crockford base32 (no `I`/`L`/`O`/`U`), displayed
      `XXX-XXX`. Generated server-side, retry on unique violation. Printed as
      text on the label beside the QR so a scuffed or faded code is still
      recoverable by typing — these labels live in a garage for a decade.
      `CrateCode.Normalize` also accepts what someone reads *off* a worn label:
      lowercase, dashed, and `I`/`L`/`O` in place of the digits they resemble.
- [x] `StorageController` — CRUD for locations, crates, items; `GET
      /api/storage/crates/by-code/{code}` for the scan path.
- [x] **Batch crate creation** — `POST /api/storage/crates/batch { count }`
      returning N unlabeled crates. This is the workflow that matters: print a
      sheet, tape labels on empty boxes, *then* scan each one as you fill it.
      Creating a crate in the UI before it physically exists is backwards.
- [x] Tests in `Aerie.Api.Tests/Storage/` — code generation and collision retry,
      batch creation, cascade behavior.

**Verify:** full lifecycle through Swagger; deleting a crate takes its items and
leaves its location; deleting a location orphans crates rather than deleting them.

*Verified* end-to-end over HTTP against the real Postgres (not just InMemory):
`Init` applied into `storage` with `public.__EFMigrationsHistory` still at 11
rows, startup logged `Migrating module context StorageContext`, a batch of 3
minted distinct codes, a crate typed back as `D2Q-dym` resolved through the scan
path, deleting the location left its crate unplaced with items intact, and
deleting the crate took its items and returned 404.

Registration note: a module's services are wired by its own
`Add<Name>Module(configuration)` extension (`Modules/Storage/StorageModule.cs`),
which `AddAerieModules` calls in one line — otherwise the registry grows a
section per app and module services drift out of the module folder.

### Phase 2 — Shell PWA

Plumbing a new SPA into the existing build. Follows the four established
patterns exactly — this is a known path, just several files.

- [x] `src/Aerie.Web/apps/family/` — Vite + React + `react-router-dom`, `base:
      '/apps/family/'`, output to `wwwroot/apps/family` (copy
      [`dashboard/vite.config.ts`](src/Aerie.Web/apps/dashboard/vite.config.ts)).
- [x] Shell chrome: header, bottom tab nav sized for one thumb, theme lifted
      from the existing `theme.css` tokens. Micro-apps mount as lazy routes
      under `/apps/family/<module>/`.
- [x] **PWA**: `manifest.webmanifest` (standalone display, icons, theme color),
      service worker caching the app shell. Cache-first for the shell,
      network-first for data — an offline *read* of a crate is useful; an
      offline write is not worth the sync complexity yet.
- [x] `vite-plugin-pwa`, or a hand-rolled SW if that pulls too much in.
      Hand-rolled ([`src/sw.js`](src/Aerie.Web/apps/family/src/sw.js), ~40
      lines). The one thing a hand-written worker genuinely can't produce is
      the list of *hashed* asset filenames, which only exists after the bundle
      is built — so that single piece is a ~30-line `serviceWorker()` plugin in
      [`vite.config.ts`](src/Aerie.Web/apps/family/vite.config.ts), and Workbox
      stays out.
- [x] Wire the build in four places, mirroring the existing apps exactly:
      a `BuildFamily` target in
      [`Aerie.Api.csproj`](src/Aerie.Api/Aerie.Api.csproj) with a
      `SkipFamilyBuild` guard; a `family-build` stage in
      [`Dockerfile.api`](src/Aerie.Api/Dockerfile.api) plus its `COPY` and
      `-p:SkipFamilyBuild=true`; `family` in the
      [`ci.yml`](.github/workflows/ci.yml#L32) matrix; the `test-web` loop in
      [`Makefile`](Makefile).
- [x] `MapFallbackToFile` + rewrite rule in
      [`Program.cs:253`](src/Aerie.Api/Program.cs#L253) so deep links survive a
      hard refresh — this is what makes a scanned QR work.
- [x] Add a card to the apps landing page
      ([`src/Aerie.Web/index.html`](src/Aerie.Web/index.html)).

**Verify:** hard-refresh `/apps/family/storage/c/ABC-123` serves the shell;
"Add to Home Screen" on iOS launches standalone with no browser chrome; airplane
mode still opens the shell.

*Verified* over HTTP against the running API: `/apps/family/storage/c/ABC-123`,
`/apps/family/storage/locations` and an unknown deep link each return the shell
document byte-identical to `/apps/family/`, while `sw.js`, the manifest, the
icons and `assets/index-*.js` still resolve as real files with correct content
types (the `:nonfile` constraint holds), and `/apps/family` redirects to
`/apps/family/`. All 10 precache URLs return 200 — worth checking mechanically,
because `cache.addAll` is all-or-nothing and one 404 silently costs you the
whole offline story. `sw.js` correctly does not precache itself. The cache name
changes for a source edit *and* for a `public/` edit (the case Vite's hashed
filenames don't cover), and returns to its previous value when both are
reverted. `dotnet build` runs `BuildFamily`; the 160 API tests still pass.

The two device-side checks — iOS Add to Home Screen, and airplane mode — are
still open, since they can't be exercised from here.

Registry note: the module seam is
[`src/modules/registry.ts`](src/Aerie.Web/apps/family/src/modules/registry.ts).
A module is a folder plus one array entry; the home-screen card, the bottom
tab, the lazy chunk and the route all derive from it, so "one nav entry" is
literally one line. The shell never learns a module's internal screens — a
module renders its own `<Routes>` under `/apps/family/<id>/`.

### Phase 3 — Storage Helper UI

- [x] **Crate view** — the scan destination, and the screen that has to be
      instant: label, location, item list, add-item inline.
- [x] **Item index** — flat searchable list across all crates, each row showing
      crate + location. This is the "where is the drill" screen and the reason
      the app exists.
- [x] **Locations** — simple CRUD list. Flat, no hierarchy; add nesting only if
      it's genuinely missed.
- [x] **Crate list** grouped by location, for the "what's in the attic" question.
- [x] [`docs/storage-helper.md`](docs/storage-helper.md), per the file-impact
      list below.

**Verify:** add a crate, put items in it, find each item from the index, land on
the right crate. On a phone, one-handed.

*Verified* at the seam this phase could break: every DTO's JSON was exercised
against the running API and the real Postgres — location, crate, item, the
`by-code` scan path (typed back lowercase and dashed, as someone reads it off a
label), the `items` index row, a `400` body, a `404`, and both cascades — and
matched `types.ts` field for field. `npm run build` and `oxlint` are clean; the
module is its own 16 kB lazy chunk, so the shell's launch cost is unchanged.

Screen behaviour on a phone is the user's to check — that half of "one-handed"
can't be exercised from here.

Two notes for Phase 4. Search is client-side over the loaded index (every term
must match somewhere on the row, so `drill garage` works); it's a placeholder
for Phase 5's `GET /search`, but it's the placeholder that keeps working offline,
which the real one won't. And crate creation in the UI is a single "New crate"
button — the batch endpoint stays unused until the print sheet exists, because
minting twenty blank crates is only useful when you can print twenty labels.

Routing note: links inside a module are built from
[`routes.ts`](src/Aerie.Web/apps/family/src/modules/storage/routes.ts), not
`../`-relative. `..` resolves against the *route* hierarchy, and this module
renders links from an index route, a splat route and ordinary routes alike,
where it means three different things.

### Phase 4 — QR labels

- [x] Client-side QR via `qrcode` — already a dependency in
      [`admin/package.json`](src/Aerie.Web/apps/admin/package.json) for kiosk
      provisioning, same pattern. Loaded through a dynamic `import()`
      ([`qr.ts`](src/Aerie.Web/apps/family/src/modules/storage/qr.ts)) so its
      ~30 kB is its own chunk: printing happens at a desk, and the path this
      app is judged by is a camera pointed at a box.
- [x] **Encode a full URL**, `{PublicBaseUrl}/apps/family/storage/c/{code}` — so
      the stock camera app opens the crate with nothing installed.
- [x] `Apps:PublicBaseUrl` config, supplied at deploy from `vars.DOMAIN` like
      every other operator value. **Not hardcoded** — per
      [`docs/ethos.md`](docs/ethos.md) this is exactly the "structural file
      quietly carrying an operator value" failure mode.
      [`Modules/AppsOptions.cs`](src/Aerie.Api/Modules/AppsOptions.cs) +
      `GET /api/apps/config`, registered from `AddAerieModules` so `Program.cs`
      still doesn't change. Unset falls back to the printing browser's origin
      and says so on screen — a fresh operator gets working labels, but never
      silently.
- [x] **Print sheet** — `@page` CSS, grid of QR + human-readable code + blank
      write-on line. Rows/columns and page size as UI settings defaulting to
      Letter; don't hardcode a label product or assume Letter paper.
      Laid out in `mm`, so the on-screen preview is at true physical size.
- [x] "Print N new labels" flow: batch-create → print → tape → scan to name.

**Verify:** print a sheet on real paper, scan with the stock iOS camera from
across a room, land on the right crate. Scan a label photocopied once to
approximate wear.

*Verified* at the seams that can be exercised without paper: `/api/apps/config`
returned the configured base with its trailing slash trimmed over HTTP, a batch
of 3 minted distinct codes, and the URL the sheet prints
(`/apps/family/storage/c/93TQT3`) both resolved through `by-code` and served the
shell byte-identical to `/apps/family/` on a hard refresh — the printed string is
therefore a working link end to end. The new QR chunk is in the service worker's
precache and all 12 precache URLs still return 200, so printing works offline
too. `NormalizeBaseUrl` has its own tests for the cases that would only surface
on paper (no scheme, wrong scheme, unset); API tests are at 174, `npm run build`
and `oxlint` are clean, and the storage chunk went 16 kB → 24 kB with the
encoder split out.

The acceptance test — paper, a stock camera, and a photocopied label — is the
user's, along with choosing a grid that suits their boxes.

Config note: `compose.prod.yml` gains one line (`Apps__PublicBaseUrl` built from
`DOMAIN`). That is the one edit to the "untouched" list below, and it's platform
cost rather than per-app cost: it's the install's own URL, and app #2 inherits it
without touching a compose file.

### Phase 5 — Search

- [x] Generated `tsvector` column on `items` (name + notes), GIN index.
- [x] `GET /api/storage/search?q=` spanning item name/notes, crate label, and
      location name; returns item → crate → location in one row — the same
      `ItemIndexRow` as `GET /items`, so the list screen and the match screen
      stay one screen.
- [x] Prefix matching so search is useful while typing.

Postgres full-text, deliberately — **not** OpenSearch. The cluster is already
there for logs, but a few hundred household items is three orders of magnitude
below where that earns its complexity, and `tsvector` won't need replacing at
this scale.

**Verify:** partial words and misspelling-adjacent prefixes find the right item;
searching a location name returns everything stored there.

*Verified* over HTTP against the real Postgres, with the `Search` migration
applied into `storage` (`public.__EFMigrationsHistory` still at 11 rows) and
`\d storage."Items"` showing `SearchVector` as `GENERATED ALWAYS AS … STORED`
under a GIN index. `dri` found both drill rows, `lights` and `light` each found
`String lights` (stemming), `batter` found the item whose *notes* mention a
battery, `drill garage` found the drill in the garage while `attic` alone
returned everything stored there, and the crate code matched typed as `HZ4-YHB`,
`hz4` or `yhb`. Empty, punctuation-only and stop-word-only queries return `[]`
rather than the whole index; `a drill in the garage` still finds the drill. 188
API tests pass, `npm run build` and `oxlint` are clean, and all 12 service-worker
precache URLs still return 200.

One knock-on worth naming: adding a column to `Item` meant every read that
projected the whole entity — the item index *and* the scan path — would have
shipped a search document per row to a phone. Both now project columns, verified
by the crate detail and index responses carrying no `searchVector` field, and
`UpdateCrate` counts its items instead of loading them.

Two things worth writing down, because both are trades rather than oversights:

**The GIN index does not accelerate this query.** A generated column can only see
its own row, so crate and location text is concatenated onto the item's vector
per row — and a concatenated vector can't use the index. Making it index-backed
means either splitting the query per matched crate or maintaining a denormalised
document by trigger, and neither is worth it three orders of magnitude below where
it would matter. The column and index still earn their place: the item's own text
is tokenised on write rather than per query, and the day this needs to be a lookup
it's a query change, not a schema change.

**Offline search survives.** Phase 3 predicted the switch to `GET /search` would
cost the offline half; it doesn't. The screen still loads the full index (it's the
list with an empty box), so any search failure falls back to the substring filter
over that list and says so under the box, rather than showing a red error in the
one place in the house where the signal drops. Search responses are fetched
`no-store` — one URL per settled keystroke would otherwise fill the worker's data
cache with answers to questions nobody asks twice — and `sw.js` honours that
generically, so it still knows nothing about any module's routes.

Provider note: `StorageContext` configures the tsvector column only when the
provider is Npgsql, because EF's InMemory provider — which the module's unit
tests run on — cannot map a `tsvector` at all and fails model validation. The
query-building half (`SearchQuery`) is a pure function with its own tests; what
the query *finds* is Postgres's own behaviour and is verified against a real one.

## Deferred

Named so they're decisions rather than oversights.

- **Photos** (crate contents, item pictures) — the single most valuable v2
  feature for this app class; seeing a crate's contents without unpacking it is
  most of the point. Deferred on *timing*, not value: blob storage should land
  on Longhorn after the k3s cutover rather than on the current host's disk and
  then get migrated. Revisit at [`TODO_SWARM.md`](TODO_SWARM.md) Phase 7.
- **Auth / identity** — see the tripwire above.
- **Offline writes** — offline reads ship in Phase 2; write sync needs conflict
  resolution that no current use case justifies.
- **Notifications, domain events, shared attachments, OpenSearch indexing** —
  all real platform services, none needed by one app. Build each when the second
  app makes it a duplication rather than a guess.
- **`@aerie/ui` extraction** — only if the shell and the existing standalone
  apps need to share components.
- **Native iOS shell** — when feel or push reliability demands it.

## File impact

**New**
```
src/Aerie.Api/Modules/IModuleContext.cs, ModuleRegistration.cs,
                       ModuleDesignTimeFactory.cs, README.md,
                       AppsOptions.cs, AppsController.cs
src/Aerie.Api/Modules/Storage/**
src/Aerie.Api.Tests/Storage/**, src/Aerie.Api.Tests/Modules/**
src/Aerie.Web/apps/family/**
docs/storage-helper.md          (after Phase 3, matching docs/ conventions)
```

**Changed**
```
src/Aerie.Api/Program.cs         module migration loop; family fallback + rewrite
src/Aerie.Api/Aerie.Api.csproj   BuildFamily target
src/Aerie.Api/Dockerfile.api     family-build stage, COPY, SkipFamilyBuild
src/Aerie.Api/appsettings.json   empty Apps:PublicBaseUrl default
.github/workflows/ci.yml         family in the web matrix
Makefile                         family in test-web
src/Aerie.Web/index.html         landing page card
compose.prod.yml                 Apps__PublicBaseUrl from DOMAIN (Phase 4)
```

**Untouched** — `apps/dashboard`, `apps/admin`, `apps/docs`, `apps/modeler`,
`Ef/AerieContext.cs`, every k3s manifest, and every compose file except the one
line of `compose.prod.yml` that hands the install its own public URL. That list
is the proof that goal 1 holds: all of it is one-time platform cost, and app #2
adds nothing to any of these files.
