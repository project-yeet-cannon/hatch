# Family Apps Architecture

## Summary

Aerie hosts a suite of small household apps — [Storage Helper](storage-helper.md)
and [Gather](gather.md) so far — as **modules inside the existing `Aerie.Api`
process**, surfaced through **one shell PWA** at `/apps/family/`.

The design goal is not any individual app. It is that app #2 costs a folder and
an afternoon: one folder under `src/Aerie.Api/Modules/`, one folder under the
shell's `src/modules/`, one line in each registry. No container, no database, no
ingress, no backup entry, no uptime monitor, no CI job, and no edit to
`Program.cs`, the Dockerfile, or any deployment manifest.

App #2 is [Gather](gather.md), and it is the evidence rather than the claim: a
module folder, a shell module folder, one line in each registry, and nothing on
[What must stay untouched](#what-must-stay-untouched) was edited. The two things
it *did* cost the platform are both named below — four pieces of shell plumbing
that moved up out of Storage, and an OpenAPI schema-id collision that only a
second module could have found.

Everything below is the reasoning behind that property, and the tripwires that
would invalidate it. The step-by-step mechanics of adding an app live next to the
code, in [`src/Aerie.Api/Modules/README.md`](../src/Aerie.Api/Modules/README.md)
(backend) and
[`src/Aerie.Web/apps/family/README.md`](../src/Aerie.Web/apps/family/README.md)
(shell).

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
| Access | **Tailnet only** — no public DNS, no public ingress |
| Auth | **One wall, device grants** — [`auth-architecture.md`](auth-architecture.md). Was deferred; the tripwire below is what tripped |
| Search | **Postgres full-text**, not OpenSearch |
| Blobs / photos | **Deferred** until after the k3s cutover — see [Deferred](#deferred) |

### Why a modular monolith

The alternative — a service per app — costs a Deployment, Service, Ingress,
database, backup entry, and uptime monitor *per app*. That is the exact opposite
of goal 1, and it's worse than usual with the k3s migration in flight
([the cluster plan](plans/swarm/design.md)). As a module, a new app rides the existing
pod, inherits CNPG backups automatically, and appears in the existing
observability pipeline with no new configuration.

Splitting one out later is a connection string change, not a rewrite. The values
in play — "a bucket of functionality for my family" — make that trade obvious.

### Why schema-per-module and not one shared context

[`Ef/AerieContext.cs`](../src/Aerie.Api/Ef/AerieContext.cs) is one class holding
every `DbSet` in the home-automation domain, and every feature's migration edits
one shared model snapshot. That is fine at one domain and becomes a merge magnet
and an unreadable file at ten. A context per module means a Storage migration
never touches the climate model, and the module folder is a real boundary rather
than a naming convention.

They stay in **one database**, so cross-app queries are still just SQL, and one
backup still covers everything.

### Why module-first folders here, but not everywhere

The home-automation layout is layer-first — `Models/ClimateControl`,
`Services/DeviceMapping`, flat `Controllers/`. That's correct for what it holds:
*one* cohesive domain sliced by layer. The family apps are N independent domains
where the app is the boundary, so the folder is the app. New modules go in
`Modules/<Name>/`; existing home-automation code stays where it is. Two layouts,
each matching its own shape — don't migrate one to the other.

### Why the shell SPA removes the shared-package problem

Worth stating explicitly, because it reverses an earlier instinct to build
`@aerie/ui` and `@aerie/client` workspace packages up front.

The per-app duplication among the four standalone SPAs is real —
`deviceMetadata.ts` is byte-identical in four apps, `clientLogger.ts` exists as
four drifted copies, `theme.css` has diverged across all four. But that's a
symptom of apps being **separate Vite builds**. If every family app is a route
module inside one SPA, they share theme, components, and API client by simply
importing them. There is nothing to package.

So: no npm workspace, no shared packages, no changes to the four existing
standalone apps. Extracting `@aerie/ui` becomes worthwhile only if the family
shell and `dashboard`/`admin` need to share components — a separate, deferrable
question.

### Why PWA now, native later

An installed-to-home-screen PWA on iOS covers every capability these apps need:
camera access for scanning, offline reads via service worker, home-screen
presence, and Web Push (iOS 16.4+, install required) when notifications
eventually matter. A native app would add Swift, Xcode, a paid developer account,
and a release cycle *per micro-app* — directly against goal 4 — and would make a
second operator's install strictly worse per [`ethos.md`](ethos.md).

The long-term want (a buttoned-up native entry point) is preserved: when it's
built, it should be a **shell** — auth, nav, push, native camera — hosting the
web modules, so adding an app never requires an App Store release.

There is one free UX win the design already banks: because a QR encodes a URL,
the stock iOS camera opens a crate page directly. No app required to look
something up.

### Auth: the tripwire, and what it built

Auth was deliberately deferred here — two trusted adults, tailnet-only access,
no sensitive data — with a tripwire: **build real auth before any of these
land:** chat, documents or anything scanned, anything with financial data, or
the first non-adult account. Two things the deferral owed itself were that the
tailnet boundary is doing security work rather than just saving a login screen
(a full index of what's in your house and where is a burglary aid), and that
adding auth later be middleware plus a `Person` table rather than a refactor.

**That tripwire has since been tripped, and the wall exists** —
[`auth-architecture.md`](auth-architecture.md). It landed as predicted: one
middleware, no refactor. What it did *not* build is the `Person` table. A grant
belongs to a device, not a person, so the rule below survives the wall intact —
a module still does not invent its own notion of a user, and identity attaches
to a grant when there is something that needs it.

The tailnet boundary has not gone away either. It is now the outer of two, and
the wall is what covers the devices on the house LAN that were never on the
tailnet in the first place.

## Target architecture

```
Aerie.Api (one process, one deploy)
├── Controllers/, Services/, Ef/        home-automation domain, layer-first
├── Modules/
│   ├── Storage/                        ← the whole app, one folder
│   │   ├── StorageContext.cs           schema "storage", own migration history
│   │   ├── Entities.cs
│   │   ├── StorageController.cs        /api/storage/*
│   │   ├── StorageService.cs
│   │   └── Migrations/
│   └── Gather/                         ← app #2, same shape, schema "gather"
└── wwwroot/apps/
    ├── dashboard/ admin/ docs/ modeler/    standalone SPAs, separate builds
    └── family/                             ← the shell PWA

Postgres (one database, one backup)
├── public.*      home-automation tables
├── storage.*     locations, crates, items
└── gather.*      lists, items
```

### The seams that make an app cheap

Four pieces of one-time platform work carry the whole property. Each exists so
that app #2 edits nothing outside its own two folders:

- **`IModuleContext`** — marker interface on every module `DbContext`. Startup
  loops over the registered ones and migrates each, so
  [`Program.cs`](../src/Aerie.Api/Program.cs) holds no per-module migration
  block.
- **`ModuleRegistration.cs`** — `AddModuleContext<T>` wires a context to the
  shared connection string with its own schema and its own
  `MigrationsHistoryTable`; `AddAerieModules` is the one-line-per-app registry,
  so the list of apps lives in `Modules/` and `Program.cs` holds a single call
  that never changes. A module registers its own services through one
  `Add<Name>Module` extension, so module services never drift out of the module
  folder.
- **`ModuleDesignTimeFactory<T>`** — `dotnet ef` needs a design-time factory per
  context type and only discovers concrete non-generic ones, so a module still
  writes a three-line subclass. That subclass is the entire per-module cost of
  the migration tooling.
- **The shell registry** —
  [`src/modules/registry.ts`](../src/Aerie.Web/apps/family/src/modules/registry.ts).
  The home-screen card, the bottom tab, the lazy chunk, and the route all derive
  from one array entry, so "one nav entry" is literally one line. The shell never
  learns a module's internal screens: a module renders its own `<Routes>` under
  `/apps/family/<id>/`.

Three smaller conventions belong with those, because all three have already
bitten:

- **Style modules out of `src/theme.css` tokens and nothing else.** That is the
  entire mechanism keeping the suite looking like one product; a module with its
  own palette is how ten apps stop matching.
- **Build in-module links from a module's own `routes.ts`, not `../`-relative
  paths.** `..` resolves against the *route* hierarchy, and a module that renders
  links from an index route, a splat route, and ordinary routes alike means three
  different things by it.
- **DTO names are a module's own, and the OpenAPI document has to agree.**
  Swashbuckle keys schemas on the bare type name, so Storage and Gather both
  having an `ItemDto` served a stack trace at `/swagger` for the *whole* API.
  Fixed at the platform level rather than by renaming the newcomer —
  [`Common/SwaggerSchemaIds.cs`](../src/Aerie.Api/Common/SwaggerSchemaIds.cs)
  qualifies a module's schemas with its folder name and leaves everything
  outside `Modules/` alone. Renaming would have handed the identical failure to
  whoever adds module three.

### What app #2 moved up into the shell

Gather wanted four pieces of Storage's plumbing *verbatim*, which is the bar for
promoting anything into the shell — shared when a second app needs it unchanged,
not in anticipation. `HttpError` and `createClient` are now
[`lib/http.ts`](../src/Aerie.Web/apps/family/src/lib/http.ts), `useResource`
generalized for polling is `lib/useResource.ts`, `lib/usePolling.ts` is new, and
the notice components are `components/Notices.tsx`. Storage's side of it was
imports, one class name, and 278 deleted lines; its module-prefixed notice
classes became shell-level ones in `App.css`.

The shape of that move is the point: the second app is what tells you which
abstractions were real, and a module that finds itself importing from a sibling
module's folder is the signal to promote rather than to reach across.

### Deep links are load-bearing

`MapFallbackToFile` for `/apps/family/{*path:nonfile}` is not just refresh
survival. A printed QR label encodes `/apps/family/storage/c/{code}`, so a cold
scan from the stock camera app is *always* a deep link into a route that exists
only client-side. The `:nonfile` constraint is what keeps the service worker,
manifest, icons, and hashed assets resolving as real files.

The service worker's policy — cache-first for the shell, network-first for `/api`
GETs — follows from the same scenario: an offline *read* of a crate you've
already opened is useful in the far corner of a garage; an offline write isn't
worth the sync complexity yet. Details, including why there is no `skipWaiting()`
and why the precache list is generated at build time, are in the
[shell README](../src/Aerie.Web/apps/family/README.md#service-worker).

## Configuration

`Apps:PublicBaseUrl` — the install's own canonical public URL, supplied at deploy
from the `DOMAIN` variable like every other operator value
([`Modules/AppsOptions.cs`](../src/Aerie.Api/Modules/AppsOptions.cs), served to
the shell by `GET /api/apps/config`). It is registered from `AddAerieModules`, so
adding it changed no line of `Program.cs`.

It exists because a QR label is not a link in a page: it's taped to a box for a
decade and must carry the install's canonical host, not whichever hostname the
laptop that printed the sheet happened to be using. Unset, the app falls back to
the printing browser's origin and says so on screen — a fresh operator gets
working labels, but never silently. Hardcoding a domain here would be exactly the
"structural file quietly carrying an operator value" failure mode in
[`ethos.md`](ethos.md).

This is the one value that reached a compose file: `compose.prod.yml` gains a
single `Apps__PublicBaseUrl` line built from `DOMAIN`. That's platform cost
rather than per-app cost — it's the install's own URL, and app #2 inherits it
without touching a compose file. Any module needing the public URL reads it from
`AppsOptions` rather than adding a second setting for the same fact.

## Deferred

Named so they're decisions rather than oversights.

- **Photos** (crate contents, item pictures) — the single most valuable v2
  feature for this app class; seeing a crate's contents without unpacking it is
  most of the point. Deferred on *timing*, not value: blob storage should land on
  Longhorn after the k3s cutover rather than on the current host's disk and then
  get migrated. Revisit at [the cluster plan](plans/swarm/phase-7-cutover.md) Phase 7.
- **Identity** — people, roles and scopes. The wall (see above) authenticates
  devices, not people; a grant is a row with room for an owner when one is
  needed.
- **Offline writes** — offline reads ship with the shell; write sync needs
  conflict resolution that no current use case justifies.
- **Notifications, domain events, shared attachments, OpenSearch indexing** — all
  real platform services, none needed by one app. Build each when the second app
  makes it a duplication rather than a guess.
- **`@aerie/ui` extraction** — only if the shell and the existing standalone apps
  need to share components.
- **Native iOS shell** — when feel or push reliability demands it.

## What must stay untouched

This list is the proof that goal 1 holds. All of it was one-time platform cost;
app #2 adds nothing to any of it:

`apps/dashboard`, `apps/admin`, `apps/docs`, `apps/modeler`, `Ef/AerieContext.cs`
and its `public` schema, `Program.cs`, `Aerie.Api.csproj`, `Dockerfile.api`,
`ci.yml`, the `Makefile`, every k3s manifest, and every compose file except the
one line of `compose.prod.yml` that hands the install its own public URL.

A module migration must never appear in `Migrations/` or in
`public.__EFMigrationsHistory`. If adding an app requires editing anything on
this list, the seam has a gap — fix the seam rather than the app.
