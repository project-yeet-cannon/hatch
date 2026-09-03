# Hatch — the house project tracker

Status: not started — spec digested and decisions locked 2026-09-02; Phase 0 is
next.

Aerie's projects are currently managed by juggling markdown files in
`docs/plans/`. **Hatch** replaces that with a self-hosted Jira/Trello-lite: a
kanban board at `hatch.${DOMAIN}`, issues with Jira-style keys (`AER-1`),
configurable statuses, comments, audit trails — usable by the operator through a
browser and, one phase later, by Claude through an API key. The name is the
product: you hatch a plan here, and epics hatch into stories into shipped work.

This plan is written to be executed step by step by a small model. Every task
names its files, the pattern file to read first, and the command that proves it
worked. It is also written to be parseable by its own Phase 5 importer: each
`## Phase` heading is a story, each checkbox a task — when Hatch is alive, this
document becomes its first migrated epic.

## How to work this plan

- **One checkbox = one landable change.** Do them in order within a phase;
  phases 0–4 are strictly sequential, 5 and 6 can land in either order after 4.
- **Read the named pattern file before writing.** Every task that mirrors an
  existing pattern links it. The house has one way of doing each thing; copy
  it rather than inventing a second.
- **Run the phase's Verify commands after each task**, not just at the end.
- House rules apply: build via `make` (not bare `dotnet` — the npm step needs
  the shell profile), never commit unless asked, no hardcoded domains or
  addresses anywhere ([`ethos.md`](../ethos.md)), and the operator does all
  browser/UI verification — the implementer builds and lints only.

## Goals

1. **A board the operator lives on** — every issue from every project on one
   kanban board, drag to change status, drag to reorder, always visible.
2. **Claude as a first-class user** — paste an issue link into a VS Code chat,
   Claude reads it and gets to work; planning sessions write back to the ticket
   and create sub-tickets.
3. **Audit from day one** — no reporting in the MVP, but every mutation leaves
   an append-only event row so throughput metrics are a query away later.
4. **MVP hawk** — ship the smallest tool that beats a markdown editor, on the
   existing pod, with zero new infrastructure.

## Non-goals (MVP)

- No reporting or metrics screens (events are recorded, not rendered).
- No filtration, swimlanes, sprints, WIP limits, or board configuration beyond
  statuses-as-columns.
- No granular permissions — reaching Hatch means trusted to do everything in it.
- No status-transition rules — any status to any status, we trust ourselves.
- No GitHub integration (PR/action linking is a stated direction, not built).
- No live board updates (refetch on action/focus; no websockets).
- The PR flow itself (protected `main`, branch-per-story) is **out of scope
  entirely** — it gets its own plan later.

## Decisions

| Question | Decision |
|---|---|
| Name | **Hatch** — `hatch.${DOMAIN}`, 🐣 tile. Folders: `Modules/Hatch`, `apps/hatch`, schema `hatch`, routes `/api/hatch/*` |
| Work item noun | `issue`, typed `epic \| story \| task \| bug`. Keys are `<PROJECT>-<n>`, serial per project |
| Backend shape | A **module** in Aerie.Api per [`Modules/README.md`](../../src/Aerie.Api/Modules/README.md) — own schema, own migrations, one registry line. Splitting into its own vertical later is a connection-string change, so "part of the api for now" costs nothing architecturally |
| Frontend shape | Its **own workspace SPA** like admin/docs (not a family-shell module): `src/Aerie.Web/apps/hatch`, bundled into `wwwroot/apps/hatch`, served by the api pod |
| Subdomain | `hatch.${DOMAIN}` → existing `api` Service, kiosk-style `replacePathRegex` root rewrite to `/apps/hatch/`. No new container, monitor, or backup entry |
| Access | Behind the wall; bundle and API admin-gated exactly like the admin app (bundle 404s, API 403s) via the existing `AdminGate` |
| Claude access | **Wall-level API keys**: `Authorization: Bearer` as an alternative credential decided by the same `AuthGate`, hashed at rest, minted/revoked on an admin page, per-key scopes (first scope: `hatch`). Ships *after* the human-usable app (Phase 6) |
| Statuses | **One global set** (not per-project), rows in the DB, seeded `inbox → todo → in progress → done`, CRUD in the UI. One status = one board column |
| Ordering | Drag across columns and within a column. Rank is a **`long` with 1024-gaps**, midpoint on insert, renumber-the-column on gap exhaustion. Lexorank strings considered and rejected: string midpoint math has sharp edge cases, and a column here is tens of cards — renumbering is one cheap UPDATE |
| Rank computation | **Server-side**: the client says "before/after this card", the server picks the number. Keeps every client dumb, including Claude |
| Audit | Append-only `issue_events` written by every mutating endpoint from Phase 1. Rank-only moves are deliberately not events (board hygiene, not work) |
| Description | Markdown, stored raw, edited raw, rendered with `marked` + `dompurify` — the same pair [`apps/docs`](../../src/Aerie.Web/apps/docs) already bundles |
| Importer | Read-only web upload: each `.md` doc → epic, each `## Phase` → story, each checkbox → task. Repo plan files are retired **manually**, per the [plans lifecycle](README.md) |
| Deleted issues | Hard delete, allowed, confirmed in UI. Accepted MVP gap: a deleted issue takes its events with it |

