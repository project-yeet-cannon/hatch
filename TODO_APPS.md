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

- [ ] `Modules/IModuleContext.cs` — marker interface for module `DbContext`s.
- [ ] Replace the hardcoded migration block in
      [`Program.cs:157`](src/Aerie.Api/Program.cs#L157) with a loop over every
      registered `IModuleContext`, so module #2 never edits `Program.cs`.
- [ ] `Modules/ModuleRegistration.cs` — one extension method registering a
      module context with the shared connection string, its schema, and its own
      `MigrationsHistoryTable`.
- [ ] Generic design-time factory for module contexts.
      [`DesignTimeDbContextFactory.cs`](src/Aerie.Api/Ef/DesignTimeDbContextFactory.cs)
      is typed to `AerieContext`; `dotnet ef` needs one per context type.
- [ ] `Modules/README.md` — how to add a module, in ten lines. This is the
      artifact that makes app #2 an afternoon.

**Verify:** `dotnet ef migrations add Init --context StorageContext` scaffolds
into the module folder; app starts; `\dn` in `make db-shell` shows the new
schema; `public.__EFMigrationsHistory` is untouched.

### Phase 1 — Storage Helper backend

```
storage.locations  id, name, description, created_at
storage.crates     id, code (unique), label, location_id → locations (SET NULL),
                   notes, created_at, updated_at
storage.items      id, crate_id → crates (CASCADE), name, quantity, notes,
                   search_vector (generated), created_at, updated_at
```

- [ ] Entities + `StorageContext` with `HasDefaultSchema("storage")`.
- [ ] **Crate codes** — 6 chars, Crockford base32 (no `I`/`L`/`O`/`U`), displayed
      `XXX-XXX`. Generated server-side, retry on unique violation. Printed as
      text on the label beside the QR so a scuffed or faded code is still
      recoverable by typing — these labels live in a garage for a decade.
- [ ] `StorageController` — CRUD for locations, crates, items; `GET
      /api/storage/crates/by-code/{code}` for the scan path.
- [ ] **Batch crate creation** — `POST /api/storage/crates/batch { count }`
      returning N unlabeled crates. This is the workflow that matters: print a
      sheet, tape labels on empty boxes, *then* scan each one as you fill it.
      Creating a crate in the UI before it physically exists is backwards.
- [ ] Tests in `Aerie.Api.Tests/Storage/` — code generation and collision retry,
      batch creation, cascade behavior.

**Verify:** full lifecycle through Swagger; deleting a crate takes its items and
leaves its location; deleting a location orphans crates rather than deleting them.

### Phase 2 — Shell PWA

Plumbing a new SPA into the existing build. Follows the four established
patterns exactly — this is a known path, just several files.

- [ ] `src/Aerie.Web/apps/family/` — Vite + React + `react-router-dom`, `base:
      '/apps/family/'`, output to `wwwroot/apps/family` (copy
      [`dashboard/vite.config.ts`](src/Aerie.Web/apps/dashboard/vite.config.ts)).
- [ ] Shell chrome: header, bottom tab nav sized for one thumb, theme lifted
      from the existing `theme.css` tokens. Micro-apps mount as lazy routes
      under `/apps/family/<module>/`.
- [ ] **PWA**: `manifest.webmanifest` (standalone display, icons, theme color),
      service worker caching the app shell. Cache-first for the shell,
      network-first for data — an offline *read* of a crate is useful; an
      offline write is not worth the sync complexity yet.
- [ ] `vite-plugin-pwa`, or a hand-rolled SW if that pulls too much in.
- [ ] Wire the build in four places, mirroring the existing apps exactly:
      a `BuildFamily` target in
      [`Aerie.Api.csproj`](src/Aerie.Api/Aerie.Api.csproj) with a
      `SkipFamilyBuild` guard; a `family-build` stage in
      [`Dockerfile.api`](src/Aerie.Api/Dockerfile.api) plus its `COPY` and
      `-p:SkipFamilyBuild=true`; `family` in the
      [`ci.yml`](.github/workflows/ci.yml#L32) matrix; the `test-web` loop in
      [`Makefile`](Makefile).
- [ ] `MapFallbackToFile` + rewrite rule in
      [`Program.cs:253`](src/Aerie.Api/Program.cs#L253) so deep links survive a
      hard refresh — this is what makes a scanned QR work.
- [ ] Add a card to the apps landing page
      ([`src/Aerie.Web/index.html`](src/Aerie.Web/index.html)).

**Verify:** hard-refresh `/apps/family/storage/c/ABC-123` serves the shell;
"Add to Home Screen" on iOS launches standalone with no browser chrome; airplane
mode still opens the shell.

### Phase 3 — Storage Helper UI

- [ ] **Crate view** — the scan destination, and the screen that has to be
      instant: label, location, item list, add-item inline.
- [ ] **Item index** — flat searchable list across all crates, each row showing
      crate + location. This is the "where is the drill" screen and the reason
      the app exists.
- [ ] **Locations** — simple CRUD list. Flat, no hierarchy; add nesting only if
      it's genuinely missed.
- [ ] **Crate list** grouped by location, for the "what's in the attic" question.

**Verify:** add a crate, put items in it, find each item from the index, land on
the right crate. On a phone, one-handed.

### Phase 4 — QR labels

- [ ] Client-side QR via `qrcode` — already a dependency in
      [`admin/package.json`](src/Aerie.Web/apps/admin/package.json) for kiosk
      provisioning, same pattern.
- [ ] **Encode a full URL**, `{PublicBaseUrl}/apps/family/storage/c/{code}` — so
      the stock camera app opens the crate with nothing installed.
- [ ] `Apps:PublicBaseUrl` config, supplied at deploy from `vars.DOMAIN` like
      every other operator value. **Not hardcoded** — per
      [`docs/ethos.md`](docs/ethos.md) this is exactly the "structural file
      quietly carrying an operator value" failure mode.
- [ ] **Print sheet** — `@page` CSS, grid of QR + human-readable code + blank
      write-on line. Rows/columns and page size as UI settings defaulting to
      Letter; don't hardcode a label product or assume Letter paper.
- [ ] "Print N new labels" flow: batch-create → print → tape → scan to name.

**Verify:** print a sheet on real paper, scan with the stock iOS camera from
across a room, land on the right crate. Scan a label photocopied once to
approximate wear.

### Phase 5 — Search

- [ ] Generated `tsvector` column on `items` (name + notes), GIN index.
- [ ] `GET /api/storage/search?q=` spanning item name/notes, crate label, and
      location name; returns item → crate → location in one row.
- [ ] Prefix matching so search is useful while typing.

Postgres full-text, deliberately — **not** OpenSearch. The cluster is already
there for logs, but a few hundred household items is three orders of magnitude
below where that earns its complexity, and `tsvector` won't need replacing at
this scale.

**Verify:** partial words and misspelling-adjacent prefixes find the right item;
searching a location name returns everything stored there.

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
src/Aerie.Api/Modules/IModuleContext.cs, ModuleRegistration.cs, README.md
src/Aerie.Api/Modules/Storage/**
src/Aerie.Api.Tests/Storage/**
src/Aerie.Web/apps/family/**
docs/storage-helper.md          (after Phase 3, matching docs/ conventions)
```

**Changed**
```
src/Aerie.Api/Program.cs         module migration loop; family fallback + rewrite
src/Aerie.Api/Aerie.Api.csproj   BuildFamily target
src/Aerie.Api/Dockerfile.api     family-build stage, COPY, SkipFamilyBuild
.github/workflows/ci.yml         family in the web matrix
Makefile                         family in test-web
src/Aerie.Web/index.html         landing page card
```

**Untouched** — `apps/dashboard`, `apps/admin`, `apps/docs`, `apps/modeler`,
`Ef/AerieContext.cs`, every compose file, every k3s manifest. That list is the
proof that goal 1 holds.
