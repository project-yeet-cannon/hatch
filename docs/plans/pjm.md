# Hatch — the house project tracker

Status: phases 0-6 complete 2026-09-03 — Hatch is shipped for the operator and
for Claude: the module, the API, the board, `hatch.${DOMAIN}`, the plans
importer, and wall-level API keys. Phase 7 (migrate and dissipate) remains, and
is operator-paced. A round of ergonomics landed on top of the plan afterwards —
see [After the plan](#after-the-plan) for what that changed about the decisions
below.

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
  statuses-as-columns. *(Board filtering and a bulk editor landed after the
  plan — see [After the plan](#after-the-plan). Swimlanes, sprints and WIP
  limits are still non-goals.)*
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
`^[A-Z][A-Z0-9]{1,5}$`), `Name` (text), `NextIssueNumber` (int, default 1,
`[ConcurrencyCheck]`), `CreatedAt`. The key was immutable in the MVP, on the
grounds that renaming one orphans every `AER-n` reference in the world; it is
now changeable behind a speed bump, which is the same argument with the operator
allowed to make the call — see [After the plan](#after-the-plan).

**`EfHatchStatus`** — `Id` (int, identity), `Name` (text, unique),
`SortOrder` (int; column order on the board), `IsTerminal` (bool; marks "done"
columns so later reporting knows what shipped), `Color` (text, `#rrggbb`; added
after the plan). Seeded in the Init migration: inbox (10), todo (20), in
progress (30), done (40, terminal).

**`EfHatchIssue`** — `Id` (long, identity), `ProjectId` (FK), `Number` (int;
unique index on `(ProjectId, Number)` as the numbering backstop), `Type` (text:
`epic|story|task|bug`), `Title` (text), `Description` (text, markdown, default
empty), `StatusId` (FK), `ParentId` (nullable self-FK), `Rank` (long),
`ReadyAt`/`DueAt` (nullable timestamps, each with a `…HasTime` bool),
`CreatedBy` (text actor name), `CreatedAt`, `UpdatedAt`. The display key
`AER-12` is computed (`Project.Key + "-" + Number`), never stored.

The two dates are a pair with different jobs: `ReadyAt` is a gate — an issue
whose ready date has not arrived is folded off the board, so next August's
certificate renewal can be filed the day the certificate is bought — and
`DueAt` is a deadline, drawn as a chip that warms from three days out. Both are
optional and both are written on the wire as one string per field, either a
date (`2027-08-15`) or an instant (`2027-09-01T17:00:00Z`); the `HasTime` bool
is what keeps those two apart in the database, because a bare date has to be
pinned to some midnight to be stored and a reader west of UTC would otherwise
draw the day before. Only formal validity is checked: a past date is a fact
worth recording, and a ready date after a due date is a mix-up worth seeing on
the card rather than one worth a 400.

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
| `/api/hatch/board` | GET | Statuses (sorted) + all issues (key, type, title, statusId, rank, parentKey, projectKey, readyAt, dueAt), ordered by `(StatusId, Rank, Id)`. Never filtered — the browser folds not-yet-ready cards away, the server hands over all of them |
| `/api/hatch/issues` | POST | `{ projectId, type, title, description?, parentKey?, readyAt?, dueAt? }` — mints number, lands in the lowest-`SortOrder` status, rank = bottom of column, writes `created` |
| `/api/hatch/issues/{key}` | GET, PATCH, DELETE | GET includes parent/children keys and both dates. PATCH: title/description/type/statusId/parentKey/readyAt/dueAt, one event per changed field; `""` clears a parent or a date. DELETE: confirm-worthy, cascades |
| `/api/hatch/issues/{key}/move` | POST | `{ statusId, afterKey?, beforeKey? }` — server computes rank; writes `status_changed` only if the column changed |
| `/api/hatch/issues/{key}/comments` | GET, POST | POST writes `commented` |
| `/api/hatch/issues/{key}/events` | GET | Newest first |
| `/api/hatch/import/preview` | POST | Multipart `.md` files → parse tree, touches nothing |
| `/api/hatch/import/preview-text` | POST | `{ title, body }` → one parse tree, touches nothing. The same parser as `preview`, for a plan that was pasted rather than uploaded; the title plays the filename's part — the provenance every issue carries, and the epic's title when the body has no `#` heading |
| `/api/hatch/import` | POST | `{ projectId, docs }` → creates the hierarchy, whichever preview produced them |

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

- [x] Create `src/Aerie.Api/Modules/Hatch/Entities.cs` with the five entities
      exactly as the Domain model section above specifies, and
      `src/Aerie.Api/Modules/Hatch/HatchContext.cs` with
      `public const string Schema = "hatch"`, a `DbSet` per entity, the
      `IModuleContext` marker, and `OnModelCreating` pinning the schema and
      declaring: unique index on `EfHatchProject.Key`, unique index on
      `(EfHatchIssue.ProjectId, Number)`, unique index on `EfHatchStatus.Name`,
      cascade deletes from issue to comments/events, restrict on
      issue→status and issue→project, and `Payload` mapped as `jsonb`.
- [x] Create `src/Aerie.Api/Modules/Hatch/HatchDesignTimeFactory.cs`
      (three lines, mirror `QuillDesignTimeFactory.cs`).
- [x] Create `src/Aerie.Api/Modules/Hatch/HatchModule.cs` with
      `AddHatchModule(...)` registering the context (mirror `QuillModule.cs`),
      and add the one `services.AddHatchModule(configuration);` line to
      `AddAerieModules` in
      [`ModuleRegistration.cs`](../../src/Aerie.Api/Modules/ModuleRegistration.cs).
- [x] Scaffold the Init migration into the module folder:
      `dotnet ef migrations add Init --context HatchContext --project ./src/Aerie.Api/Aerie.Api.csproj -o Modules/Hatch/Migrations`,
      then hand-add four `migrationBuilder.InsertData` calls seeding the
      statuses (inbox/10, todo/20, in progress/30, done/40 with
      `IsTerminal = true`) so every install starts with a working board.
- [x] Add `src/Aerie.Api.Tests/Hatch/HatchContextTests.cs` mirroring
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

- [x] Create `Dtos.cs`: records for project/status/issue/comment/event
      responses and the create/patch/move request bodies from the API table.
      Issue DTOs carry the computed `key` and `parentKey`, never raw ids alone.
- [x] Create `RankService.cs` implementing the Rank spec (bottom / between /
      renumber-on-exhaustion), plus
      `src/Aerie.Api.Tests/Hatch/RankServiceTests.cs`: bottom-of-empty is 0,
      between(0, 1024) is 512, between(5, 6) triggers renumber, order is
      preserved after renumber. Register it scoped in `AddHatchModule`.
- [x] Create `ProjectsController.cs` and `StatusesController.cs` with the CRUD
      rules from the API table (key regex `^[A-Z][A-Z0-9]{1,5}$`, key
      immutability, 409s with a plain-text reason for the delete guards).
- [x] Create `IssuesController.cs`: POST create with the retry-loop numbering
      exactly as specified under **Issue numbering**; GET by key (split on the
      last `-`, look up project by key, then `(ProjectId, Number)` — unknown
      key is 404); PATCH writing one event per changed field; DELETE.
      Parent validation per the Domain model rules — walk ancestors to refuse
      cycles, 400 with a reason.
- [x] Add the `move` endpoint to `IssuesController.cs` calling `RankService`,
      and `BoardController.cs` returning statuses + issues ordered by
      `(StatusId, Rank, Id)`.
- [x] Add comments GET/POST and events GET to `IssuesController.cs` (or a
      sibling controller if it crowds past ~300 lines).
- [x] Add `src/Aerie.Api.Tests/Hatch/IssuesControllerTests.cs` mirroring
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

- [x] Scaffold `src/Aerie.Web/apps/hatch/` by copying `apps/docs`'s
      `package.json` (name `aerie-hatch`, keep `marked` + `dompurify` +
      `react-router-dom`, drop `marked-gfm-heading-id`), `tsconfig*.json`,
      `.oxlintrc.json`, `index.html` (title **Hatch**, 🐣 favicon following the
      docs app's `public/` pattern), and `vite.config.ts` with
      `base: '/apps/hatch/'`, `aerieRevision({ app: 'hatch' })`, and
      `outDir: .../wwwroot/apps/hatch`. Run `npm install` at the
      `src/Aerie.Web` workspace root to update the one lockfile.
- [x] Create `src/App.tsx` mirroring admin's: `<TopBar appName="Hatch">` from
      `@aerie/ui`, router with routes `/` (Board), `/projects`, `/statuses`,
      `/issues/:key`, `/import` — each a stub component in `src/pages/`. Copy
      admin's `clientLogger` wiring and `ErrorBoundary` pattern.
- [x] In [`Program.cs`](../../src/Aerie.Api/Program.cs): add
      `app.MapFallbackToFile("/apps/hatch/{*path:nonfile}", "apps/hatch/index.html");`
      beside the admin fallback (~line 549) and an
      `opt.AddRedirect("^apps/hatch$", "apps/hatch/");` beside admin's
      (~line 586).
- [x] Generalize
      [`AdminAppMiddleware`](../../src/Aerie.Api/Common/AdminAppMiddleware.cs):
      the single `AdminApp` PathString becomes a
      `static readonly PathString[] GatedApps = { "/apps/admin", "/apps/hatch" }`
      checked in a loop; update the class doc comment to say the boundary now
      covers every operator-only bundle. Extend the existing middleware tests
      under `src/Aerie.Api.Tests/Common/` with the `/apps/hatch` refusal case
      (404, `no-store`) and the pass-through case.
- [x] Register the app everywhere the house enumerates apps to build: add
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

- [x] Create `src/api/client.ts`: typed fetch wrappers for every Phase 1
      endpoint, DTO types matching `Dtos.cs`, non-2xx responses throw with the
      server's reason text.
- [x] Build the Board page: one column per status in `SortOrder` order, cards
      ordered by rank showing key, a type badge, and title; card click
      navigates to `/issues/:key`. Data refetches after every action and on
      window focus.
- [x] Add drag with `@dnd-kit/core` + `@dnd-kit/sortable` (workspace-installed
      in `apps/hatch` only): dropping calls the `move` endpoint with `statusId`
      and the neighbor-derived `afterKey`/`beforeKey`, applies optimistically,
      and refetches on error.
- [x] Add issue creation: a "New issue" button on the board opening a dialog —
      project select, type select, title input, description textarea — POSTs
      and refetches.
- [x] Build the issue detail page: title (inline edit), type/status selects,
      parent picker (issues of the same project filtered to the legal parent
      types, clearable — the sheet asks for the epic to be easily mutable),
      description as a raw-markdown textarea with a preview toggle rendered
      via `marked` + `dompurify` (mirror how `apps/docs` sanitizes), comments
      list with an add box, and the event trail collapsed at the bottom.
      Delete lives here, behind a confirm.
- [x] Build the Projects page (list, create with key+name, rename, delete with
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

- [x] Create `charts/aerie/templates/middleware-hatch.yaml`: a
      `replacePathRegex` middleware `hatch-root-rewrite` mapping `^/$` to
      `/apps/hatch/` (copy `middleware-kiosk.yaml`, comment included).
- [x] Add a `hatch` Ingress to `charts/aerie/templates/ingress.yaml`: host
      `hatch.{{ .Values.domain }}`, backend `api:8080`, and the same composed
      middleware annotation the `kiosk` Ingress uses — auth first when
      `auth.mode` is `full`, then `hatch-root-rewrite` — with the
      `<namespace>-<name>@kubernetescrd` form.
- [x] Add the picker tile in
      [`apps/home/src/apps.ts`](../../src/Aerie.Web/apps/home/src/apps.ts):
      operator tier, icon 🐣, `subdomain: 'hatch'`, `withheldIf404: true` —
      and generalize `lib/useWithheldApps.ts` so a subdomain entry can name the
      same-origin probe path (`/apps/hatch/`) it HEADs, updating the "only
      admin sets this" comments in both files.
- [x] Update the hostname table in
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

A plan does not have to be a file to be worth filing, so there are two roads in
and one parser at the end of both: an upload of `.md` files, and a title and a
body typed into the page. Below, *source name* is the filename on the first road
and the typed title on the second — it is the same argument to the same
function, which is why the second road needed no second reading of markdown.

Parse rules, per document: the first `#` H1 is the epic title (fallback: the
source name); everything before the first phase heading is the epic description.
Every `##` H2 whose text starts with `Phase` (case-insensitive) opens a story
titled with the H2 text; other H2 sections stay in the epic description. Inside
a story's section, every top-level `- [x]` / `- [x]` list item is a task
(nested checkboxes flatten, in order); non-checkbox lines become the story
description. Checked tasks land in the terminal status (lowest `SortOrder`
among `IsTerminal`, else the last status), unchecked in `todo`. A story with
boxes lands `done` if all are checked, `in progress` if some are, `todo`
otherwise; a boxless story lands `todo`; the epic lands `done` only if every
story did. Every imported description ends with an `_Imported from
`source`_` line, and every created issue gets one `imported` event carrying
the source name.

- [x] Create `src/Aerie.Api/Modules/Hatch/PlanImportParser.cs`: pure function
      from `(filename, content)` to a `ParsedEpic` tree DTO implementing
      exactly the rules above — no DB access, registered in `AddHatchModule`.
- [x] Add `src/Aerie.Api.Tests/Hatch/PlanImportParserTests.cs` with inline
      markdown fixtures: a full doc with phases and mixed checkboxes, a doc
      with no phases (epic only), a doc with H2s that aren't phases, checked
      state mapping, and this plan file's own shape (H1 + `## Phase N — title`
      + checkboxes) parsing to the expected counts.
- [x] Create `ImportController.cs`: `preview` accepting multipart `.md`
      uploads (reject non-`.md`, cap 1 MB/file) returning the trees;
      `import` accepting `{ projectId, docs }` and creating epics, stories,
      and tasks with parents, statuses, ranks, and the numbering path from
      Phase 1 — plus `src/Aerie.Api.Tests/Hatch/ImportControllerTests.cs`
      asserting the created hierarchy, statuses, and `imported` events.
- [x] Build the Import page in `apps/hatch`: multi-file input, preview tree
      with per-file epic/story/task counts and statuses, project picker,
      import button, and a result list linking each created epic.
- [x] Add the second road: `preview-text` taking `{ title, body }` through the
      same parser, and a Title/Body form beside the file input. The preview
      lists every task as well as every phase — the phase titles are the
      document's own headings, but a task title is the parser's guess at where
      a sentence ends, so the chop is the part that wants checking before forty
      issues land on a board with no undo.

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

- [x] Add `EfApiKey` to the core `public` schema (new `Ef/ApiKeys.cs`, DbSet
      on `AerieContext`, migration via
      `make ef-migration migration=ApiKeys`): `Id`, `Name` (unique), `Prefix`
      (first 12 chars of the secret, display only), `Hash` (SHA-256 of the
      full secret), `Scopes` (text[]), `CreatedAt`, `LastUsedAt` (nullable),
      `RevokedAt` (nullable). Secrets are `aerie_ak_` + 32 chars from
      `RandomNumberGenerator`, shown exactly once at mint. Core schema, not
      `hatch`: the wall reads it, and no module may own what the wall reads.
- [x] Teach the wall the bearer lane: `AuthMiddleware` and
      `AuthController.Verify` extract an `Authorization: Bearer aerie_ak_…`
      header (Verify reads it from the forwarded request, same as it treats
      cookies) and pass it to `AuthGate.EvaluateAsync` as a distinct
      credential; the gate hashes it, matches an unrevoked key, and returns a
      new authenticated-as-key decision (`AuthDecision` gains an
      `EfApiKey? ApiKey`; new refusal reason `unknown_key`; no cookie
      re-issue on this lane; `LastUsedAt` updated at most once a minute).
- [x] Scope the admin gate: `RequireAdminAttribute` gains an optional
      `AcceptScope` string, `AdminGate.EvaluateAsync` allows a key-caller iff
      the attribute names a scope the key carries (person logic unchanged;
      key-callers hitting plain `[RequireAdmin]` get the 403); every Hatch
      controller switches to `[RequireAdmin(AcceptScope = "hatch")]`. Expose
      the actor through `ICallerIdentity` (an `ActorName` that answers person
      name, else key name, else `"operator"`) and use it for Hatch's
      `CreatedBy`/`Actor`/`Author` fields.
- [x] Add key management to core auth: `GET/POST /api/auth/keys` and
      `POST /api/auth/keys/{id}/revoke` on a controller carrying plain
      `[RequireAdmin]` (humans only mint keys), plus an **API Keys** page in
      `apps/admin` beside Sessions — list (name, prefix, created, last used,
      revoked), mint dialog (name; scopes fixed to `hatch` for now), revoke
      with confirm, and the show-once secret presentation.
- [x] Tests in `src/Aerie.Api.Tests/Auth/`: gate accepts a valid key, refuses
      revoked/unknown, scope mismatch 403s, plain `[RequireAdmin]` refuses
      key-callers, mint-then-use round-trips, and the hash — never the
      secret — is what the row stores.
- [x] Write the contract into `CLAUDE.md`: the key lives outside the repo (a
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

## After the plan

Phases 0-6 shipped the tracker. Using it for a week produced a round of
ergonomics that is not a phase - there was no plan to work, only a list - and is
recorded here because three of the items reverse a decision stated above.

- **Columns carry a colour.** `EfHatchStatus.Color`, `#rrggbb`, operator-editable
  on the Statuses page. A row rather than a palette keyed on the four seeded
  names: the operator invents columns, and a lookup by name would leave a new
  one grey forever and lose a renamed one's colour. The ink written on a colour
  is computed from its luminance rather than chosen (`lib/color.ts`), because
  CSS still cannot ask that question.
- **A project key can change.** *Reverses "immutable after creation".* The cost
  is unchanged and is now stated to the operator instead of being decided for
  them: text references (`AER-12` in a commit message, a branch name, a chat
  log) go dead, while the structure comes through intact, because parentage is a
  foreign key and the number is the issue's own. The speed bump is the browser's
  - type the old key to confirm - and the API simply refuses a bad or taken one.
- **The board filters.** *Narrows "no filtration".* A toggle per type and a dumb
  substring search over key, title, type and parent, done in the browser over
  the board already in hand - a filter that refetched would drop the drag in
  progress. Placement moved to `lib/place.ts` so a drop lands next to the card
  the operator can see rather than next to a hidden row at the same index.
- **`GET /api/hatch/issues` and `POST /api/hatch/issues/bulk`.** A filter
  (project, type, column, parent, ancestor at any depth, title substring) and one
  edit applied to many issues. The bulk path shares the single-issue edit path,
  which was pulled apart so that everything refusable is looked up before
  anything is written - that ordering is what lets one issue's refusal leave
  that issue untouched while the batch around it goes through.
- **The board is denser and says more during a drag.** Columns are grid tracks
  sharing the width, cards are narrower with titles cut to a few hundred
  characters and three lines, a card follows the cursor, and the column a drop
  would land in lights up in its own colour. Clicking a card opens a summary
  built from what the board already downloaded rather than loading the issue.
- **Status is the issue page's own band**, in its column's colour, with every
  column beside it as one press each.

### The agent round

A second round, larger than the first, which turns the tracker into something
an unattended session works rather than something a person reads to a session.

- **A review column.** Seeded left of the first terminal one. The flow now has
  an end an agent may reach (`review`) and an end only the operator may
  (`done`), which is what makes "never move a ticket to a terminal status"
  something the board expresses rather than something a prompt asks for.
- **Playbooks.** *Narrows "no board configuration beyond statuses-as-columns".*
  A row per (transition, issue types) carrying a prompt, a model and an effort
  level. The matrix exists because "do the next increment" is not one job:
  turning a paragraph into an epic with stories under it is the hardest
  thinking in the flow and wants the largest model at the highest effort, while
  picking up a specified task and writing the code is ordinary work. Seeded
  with six rows; retuned on a page, because nobody guesses the right effort for
  a transition first time - they find it after watching a run go badly.
- **Writing a playbook is closed to an API key.** `[RequireAdmin]` naming no
  scope, on the three writes. This is the one edge in the graph that closes a
  loop: an agent able to widen its own prompt and raise its own budget has no
  fixed point to settle at, and the failure is unbounded spend rather than a
  wrong answer. Cutting it costs nothing and is cut in the route rather than
  asked for in a prompt.
- **`GET /api/hatch/work/next` and `/api/hatch/work/{key}`.** What to do next
  and how, in one request - which issue, which way it is going, whether it may
  go there, and which playbook speaks for the move. Server-side for the reason
  the rank is: it keeps every client dumb. The board is worked *right to left*,
  because a board worked the other way starts everything and finishes nothing.
- **`scripts/hatch.sh work`.** Reads that answer and spawns a headless session
  with the playbook's prompt, model and effort - `--model` and `--effort`
  override for one run without touching the matrix. The loop the operator runs
  is now one command.

### Questions, on the ticket

The first round gave an agent everything it needed to work except a way to say
it could not. A headless session that reached a decision it had no standing to
make had one option - guess - and the guess landed in a transcript nobody reads
twice. This round gives that a shape, and the shape is the same one the ready
date has: something that would otherwise be prose in a description becomes a row
that the board and the dispatcher can act on.

- **A comment has a kind, and an answer names its question.** `Kind` is
  `""`, `question` or `answer`; an answer carries `AnswersId`, pointing at the
  question it settles on the same issue. Open is computed - a question nothing
  points at - and never stored, so a question cannot be open and answered at
  once because two writes disagreed. The link runs answer→question rather than
  the other way, which is what lets a decision be refined by a second answer
  without editing the first.
- **An open question blocks the dispatch.** A fourth refusal in
  `WorkController.Blocked`, so `work/next` folds the card past exactly as it
  folds one whose ready date has not arrived, and `work AER-12` by name comes
  back with the question rather than a session. Without this the loop's only
  reaction to a question is to ask it again on the next run.
- **The board says which cards are waiting.** `IssueCardDto.OpenQuestions`, a
  badge on the card and a *waiting on me* switch beside the type toggles. A
  question nobody can see is a question nobody answers, so the one thing not
  done here was folding a waiting card off the board.
- **`hatch.sh ask`, `questions`, and `answer`.** `answer` walks the open
  questions one at a time on the terminal and posts each reply as it is typed -
  serial because a list of six printed at once gets answered in aggregate, which
  is how a wrong assumption gets in. `work` prints whatever the run it just
  spawned asked for, with the link, so the questions are not left in the
  scrollback.
- **`hatch.sh work -i`.** The same ticket, playbook and budget in a session the
  operator sits in - no `bypassPermissions`, because that grant only exists
  because a print-mode run has nobody to answer a prompt. Where `ask` is how an
  unattended run raises a decision, this is how a watched one does.
- **Nothing stops an API key answering its own question**, and that is stated
  rather than papered over. A key is what `hatch.sh answer` types with and it is
  also what a spawned agent inherits; the server cannot tell them apart, and a
  check that looked like it could would be worse than none. What holds the loop
  shut is one step further out - the dispatch is refused while a question is
  open, and the dispatch is a command the operator types. A second scope minted
  for agents would close it properly, and is not worth a column until somebody
  wants it.

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