## Domain model

All in the `hatch` schema. Entities carry the house `Ef` prefix.

**`EfHatchProject`** — `Id` (int, identity), `Key` (text, unique, must match
`^[A-Z][A-Z0-9]{1,5}$`, immutable after creation — renaming keys would orphan
every `AER-n` reference in the world), `Name` (text), `NextIssueNumber` (int,
default 1, `[ConcurrencyCheck]`), `CreatedAt`.

**`EfHatchStatus`** — `Id` (int, identity), `Name` (text, unique),
`SortOrder` (int; column order on the board), `IsTerminal` (bool; marks "done"
columns so later reporting knows what shipped). Seeded in the Init migration:
inbox (10), todo (20), in progress (30), done (40, terminal).

**`EfHatchIssue`** — `Id` (long, identity), `ProjectId` (FK), `Number` (int;
unique index on `(ProjectId, Number)` as the numbering backstop), `Type` (text:
`epic|story|task|bug`), `Title` (text), `Description` (text, markdown, default
empty), `StatusId` (FK), `ParentId` (nullable self-FK), `Rank` (long),
`CreatedBy` (text actor name), `CreatedAt`, `UpdatedAt`. The display key
`AER-12` is computed (`Project.Key + "-" + Number`), never stored.

Parent rules, enforced server-side: parent must exist, be in the same project,
and not create a cycle. Type pairing: epic→epic, story→epic, task→story or bug,
bug→epic or story (all optional; 0 or 1 parent).

**`EfHatchComment`** — `Id` (long, identity), `IssueId` (FK, cascade),
`Author` (text actor name), `Body` (text, markdown), `CreatedAt`.

**`EfHatchIssueEvent`** — `Id` (long, identity), `IssueId` (FK, cascade),
`Actor` (text), `Kind` (text: `created | retitled | redescribed | retyped |
status_changed | parent_changed | commented | imported`), `Payload` (jsonb —
`{ "from": ..., "to": ... }` for changes, source filename for imports), `At`.

## API surface

Everything under `/api/hatch`, every endpoint `[RequireAdmin]` (Phase 6 widens
that attribute for scoped keys). Issue routes take the display key (`AER-12`).

| Route | Verbs | Notes |
|---|---|---|
| `/api/hatch/projects` | GET, POST | POST validates key format + uniqueness |
| `/api/hatch/projects/{id}` | PATCH, DELETE | PATCH: name only. DELETE: 409 unless the project has zero issues |
| `/api/hatch/statuses` | GET, POST | |
| `/api/hatch/statuses/{id}` | PATCH, DELETE | PATCH: name, sortOrder, isTerminal. DELETE: 409 while any issue holds it |
| `/api/hatch/board` | GET | Statuses (sorted) + all issues (key, type, title, statusId, rank, parentKey, projectKey), ordered by `(StatusId, Rank, Id)` |
| `/api/hatch/issues` | POST | `{ projectId, type, title, description?, parentKey? }` — mints number, lands in the lowest-`SortOrder` status, rank = bottom of column, writes `created` |
| `/api/hatch/issues/{key}` | GET, PATCH, DELETE | GET includes parent/children keys. PATCH: title/description/type/statusId/parentKey, one event per changed field. DELETE: confirm-worthy, cascades |
| `/api/hatch/issues/{key}/move` | POST | `{ statusId, afterKey?, beforeKey? }` — server computes rank; writes `status_changed` only if the column changed |
| `/api/hatch/issues/{key}/comments` | GET, POST | POST writes `commented` |
| `/api/hatch/issues/{key}/events` | GET | Newest first |
| `/api/hatch/import/preview` | POST | Multipart `.md` files → parse tree, touches nothing |
| `/api/hatch/import` | POST | `{ projectId, docs }` → creates the hierarchy |

**Issue numbering** (the one concurrency-sensitive spot): inside the create
request — read the project, take `NextIssueNumber`, increment it, save issue
and project in one `SaveChanges`. `[ConcurrencyCheck]` on `NextIssueNumber`
makes a race a `DbUpdateConcurrencyException`; catch it (and unique-index
violations) and retry up to 5 times. No raw SQL, testable in-memory, and the
`(ProjectId, Number)` unique index backstops it in Postgres.

**Rank** (in a `RankService` with unit tests): bottom of column = last
rank + 1024 (or 0 in an empty column); between two cards = `(a + b) / 2`;
top = first − 1024 (negatives fine). When `a + 1 == b` there is no midpoint: rewrite
the whole column to 0, 1024, 2048… in the same transaction, then place the
card.

## Phase 0 — Backend skeleton: module, schema, seeded statuses

The `Hatch` module exists, migrates, and holds the seeded status rows. Pattern
to read first: [`Modules/README.md`](../../src/Aerie.Api/Modules/README.md)
end to end, then the [`Quill`](../../src/Aerie.Api/Modules/Quill) module as the
worked example (it is the newest and smallest).

- [ ] Create `src/Aerie.Api/Modules/Hatch/Entities.cs` with the five entities
      exactly as the Domain model section above specifies, and
      `src/Aerie.Api/Modules/Hatch/HatchContext.cs` with
      `public const string Schema = "hatch"`, a `DbSet` per entity, the
      `IModuleContext` marker, and `OnModelCreating` pinning the schema and
      declaring: unique index on `EfHatchProject.Key`, unique index on
      `(EfHatchIssue.ProjectId, Number)`, unique index on `EfHatchStatus.Name`,
      cascade deletes from issue to comments/events, restrict on
      issue→status and issue→project, and `Payload` mapped as `jsonb`.
- [ ] Create `src/Aerie.Api/Modules/Hatch/HatchDesignTimeFactory.cs`
      (three lines, mirror `QuillDesignTimeFactory.cs`).
- [ ] Create `src/Aerie.Api/Modules/Hatch/HatchModule.cs` with
      `AddHatchModule(...)` registering the context (mirror `QuillModule.cs`),
      and add the one `services.AddHatchModule(configuration);` line to
      `AddAerieModules` in
      [`ModuleRegistration.cs`](../../src/Aerie.Api/Modules/ModuleRegistration.cs).
- [ ] Scaffold the Init migration into the module folder:
      `dotnet ef migrations add Init --context HatchContext --project ./src/Aerie.Api/Aerie.Api.csproj -o Modules/Hatch/Migrations`,
      then hand-add four `migrationBuilder.InsertData` calls seeding the
      statuses (inbox/10, todo/20, in progress/30, done/40 with
      `IsTerminal = true`) so every install starts with a working board.
- [ ] Add `src/Aerie.Api.Tests/Hatch/HatchContextTests.cs` mirroring
      `Quill/QuillContextTests.cs` (in-memory database): entities round-trip,
      and the model's default schema is `"hatch"`.

Verify: `make build`, `make test-api`, then `make db`,
`make ef-database-update context=HatchContext`, and `make db-shell` →
`\dt hatch.*` shows five tables and `select * from hatch."Statuses"` shows four
rows. Confirm `public.__EFMigrationsHistory` gained nothing.

## Phase 1 — Issues API: CRUD, numbering, ranking, comments, audit

The full API surface from the table above, minus the two import endpoints.
Pattern: `Quill/QuillController.cs` for controller shape and
`Quill/Dtos.cs` for DTO shape. All controllers live in
`src/Aerie.Api/Modules/Hatch/`, carry `[RequireAdmin]`
([`RequireAdminAttribute`](../../src/Aerie.Api/Common/RequireAdminAttribute.cs)),
and resolve the actor name for `CreatedBy`/`Actor`/`Author` fields through
[`ICallerIdentity`](../../src/Aerie.Api/Services/Auth/CallerIdentity.cs) —
person's name when a person is linked, otherwise the literal `"operator"`
(local dev and unlinked devices). Never read the cookie directly.

- [ ] Create `Dtos.cs`: records for project/status/issue/comment/event
      responses and the create/patch/move request bodies from the API table.
      Issue DTOs carry the computed `key` and `parentKey`, never raw ids alone.
- [ ] Create `RankService.cs` implementing the Rank spec (bottom / between /
      renumber-on-exhaustion), plus
      `src/Aerie.Api.Tests/Hatch/RankServiceTests.cs`: bottom-of-empty is 0,
      between(0, 1024) is 512, between(5, 6) triggers renumber, order is
      preserved after renumber. Register it scoped in `AddHatchModule`.
- [ ] Create `ProjectsController.cs` and `StatusesController.cs` with the CRUD
      rules from the API table (key regex `^[A-Z][A-Z0-9]{1,5}$`, key
      immutability, 409s with a plain-text reason for the delete guards).
- [ ] Create `IssuesController.cs`: POST create with the retry-loop numbering
      exactly as specified under **Issue numbering**; GET by key (split on the
      last `-`, look up project by key, then `(ProjectId, Number)` — unknown
      key is 404); PATCH writing one event per changed field; DELETE.
      Parent validation per the Domain model rules — walk ancestors to refuse
      cycles, 400 with a reason.
- [ ] Add the `move` endpoint to `IssuesController.cs` calling `RankService`,
      and `BoardController.cs` returning statuses + issues ordered by
      `(StatusId, Rank, Id)`.
- [ ] Add comments GET/POST and events GET to `IssuesController.cs` (or a
      sibling controller if it crowds past ~300 lines).
- [ ] Add `src/Aerie.Api.Tests/Hatch/IssuesControllerTests.cs` mirroring
      `Quill/QuillControllerTests.cs`: numbers increment per project and are
      independent across projects; `AER-1` resolves and `AER-999`/`ZZZ-1` 404;
      cycle parenting and cross-project parenting are refused; each PATCH field
      writes its event; move across columns writes `status_changed` and a
      within-column move writes nothing; project/status delete guards 409.

Verify: `make test-api`, then `make run` and exercise
`http://localhost:5197/swagger` — create a project `AER`, an epic, a story
under it, move the story, comment on it, read its events.

## Phase 2 — Web app skeleton: workspace app, gating, registries

An `apps/hatch` SPA that builds, lints, mounts under the api, and 404s for
non-admins — empty pages, real plumbing. Patterns: [`apps/docs`](../../src/Aerie.Web/apps/docs)
for scaffolding (newest small app), [`apps/admin`](../../src/Aerie.Web/apps/admin)
for `App.tsx`/TopBar/clientLogger shape.

- [ ] Scaffold `src/Aerie.Web/apps/hatch/` by copying `apps/docs`'s
      `package.json` (name `aerie-hatch`, keep `marked` + `dompurify` +
      `react-router-dom`, drop `marked-gfm-heading-id`), `tsconfig*.json`,
      `.oxlintrc.json`, `index.html` (title **Hatch**, 🐣 favicon following the
      docs app's `public/` pattern), and `vite.config.ts` with
      `base: '/apps/hatch/'`, `aerieRevision({ app: 'hatch' })`, and
      `outDir: .../wwwroot/apps/hatch`. Run `npm install` at the
      `src/Aerie.Web` workspace root to update the one lockfile.
- [ ] Create `src/App.tsx` mirroring admin's: `<TopBar appName="Hatch">` from
      `@aerie/ui`, router with routes `/` (Board), `/projects`, `/statuses`,
      `/issues/:key`, `/import` — each a stub component in `src/pages/`. Copy
      admin's `clientLogger` wiring and `ErrorBoundary` pattern.
- [ ] In [`Program.cs`](../../src/Aerie.Api/Program.cs): add
      `app.MapFallbackToFile("/apps/hatch/{*path:nonfile}", "apps/hatch/index.html");`
      beside the admin fallback (~line 549) and an
      `opt.AddRedirect("^apps/hatch$", "apps/hatch/");` beside admin's
      (~line 586).
- [ ] Generalize
      [`AdminAppMiddleware`](../../src/Aerie.Api/Common/AdminAppMiddleware.cs):
      the single `AdminApp` PathString becomes a
      `static readonly PathString[] GatedApps = { "/apps/admin", "/apps/hatch" }`
      checked in a loop; update the class doc comment to say the boundary now
      covers every operator-only bundle. Extend the existing middleware tests
      under `src/Aerie.Api.Tests/Common/` with the `/apps/hatch` refusal case
      (404, `no-store`) and the pass-through case.
- [ ] Register the app everywhere the house enumerates apps to build: add
      `hatch` to the `for app in ...` list in the [`Makefile`](../../Makefile)
      `test-web` target, and to the `matrix.app` list in
      [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml) (~line 33).

Verify: `cd src/Aerie.Web && npm run lint -w apps/hatch && npm run build -w apps/hatch`,
then `make build` and `make run` — `http://localhost:5197/apps/hatch/` renders
the stub shell with the house top bar (Program.cs mounts a SPA only if its
`wwwroot` directory exists, so build the bundle before running). `make test-api`
for the middleware tests.

## Phase 3 — Board and issue UI

The tool becomes usable: board with drag, issue detail with markdown, project
and status management. All data via a thin same-origin client; the operator
does the visual pass — the implementer's definition of done is lint + build +
tests green.

- [ ] Create `src/api/client.ts`: typed fetch wrappers for every Phase 1
      endpoint, DTO types matching `Dtos.cs`, non-2xx responses throw with the
      server's reason text.
- [ ] Build the Board page: one column per status in `SortOrder` order, cards
      ordered by rank showing key, a type badge, and title; card click
      navigates to `/issues/:key`. Data refetches after every action and on
      window focus.
- [ ] Add drag with `@dnd-kit/core` + `@dnd-kit/sortable` (workspace-installed
      in `apps/hatch` only): dropping calls the `move` endpoint with `statusId`
      and the neighbor-derived `afterKey`/`beforeKey`, applies optimistically,
      and refetches on error.
- [ ] Add issue creation: a "New issue" button on the board opening a dialog —
      project select, type select, title input, description textarea — POSTs
      and refetches.
- [ ] Build the issue detail page: title (inline edit), type/status selects,
      parent picker (issues of the same project filtered to the legal parent
      types, clearable — the sheet asks for the epic to be easily mutable),
      description as a raw-markdown textarea with a preview toggle rendered
      via `marked` + `dompurify` (mirror how `apps/docs` sanitizes), comments
      list with an add box, and the event trail collapsed at the bottom.
      Delete lives here, behind a confirm.
- [ ] Build the Projects page (list, create with key+name, rename, delete with
      the 409 reason surfaced) and the Statuses page (list in `SortOrder`
      order, rename, up/down reorder writing `SortOrder`, add, terminal
      toggle, delete with the 409 reason surfaced).

Verify: `cd src/Aerie.Web && npm run lint -w apps/hatch && npm run test --if-present -w apps/hatch && npm run build -w apps/hatch`,
then `make run` for the operator's hands-on pass.

## Phase 4 — hatch.${DOMAIN}: ingress, picker tile, ship it

Hatch gets its address and its tile. This is the MVP ship point for the
human-only tool. Pattern:
[`reverse-proxy-architecture.md`](../reverse-proxy-architecture.md) ("Adding a
new hostname") and the kiosk pair in
[`charts/aerie/templates`](../../charts/aerie/templates). Remember `prune`
is live and a missing middleware annotation fails silently as an ungated route
— the 302 check below is the only proof.

- [ ] Create `charts/aerie/templates/middleware-hatch.yaml`: a
      `replacePathRegex` middleware `hatch-root-rewrite` mapping `^/$` to
      `/apps/hatch/` (copy `middleware-kiosk.yaml`, comment included).
- [ ] Add a `hatch` Ingress to `charts/aerie/templates/ingress.yaml`: host
      `hatch.{{ .Values.domain }}`, backend `api:8080`, and the same composed
      middleware annotation the `kiosk` Ingress uses — auth first when
      `auth.mode` is `full`, then `hatch-root-rewrite` — with the
      `<namespace>-<name>@kubernetescrd` form.
- [ ] Add the picker tile in
      [`apps/home/src/apps.ts`](../../src/Aerie.Web/apps/home/src/apps.ts):
      operator tier, icon 🐣, `subdomain: 'hatch'`, `withheldIf404: true` —
      and generalize `lib/useWithheldApps.ts` so a subdomain entry can name the
      same-origin probe path (`/apps/hatch/`) it HEADs, updating the "only
      admin sets this" comments in both files.
- [ ] Update the hostname table in
      [`reverse-proxy-architecture.md`](../reverse-proxy-architecture.md) and
      add `hatch` to the asserted list in
      [`scripts/k3s/Test-NameResolution.ps1`](../../scripts/k3s/Test-NameResolution.ps1).

Verify: `make test-web`, `make test-api`. After the operator pushes and Flux
reconciles: an un-enrolled browser at `hatch.${DOMAIN}` gets a 302 to sign-in
(this is the only proof the wall is attached), an enrolled admin device gets
the board, a non-admin enrolled device gets a 404, and the picker on
`home.${DOMAIN}` shows the 🐣 tile only to admins.

## Phase 5 — Plans importer

The `docs/plans/` directory becomes uploadable. The importer is read-only: it
copies plans in, and retiring the `.md` files stays a deliberate manual act per
the [plans lifecycle](README.md). Parsing is server-side and deterministic.

Parse rules, per uploaded file: the first `#` H1 is the epic title (fallback:
filename); everything before the first phase heading is the epic description.
Every `##` H2 whose text starts with `Phase` (case-insensitive) opens a story
titled with the H2 text; other H2 sections stay in the epic description. Inside
a story's section, every top-level `- [ ]` / `- [x]` list item is a task
(nested checkboxes flatten, in order); non-checkbox lines become the story
description. Checked tasks land in the terminal status (lowest `SortOrder`
among `IsTerminal`, else the last status), unchecked in `todo`. A story with
boxes lands `done` if all are checked, `in progress` if some are, `todo`
otherwise; a boxless story lands `todo`; the epic lands `done` only if every
story did. Every imported description ends with an `_Imported from
`filename`_` line, and every created issue gets one `imported` event carrying
the filename.

- [ ] Create `src/Aerie.Api/Modules/Hatch/PlanImportParser.cs`: pure function
      from `(filename, content)` to a `ParsedEpic` tree DTO implementing
      exactly the rules above — no DB access, registered in `AddHatchModule`.
- [ ] Add `src/Aerie.Api.Tests/Hatch/PlanImportParserTests.cs` with inline
      markdown fixtures: a full doc with phases and mixed checkboxes, a doc
      with no phases (epic only), a doc with H2s that aren't phases, checked
      state mapping, and this plan file's own shape (H1 + `## Phase N — title`
      + checkboxes) parsing to the expected counts.
- [ ] Create `ImportController.cs`: `preview` accepting multipart `.md`
      uploads (reject non-`.md`, cap 1 MB/file) returning the trees;
      `import` accepting `{ projectId, docs }` and creating epics, stories,
      and tasks with parents, statuses, ranks, and the numbering path from
      Phase 1 — plus `src/Aerie.Api.Tests/Hatch/ImportControllerTests.cs`
      asserting the created hierarchy, statuses, and `imported` events.
- [ ] Build the Import page in `apps/hatch`: multi-file input, preview tree
      with per-file epic/story/task counts and statuses, project picker,
      import button, and a result list linking each created epic.

Verify: `make test-api`, the `apps/hatch` lint/build pair, then a hands-on
round trip: upload a copy of a small real plan, check the preview counts, and
import into a scratch project.

## Phase 6 — API keys: Claude gets hands

Programmatic access through the same wall, decided by the same gate. A key is
a first-class credential: named (that name is the audit actor), hashed at
rest, scoped, revocable from the admin app. Read
[`AuthGate.cs`](../../src/Aerie.Api/Services/Auth/AuthGate.cs),
[`AuthMiddleware.cs`](../../src/Aerie.Api/Common/AuthMiddleware.cs), and
[`AdminGate.cs`](../../src/Aerie.Api/Services/Auth/AdminGate.cs) before
touching any of them — the one-gate/one-allow-list property is the design.

- [ ] Add `EfApiKey` to the core `public` schema (new `Ef/ApiKeys.cs`, DbSet
      on `AerieContext`, migration via
      `make ef-migration migration=ApiKeys`): `Id`, `Name` (unique), `Prefix`
      (first 12 chars of the secret, display only), `Hash` (SHA-256 of the
      full secret), `Scopes` (text[]), `CreatedAt`, `LastUsedAt` (nullable),
      `RevokedAt` (nullable). Secrets are `aerie_ak_` + 32 chars from
      `RandomNumberGenerator`, shown exactly once at mint. Core schema, not
      `hatch`: the wall reads it, and no module may own what the wall reads.
- [ ] Teach the wall the bearer lane: `AuthMiddleware` and
      `AuthController.Verify` extract an `Authorization: Bearer aerie_ak_…`
      header (Verify reads it from the forwarded request, same as it treats
      cookies) and pass it to `AuthGate.EvaluateAsync` as a distinct
      credential; the gate hashes it, matches an unrevoked key, and returns a
      new authenticated-as-key decision (`AuthDecision` gains an
      `EfApiKey? ApiKey`; new refusal reason `unknown_key`; no cookie
      re-issue on this lane; `LastUsedAt` updated at most once a minute).
- [ ] Scope the admin gate: `RequireAdminAttribute` gains an optional
      `AcceptScope` string, `AdminGate.EvaluateAsync` allows a key-caller iff
      the attribute names a scope the key carries (person logic unchanged;
      key-callers hitting plain `[RequireAdmin]` get the 403); every Hatch
      controller switches to `[RequireAdmin(AcceptScope = "hatch")]`. Expose
      the actor through `ICallerIdentity` (an `ActorName` that answers person
      name, else key name, else `"operator"`) and use it for Hatch's
      `CreatedBy`/`Actor`/`Author` fields.
- [ ] Add key management to core auth: `GET/POST /api/auth/keys` and
      `POST /api/auth/keys/{id}/revoke` on a controller carrying plain
      `[RequireAdmin]` (humans only mint keys), plus an **API Keys** page in
      `apps/admin` beside Sessions — list (name, prefix, created, last used,
      revoked), mint dialog (name; scopes fixed to `hatch` for now), revoke
      with confirm, and the show-once secret presentation.
- [ ] Tests in `src/Aerie.Api.Tests/Auth/`: gate accepts a valid key, refuses
      revoked/unknown, scope mismatch 403s, plain `[RequireAdmin]` refuses
      key-callers, mint-then-use round-trips, and the hash — never the
      secret — is what the row stores.
- [ ] Write the contract into `CLAUDE.md`: the key lives outside the repo (a
      local secrets file, never committed — Hatch ships to other operators);
      given a `hatch.${DOMAIN}/issues/AER-n` link or bare key, fetch
      `GET /api/hatch/issues/AER-n` with the bearer header and get to work; a
      planning session PATCHes acceptance criteria into the description,
      creates child stories/tasks, and moves the ticket `inbox → todo`;
      implementation moves `todo → in progress` and comments with commit/PR
      references; only the operator moves tickets to `done`.

Verify: `make test-api`, `make test-web` (admin app changed). End-to-end after
deploy: `curl -H "Authorization: Bearer $KEY" https://hatch.${DOMAIN}/api/hatch/board`
returns the board through Traefik (forwardAuth hands Verify the original
request headers — this curl is the proof), the same curl with a revoked key
gets 401, and against `/api/devices` gets 403.

## Phase 7 — Migrate and dissipate

Hatch becomes the system of record and this plan eats itself. Operator-paced —
each step is deliberate, none is automated.

- [ ] Create the real projects in the UI — `AER` for the platform, and carve
      off others (the trading silo is the stated candidate) as feels right.
- [ ] Import the active plans from `docs/plans/` one at a time via the Import
      page, eyeballing each epic against its source before moving on.
- [ ] Retire each verified plan file manually (`git rm`), updating the
      Current-plans table in [`README.md`](README.md) — the lifecycle's step 3,
      done per-plan rather than in bulk.
- [ ] Write `docs/hatch.md` — the permanent architecture doc: domain model,
      the admin-gated posture, the API-key mechanics and their one-gate
      rationale, and the operator/Claude workflow contract. Link it from
      `Modules/README.md`'s examples and the README app list.
- [ ] Delete this file.
