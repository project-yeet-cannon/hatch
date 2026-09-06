# Hatch

## Summary

The house project tracker: a kanban board at `hatch.${DOMAIN}`, issues with
Jira-style keys (`AER-12`), operator-editable columns, comments, and an
append-only audit trail. It replaced a folder of markdown plan files, and it is
the system of record for what Aerie is working on.

The name is the product. You hatch a plan here, and epics hatch into stories
into shipped work.

It is spelled `Hatch` / `hatch` everywhere — C# namespace
`Aerie.Api.Modules.Hatch`, Postgres schema `hatch`, routes under `/api/hatch`,
bundle at `/apps/hatch/`, and its own host `hatch.${DOMAIN}`.

Four properties carry the design:

- **One board, every project.** A project is a key namespace, not a container.
  Switching boards to find out what is next is the thing the markdown files
  already did badly.
- **Behind the wall, and admin-gated on top of it.** The bundle 404s and the
  API 403s for anyone who is not the operator — the same posture the admin app
  takes, through the same gate.
- **Claude is a first-class caller.** An `Authorization: Bearer aerie_ak_…` key
  reaches `/api/hatch/*` and nothing else, decided by the same gate that
  decides everything else. A ticket link is a complete instruction, and the
  ticket is where the answer goes back.
- **Everything an agent needs to decide is decided on the server.** Which issue
  is next, which column it is headed for, whether it may go there, what it is
  told when it gets there, and what a subtree adds up to. A client that
  re-derived any of those would drift the first time a column was renamed.

The [workflow contract](#the-operator-and-claude-contract) — what an agent may
do to a ticket, and what only a person may — is the last section, and is
mirrored in [`CLAUDE.md`](../CLAUDE.md) because that is where an agent reads it.

## Goals

1. **A board the operator lives on** — every issue from every project on one
   kanban board, drag to change status, drag to reorder, always visible.
2. **Claude as a first-class user** — paste an issue link into a session and it
   reads the ticket and gets to work; a planning session writes the plan back
   onto the ticket and files the children.
3. **Audit from day one** — every mutation leaves an append-only event row, so
   throughput and cycle time are a query away rather than a migration away.
4. **The smallest tool that beats a markdown editor**, on the existing pod,
   with no new infrastructure.

## Non-goals

These are absences on purpose, and each one is cheap to add later if the
absence starts to hurt:

- **No status-transition rules.** Any column to any column; we trust ourselves.
  The one asymmetry in the flow is not a rule in the database — it is that an
  agent is never dispatched *into* a terminal column (see
  [the dispatcher](#the-dispatcher)). The second thing that could be mistaken
  for one is [closing a subtree](#closing-a-subtree), and it is not a rule
  either: it is a question the browser asks a person after a move has already
  committed. Nothing is forbidden by it and nothing is required — the answer is
  the operator's, and "leave them" is one of the two.
- **No granular permissions.** Reaching Hatch at all means trusted to do
  everything in it. The one exception is the API key, whose scope is a
  statement about *which surface*, never about which verb.
- **No swimlanes, sprints, or WIP limits.**
- **No GitHub integration.** A commit sha in a comment is the link, and it is
  written by whoever did the work.
- **No live board updates.** Refetch on action and on focus; no websockets.
- **Events are recorded, not rendered.** The Plan view answers "how far along
  is this" from current state; nothing draws the event log as a report.

## Where it lives

**Backend: a module.** `src/Aerie.Api/Modules/Hatch`, its own `hatch` schema,
its own migration history, one line in `AddAerieModules` — the shape
[`Modules/README.md`](../src/Aerie.Api/Modules/README.md) describes. Splitting
it into its own service later is a connection-string change, so "part of the
API for now" costs nothing architecturally.

**Frontend: its own workspace SPA**, not a family-shell module.
`src/Aerie.Web/apps/hatch`, built into `wwwroot/apps/hatch`, served by the same
pod — the arrangement the admin and docs apps already use. It is an operator's
tool, and the family shell is the household's.

**Host: `hatch.${DOMAIN}`**, pointed at the existing `api` Service with a
Traefik `replacePathRegex` rewriting `/…` to `/apps/hatch/…`
(`charts/aerie/templates/middleware-hatch.yaml`). No new container, no new
monitor, no new backup entry.

The rewrite covers *every* path, not just `/`, because
`hatch.${DOMAIN}/issues/AER-12` is the link that gets pasted into a chat window
and has to survive being opened cold. Three prefixes are kept off that router by
a second Ingress (`hatch-direct` in `ingress.yaml`) which carries no rewrite:
the bundle's own assets under `/apps/hatch/`, the sign-in shell at `/apps/auth/`,
and `/api/hatch/*` — which is how a key can `curl` the API on the same host the
browser uses. Because a client-side router cannot see any of this, the bundle
reads its basename off the URL (`lib/basename.ts`) rather than assuming a
prefix; the two have to change together, since a rewrite the app does not expect
renders a blank page with nothing in the network panel to explain it.

## Domain model

Six tables in the `hatch` schema, all carrying the house `Ef` prefix. The
authority is [`Entities.cs`](../src/Aerie.Api/Modules/Hatch/Entities.cs), which
carries the per-field reasoning; this is the shape and the decisions worth
having in one place.

### Project

`EfHatchProject` — `Key` (unique, `^[A-Z][A-Z0-9]{1,5}$`), `Name`,
`NextIssueNumber`, `CreatedAt`.

A project exists so an issue can be called `AER-12` rather than `#4471`, and so
two efforts can number themselves independently. It is deliberately **not a
container**: the board shows every project at once.

The key is changeable, behind a speed bump in the browser (type the old key to
confirm). The cost is stated to the operator rather than decided for them: every
`AER-12` written into a commit message, a branch name or a chat log goes dead,
because those are references this database has never seen and cannot rewrite.
What survives is the part that matters — parentage is a foreign key and the
number is the issue's own column, so a rekeyed project keeps every story under
its epic.

### Status

`EfHatchStatus` — `Name` (unique), `SortOrder`, `IsTerminal`, `Color`
(`#rrggbb`).

One row is one column on the board. **Global, not per-project**, because the
board shows every project at once and a per-project set would have no column to
put a foreign issue in. Rows rather than an enum because the operator renames
and reorders them from the Statuses page, and an enum would make "add a review
column" a deploy.

`SortOrder` is sparse (the seed is 10 through 70, in tens) so inserting a column
between two is one write.

`IsTerminal` marks the columns that mean *shipped*. Three things read it: the
importer lands a checked box in a terminal column, the dispatcher refuses to
move anything into one, and the browser offers to close a subtree when an issue
lands in one (see [closing a subtree](#closing-a-subtree)).

`Color` is a column rather than a palette keyed on the shipped names, because
the operator invents columns — a lookup by name would leave a new one grey
forever and lose a renamed one's colour. The ink written on a colour is computed
from its luminance (`lib/color.ts`), because CSS still cannot ask that question.

Every install starts with the same seven columns, and with the same flow
through them:

| Column | Sort | Terminal | Whose | What happens in it |
|---|---|---|---|---|
| Draft | 10 | | operator | An idea being written. Nothing reads it. |
| Breakdown | 20 | | **agent** | Turn the draft into a specification: acceptance criteria on the issue, children under it. |
| Backlog | 30 | | operator | Specified work, awaiting selection. |
| To Do | 40 | | **agent** | Pick it up and do it. |
| In Progress | 50 | | **agent** | Finish it, push it, put it up for review. |
| In Review | 60 | | operator | Read the pull request, wait for green, merge. |
| Done | 70 | ✓ | operator | Terminal. |

**Which column belongs to whom is not a field.** It is derived: a column an
agent may leave is a column some [playbook](#playbooks) names as its `from`, for
that issue's type. A missing playbook row is how a column becomes the
operator's, and it is also how an epic in Breakdown can stay the operator's
while a story in Breakdown is the agent's, because a playbook names types. A
second "whose column is this" flag would be free to disagree with the first, and
a flag the loop could read is one step from a flag the loop could set.

The order is the point. `Backlog` is the gate between "somebody wrote a
paragraph" and "a pull request is open", and `In Review` is the gate before
shipped — which is what makes "never move a ticket to a terminal status"
something the board expresses rather than something a prompt has to ask for.

Three migrations built it, and the last one is the one to read before changing
any of it. `Init` seeded the first four columns; `Playbooks` measured `review`
into place immediately left of the first terminal column; `TheFlow` renamed the
five for people, added `Breakdown` and `Backlog`, and moved the specifying
playbooks onto the transition they now describe.

`TheFlow` touches **only a board that is still exactly the one those two
migrations shipped** — the five columns, in their order, with their terminal
flags. Anything else and it does nothing at all: no rename, no insert, no
repoint. That is a harder guard than the measurement `Playbooks` used, and
deliberately: one column relative to the first terminal one is a question a
board can answer, but `Breakdown` means nothing except "between Draft and
Backlog", and there is no measurement that finds those two on a board somebody
arranged by hand. A board whose columns no longer alternate is worse than one
that was left alone, because the loop would run, and it would run straight
through the gate that was supposed to stop it.

### Issue

`EfHatchIssue` — `ProjectId`, `Number`, `Type`, `Title`, `Description`,
`StatusId`, `ParentId`, `Rank`, `ReadyAt`/`ReadyAtHasTime`,
`DueAt`/`DueAtHasTime`, `PullRequestUrl`, `CreatedBy`, `CreatedAt`,
`UpdatedAt`.

The display key `AER-12` is **computed** (`Project.Key + "-" + Number`) and
never stored, so there is exactly one fact about a key anywhere and no chance of
two copies disagreeing after a rename.

Types are `epic | story | task | bug`. Parentage is optional and validated
server-side: the parent must exist, be in the same project, form no cycle, and
be a legal type — epic→epic, story→epic, task→story or bug, bug→epic or story.

`Description` is markdown, stored exactly as typed and rendered by the client
(`marked` + `dompurify`, the pair the docs app already bundles). What the
database holds is what somebody wrote, and changing renderer is a frontend
change.

**The two dates are a pair with different jobs.** `ReadyAt` is a *gate*: an
issue whose ready date has not arrived is folded off the board, which is what
lets a ticket be filed the moment it is thought of rather than the moment it can
be started — buy a certificate in September and the renewal appears next August
on its own. `DueAt` is a *deadline*, drawn as a chip that warms from three days
out. Both are optional, and both are written on the wire as one string per
field: either a date (`2027-08-15`) or an instant (`2027-09-01T17:00:00Z`).

The `…HasTime` bool is what keeps those two apart, and it is load-bearing rather
than decorative. A bare date has to be pinned to *some* midnight to be stored,
and a reader west of UTC would otherwise draw the day before. So a bare date is
read in UTC, where its components are the ones that were typed, and an instant
is read in the caller's zone, where the hour somebody meant is the hour they
meant. [`IssueMoment.cs`](../src/Aerie.Api/Modules/Hatch/IssueMoment.cs) holds
the server half of that contract and `lib/schedule.ts` the browser's.

Only formal validity is checked. A past due date is accepted without comment —
half of what a tracker is for is recording that something was due last Tuesday,
and a form that argues about it is a form people stop telling the truth to — and
a ready date after a due date is a mix-up worth seeing on the card rather than
one worth a `400`.

**`PullRequestUrl` is where the work is being reviewed**, and it is one URL
rather than a list on purpose: the field answers “where is this being reviewed
*now*”, and “what has it been” is already answered by the event log, which
keeps every value the column has ever held. A list beside that would be a second
history, and a worse one.

The only rule about the value is that it is an absolute `http` or `https`
address — anything else is refused with a sentence, because a field whose only
job is to be clicked should not hold something that does not open. Nothing
parses the host: a self-hosted forge on a private address is a pull request like
any other, and a column that only accepted one company's would be a fact about
exactly one installation.

`CreatedBy` is a **name**, not a foreign key to `People`. The audit trail has to
read the same after a person row is deleted, and an API key's name goes in this
column beside a human one — neither of which a `People` FK from a module schema
could express.

#### Issue numbering

The one concurrency-sensitive spot. Inside the create request: read the project,
take `NextIssueNumber`, increment it, and save the issue and the project in one
`SaveChanges`.

`NextIssueNumber` is a column rather than `MAX(Number) + 1` because a deleted
issue must not hand its number to the next one — `AER-12` in an old chat log
should be a dead link, never a *different* ticket.

`[ConcurrencyCheck]` on that column is what makes the mint safe without a lock
or a sequence: two concurrent creates both read 12, and the second
`SaveChanges` finds the row no longer holding what it read and throws
`DbUpdateConcurrencyException`. The create path catches that (and unique-index
violations) and retries up to five times. The unique index on
`(ProjectId, Number)` is the backstop underneath, so the worst case is a refused
request rather than two issues wearing the same key. No raw SQL, and testable
in memory.

#### Ordering

`Rank` is a `long` with 1024-sized gaps. Bottom of a column is last rank + 1024
(or 0 in an empty one), between two cards is `(a + b) / 2`, and top is
first − 1024 (negatives are fine). When `a + 1 == b` there is no midpoint: the
whole column is rewritten to 0, 1024, 2048… in the same transaction and the card
is then placed. That is
[`RankService`](../src/Aerie.Api/Modules/Hatch/RankService.cs), with unit tests.

Lexorank strings were considered and rejected: string midpoint arithmetic has
sharp edge cases, and a column here holds tens of cards, not millions —
renumbering is one cheap `UPDATE`.

#### Rank computation

The client says *before this card* or *after this card*; **the server picks the
number**. This is the first instance of the rule that also governs the
dispatcher and the rollup: keep every client dumb, including Claude. A browser
and a shell script that each computed a rank would eventually disagree, and the
disagreement would show up as cards in the wrong order with no failing request
anywhere.

The browser's `lib/place.ts` is therefore about *which neighbours to name* —
which matters because the board filters, and a drop has to land next to the card
the operator can see rather than next to a hidden row at the same index.

#### Closing a subtree

Moving a parent into a terminal column used to move only the parent, and that
was the single largest source of a board disagreeing with reality: the epic read
shipped and the plan meter went on counting eleven leaves waiting, because
closing them one at a time is the work nobody does.

So an issue that lands in a terminal column with open work under it — dropped
there on the board, or pressed there on its own status bar — is answered with a
question about that work. It costs no request: `GET /board` hands the browser
every issue with its `parentKey` and its column, so `lib/closeSubtree.ts` walks
the subtree locally, and the dialog is on screen in the same frame the card
lands. A reorder inside a terminal column is not a close and asks nothing, and
neither does an issue whose descendants are all already closed.

The dialog **names the keys**, and the cascade is one `POST /issues/bulk` naming
exactly those — not a `cascade: true` flag the server would expand at write
time. That is the rule `IssueBulkEditRequest.Keys` states in `Dtos.cs`, met a
second time: a request that re-ran the query server-side could act on a row
filed between the operator reading the list and pressing the button, and what
was listed is what the operator agreed to.

Four smaller decisions, each of which reads as arbitrary until it is said:

- A descendant **already in a terminal column stays where it is**. It is already
  closed, and closing it again in a different flavour rewrites what happened to
  it.
- A descendant the filter is hiding, or a ready date is folding away, **moves
  like any other**. The fold is a view, not a fact — work that cannot start
  until August is still work under this issue.
- The descendants land in **whichever terminal column the parent landed in**,
  rather than in some canonical closed one. There is no such concept in the
  model, and a subtree abandoned into a second terminal column should read as
  abandoned.
- **The parent's move commits either way.** The question is about the
  descendants, so dismissing it means *leave them*, never *undo that* — which is
  also what lets the drag stay optimistic, with no card springing back out of a
  column it was deliberately dropped in.

### Comment, question and answer

`EfHatchComment` — `IssueId`, `Author`, `Body` (markdown), `Kind`, `AnswersId`,
`Options`, `CreatedAt`.

Most comments are notes: a commit sha, a summary for a reviewer, a change of
mind. Two are not, and those carry a `Kind`.

- A **question** (`Kind = "question"`) is an agent saying it cannot proceed
  without a decision that is not its to make.
- An **answer** (`Kind = "answer"`) is that decision, bound to the question it
  settles by `AnswersId`.

A question is a row rather than a heading in a comment body for the same reason
a ready date is a column rather than a line saying "not until March": something
has to be able to *act* on it. Prose in a thread can be searched; it cannot be
counted, and it cannot block a dispatch.

**Open is computed, never stored** — a question is open when no comment points
at it. That is why the link runs answer→question rather than the other way: a
decision can be refined by a second answer without editing the first, and a
question can never be open and answered at once because two writes disagreed.
[`Questions.cs`](../src/Aerie.Api/Modules/Hatch/Questions.cs) holds the single
definition, and the three callers that need it — the board badging a card, the
dispatcher refusing a run, and the list somebody sits down to answer — share it
rather than writing three.

`Options` is a `jsonb` list of `{ label, detail, recommended }`, null on a
question asked in prose. It exists because the first cut of this let an agent
ask, and what came back were dense paragraphs each fusing the question, two
named alternatives and a recommendation into one block a person had to parse by
eye before they could reply. A choice between named things is not an essay — it
is a menu, and a menu the reader can press is a decision made in one gesture.
Prose questions stay legal, because not every decision is a menu.

**Which option was taken is deliberately not stored.** An answer's body *is* the
label, so the thread reads as a decision rather than as an index into a list
nobody kept, and the sentence the next agent's prompt carries is the same
sentence a person reads six months later. A second column saying it in numbers
is a second thing that can come to disagree with the first.

### Issue event

`EfHatchIssueEvent` — `IssueId`, `Actor`, `Kind`, `Payload` (`jsonb`), `At`.
Append-only, written by every mutating endpoint, never edited and never deleted
except with its issue.

Kinds: `created`, `retitled`, `redescribed`, `retyped`, `status_changed`,
`parent_changed`, `ready_changed`, `due_changed`, `pull_request_changed`,
`commented`, `asked`, `answered`, `imported`.

Nothing renders this, and it has been written since the first release anyway,
because an event log is the one feature that cannot be added retroactively:
turned on in March, it answers nothing about February. The cost is a row per
edit.

`Payload` is schemaless on purpose — `{ "from": …, "to": … }` for an edit, the
source filename for an import — because the shape differs per kind and a column
per field would be a migration every time a new verb is logged.

**Rank-only moves are deliberately not events.** Dragging a card up its column
is board hygiene, not work, and logging it would bury the status changes that
matter under a hundred lines of tidying.

### Playbook

`EfHatchPlaybook` — `FromStatusId`, `ToStatusId`, `Types`, `Prompt`, `Model`,
`Effort`. Unique on `(from, to, types)`.

What an agent is told, and how much thought to spend, when it moves an issue of
some type from one column to the next. See [the dispatcher](#the-dispatcher).

## The wall, the admin gate, and API keys

Hatch adds no authentication of its own. It sits behind the two boundaries
[`auth-architecture.md`](auth-architecture.md) describes, and Phase 6 of its
build taught the outer one a second lane.

### The posture

- **The wall** (`AuthGate` + `AuthMiddleware`, enforced at Traefik and in
  process) decides whether a request reaches the app at all. `hatch.${DOMAIN}`
  carries the same `aerie-auth` middleware annotation as `home` and `kiosk`.
- **The admin gate** (`AdminGate`) decides whether an already-authenticated
  request may do an operator's things. Every Hatch controller carries
  `[RequireAdmin(AcceptScope = "hatch")]`, and the bundle is in
  `AdminAppMiddleware`'s `GatedApps` beside `/apps/admin`.

The refusals differ by design, and are the admin app's exactly:

| Reaching | As | Answer |
|---|---|---|
| `/apps/hatch/…` | a device not linked to an admin | **404** — indistinguishable from an install built without it |
| `/api/hatch/…` | a device not linked to an admin | **403** |
| anything | no credential at all | **401**, or a redirect to sign-in for a browser navigation |

The two gates stay separate rather than being folded into one. The wall runs on
every request and is enforced in two places; the admin gate runs on the handful
of routes that ask and is enforced only in this process. Folding them would put
a person lookup on the health probes and the media stream, which is precisely
what the wall's allow-list exists to prevent.

### The key

`EfApiKey` — `Name` (unique), `Prefix`, `Hash`, `Scopes` (`text[]`),
`CreatedAt`, `LastUsedAt`, `RevokedAt`.

A secret is `aerie_ak_` + 32 characters from a cryptographic RNG, drawn from
letters and digits only because a key is copied through shells, YAML and JSON
and every one of those has an opinion about punctuation. It is shown **exactly
once**, at mint, on the admin app's **API keys** page.

The `aerie_ak_` prefix is not decoration. It is there so that a string pasted
into a config file, a log line or a commit is recognisable as an Aerie
credential on sight — by a person reading a diff, and by the secret scanners
that read public repositories for exactly these shapes.

Only the SHA-256 of the secret is stored, exactly as for a grant token: a stolen
database yields nothing usable, and a lost key is replaced by minting another
rather than by looking this one up. `Prefix` — the first 12 characters — is
stored separately because it cannot be derived from a hash, and it is the only
part of a secret it is safe to show; without it, a key list is a list of names
somebody typed that you cannot check against the value in a config file.

Revocation is a **column, not a `DELETE`**, unlike a grant: the audit trail
names this key by a name that has to keep resolving, and "who was Claude in
March" is a question a deleted row cannot answer. `LastUsedAt` is written at
most once per `Auth:LastSeenThrottleSeconds`, the same throttle a grant's
last-seen takes and for the same reason.

**The row lives in the core `public` schema**, not in `hatch` beside the app
that prompted it. The wall reads this table on the way in, so a module owning it
would make the wall depend on a module — the one thing `Modules/README.md` says
never happens. The scope strings name modules; the rows do not belong to them.

A key is a **peer** of a grant rather than a variant of one. A grant is a device
the household enrolled and the wall lets in whole; a key is a named program
allowed a stated slice. They differ in every field that matters — a key carries
scopes and a grant does not, a key is revoked by a column and a grant by a
`DELETE`, and a key is never re-issued into a cookie. Folding them into one
table would mean a nullable half of the row for each.

### One gate, two lanes

The bearer header is **not a second authentication system**. `AuthMiddleware`
reads `Authorization: Bearer` alongside the cookies and hands both to the same
`AuthGate.EvaluateAsync`, which hashes the secret, matches an unrevoked key, and
returns an ordinary `AuthDecision` carrying the key. `AuthController.Verify` —
the endpoint Traefik's forwardAuth calls — reads the header off the forwarded
request exactly as it reads the cookie, so the proxy half and the in-process
half agree without either learning a new concept.

That is the whole of the rationale, and it is worth stating plainly because it
is what the design is *for*: **there is one place that decides whether a request
gets in, and one allow-list of what never has to.** A separate key middleware
would have been less code to write and would have created a second answer to
"who is calling" — a second thing to keep in step with the allow-list, a second
thing to remember when the wall's rollback is exercised, and a second thing that
could be right when the first was wrong. A new credential is a lane through the
existing gate, never a gate beside it.

Three things fall out of that, none of them added on purpose:

- **No cookie is re-issued on the key lane.** A key is the whole credential,
  held in a file the wall did not write and cannot refresh.
- **`unknown_key` is a refusal reason like any other**, logged in the same line
  and shaped by the same `Refuse`. A caller that presented a bearer gets a bare
  `401` rather than a redirect to sign-in, because a program has no browser to
  redirect.
- **The key lane is decided before the person lane and never falls through to
  it.** A key has no person and never will, so a key that fails must be refused
  rather than handed to a check that would refuse it again for the wrong reason
  — and, more importantly, a key must not inherit the answer a browser signed in
  on the same machine would have got.

### What a scope is, and what it is not

`AcceptScope` on `[RequireAdmin]` names the one scope that route will take from
a key. It is **opt-in**: naming a scope is a claim that this surface has been
thought through for a caller that is a program rather than a person, and the
honest number of surfaces that have been is one. Every route that names none —
minting credentials, revoking sessions, editing the house, roughly forty verbs
— keeps refusing keys, with `key_not_accepted`. That is what makes adding a key
a bounded act rather than a broad one.

A scope widens nothing for a person: a route that accepts `hatch` is still
closed to a household member who is not an administrator. And a key is never an
*administrator* — `AdminOutcome.Key` is its own outcome, and it means "this
program reaches what its scopes name", not "this program is the operator".

If a request comes back `403`, the key is working and the route is not one a key
may take.

### The one edge that is deliberately cut

**Writing a playbook is closed to a key.** The three write verbs on
`PlaybooksController` carry plain `[RequireAdmin]` naming no scope, while the
read carries the scope like everything else.

This is the one edge in the graph that would close a loop. A playbook chooses
the model, the effort and the prompt for the next agent, so an agent able to
edit one could widen its own instructions and raise its own budget — and that
failure is unbounded spend rather than a wrong answer, with no fixed point to
settle at. Cutting it costs nothing, and it is cut *in the route* rather than
asked for in a prompt, because a rule an agent is merely told is a rule an agent
can reason its way past.

One related edge is **not** cut, and is stated rather than papered over:
**nothing stops a key answering its own question.** A key is what
`hatch.sh answer` types with and it is also what a spawned agent inherits; the
server cannot tell them apart, and a check that looked like it could would be
worse than none. What holds the loop shut is one step further out — the dispatch
is refused while a question is open, and the dispatch is a command the operator
types. A second scope minted for agents would close it properly, and is not
worth a column until somebody wants one.

## API surface

Everything under `/api/hatch`, every route `[RequireAdmin(AcceptScope =
"hatch")]` except where noted. Issue routes take the display key (`AER-12`).

| Route | Verbs | Notes |
|---|---|---|
| `/projects` | GET, POST | POST validates key format and uniqueness |
| `/projects/{id}` | PATCH, DELETE | PATCH name and key; DELETE 409s unless the project is empty |
| `/statuses` | GET, POST | |
| `/statuses/{id}` | PATCH, DELETE | DELETE 409s while any issue holds it |
| `/board` | GET | Statuses plus every issue, ordered by `(StatusId, Rank, Id)`. Never filtered — the browser folds not-yet-ready cards away; the server hands over all of them |
| `/issues` | GET, POST | GET filters on `projectId`, `type`, `statusId`, `parentKey`, `ancestorKey`, `text`, ANDed, all optional |
| `/issues/bulk` | POST | `keys` plus any of `type`, `statusId`, `parentKey`, `readyAt`, `dueAt` |
| `/issues/{key}` | GET, PATCH, DELETE | PATCH writes one event per changed field; `""` clears a parent, a date or the pull request URL |
| `/issues/{key}/move` | POST | `{ statusId, afterKey?, beforeKey? }` — the server computes the rank |
| `/issues/{key}/comments` | GET, POST | POST carries the kind, the `answersId`, and a question's options |
| `/issues/{key}/questions` | GET | `?open=false` for the answered ones too |
| `/issues/{key}/events` | GET | Newest first |
| `/questions` | GET | Every open question in the house |
| `/plan`, `/plan/{key}` | GET | See [the level above the board](#the-level-above-the-board) |
| `/work/next`, `/work/{key}` | GET | See [the dispatcher](#the-dispatcher) |
| `/work/queue` | GET | The same walk `next` takes, reported rather than acted on — see [what a pass skipped](#what-a-pass-skipped) |
| `/playbooks` | GET | **Reads only.** POST/PATCH/DELETE are plain `[RequireAdmin]` |
| `/import/preview`, `/import/preview-text`, `/import` | POST | See [the importer](#the-importer) |

Two of the filters are worth knowing: `ancestorKey` returns everything below an
issue at any depth — an epic's stories and their tasks in one request — and
`parentKey=` (empty) finds the issues with no parent at all.

The bulk endpoint follows the same rules a single `PATCH` does: a field left out
is left alone, `""` clears one, and re-applying the same edit writes nothing. It
answers with `changed`, `unchanged` and `failures`. That "one refusal does not
take the batch with it" property is not a `try`/`catch` per key — the
single-issue edit path was pulled apart so that everything refusable is looked
up before anything is written, and the bulk path shares it.

## The dispatcher

`GET /api/hatch/work/next` answers "what should an agent do next, and how?" in
one request: which issue, which column it is headed for, whether it may go
there, which playbook speaks for the move, the issue's children, and its
questions.

**The board is worked right to left.** `work/next` takes the top of the
rightmost column that still has something an agent may advance. That is a
scheduling policy worth saying out loud: a board worked left to right starts
everything and finishes nothing; one worked right to left pushes whatever is
furthest along over the line before it opens anything new. The second is what a
person does when they mean to ship.

`?ancestorKey=AER-1` asks the same question of one epic's subtree instead of the
whole board — the same rule, narrower candidates, nothing else changed. It
shares `Rollup.DescendantIdsAsync` with the search filter rather than walking
the tree a second time, because a scope and a meter that disagreed about what is
under an epic is a bug nobody notices until the two are on one screen.

`?offsetMinutes=` is how a ready date is read against the caller's calendar day
rather than the server's.

**Four refusals**, and two of them are rules of the whole loop rather than
missing configuration:

1. The issue is already in a terminal column — there is nothing after it.
2. There is no column to its right.
3. The next column **is** terminal — *only the operator decides that something
   shipped*.
4. The issue holds an unanswered question — *it is waiting on a person, not on
   an agent*.

…and then, if none of those, the ordinary one: no playbook covers this
transition for this type.

The fourth is checked before the playbook, because "nobody has answered you" is
a more useful sentence than "no playbook covers this" when both are true. It is
also what keeps a question from being asked twice: without it, the loop's only
reaction to an open question would be to spawn another session that asks it
again.

`work/next` folds past everything blocked in silence — "nothing to do" is the
useful answer and a list of reasons is not — while `work/{key}`, which somebody
asked for by name, returns the refusal rather than a 404, because a person who
named a ticket is owed the sentence saying why it cannot move.

### Three more, on `next` alone

The five refusals above are facts about an issue. Three further rules are the
*loop's policy* — what an unattended run may **start**, as opposed to what may
move — so they are asked on `work/next` and not on `work/{key}`. A person who
names a ticket is giving an instruction, and housekeeping does not overrule it.

1. **Its type is `story` or `bug`.** An epic is out because choosing what an
   effort contains is a product call; a task because a task is a seam inside a
   story, and the story is the unit that ships — a task still crosses the board
   as part of its story's increment. `?types=story,bug,task` widens the set for
   a caller who means to, and a type nobody defined is a 400 naming it rather
   than a filter that silently matches nothing and reads as a finished board.
2. **Its ready date has arrived**, read against the caller's calendar day
   (`?offsetMinutes=`) rather than the server's. A card folded off the board is
   not one an unattended pass should be spending an increment on — but somebody
   who names a ticket ahead of its date has said the date is not the point
   today, and is given the dispatch rather than a lecture about it.
3. **No sibling of it is awaiting review** — two open pull requests under one
   parent is one too many. "Awaiting review" is measured, not named: the column
   immediately left of the first terminal one, which is exactly where the
   migration that added `review` placed it, so an operator who renames the
   column does not silently turn the rule off. Same-parent only — a null parent
   is not a group, so two loose issues are not siblings of each other — and a
   sibling in a terminal column blocks nothing, because merged work is not work
   in flight.

### What a pass skipped

`work/next` folding in silence is right for one increment and backwards for an
unattended loop. Nobody is watching, and the one thing worth having afterwards
is what the pass skipped and why — above all a column and type nobody has
written a playbook for, which reads as a finished board from the outside and is
not one.

`GET /api/hatch/work/queue` answers with every issue the dispatcher considers,
in the order it considers them, each carrying the sentence saying why it cannot
be advanced — or nothing, where it can. It takes the same `ancestorKey`,
`offsetMinutes` and `types` and means the same things by them, and each row is
what a dispatch carries minus the playbook prompt, the children and the
questions: those are the payload of one agent working one issue, and loading
them for every row would make a whole-board read expensive for nothing.

**The first entry with no reason is what `work/next` returns**, because it is
the same walk. `GetNextWork` is the first clear row of the scan rather than a
second loop that happens to agree with it — two walks that could disagree about
the order of the board is precisely the bug this endpoint exists to expose.

Every fold therefore lives in one place and in one order, most fundamental
first: a next column that is terminal, then the type an unattended run picks up,
then a ready date, then an unanswered question, then a sibling awaiting review,
and last the missing playbook — last because it is only worth saying about an
issue that is otherwise a candidate. The three that are the *loop's* policy
rather than facts about an issue are asked only when the pass is asking, so
`work/{key}` still ignores them.

The columns with nowhere to go — a terminal one, and a rightmost one that is not
terminal — are absent rather than listed as blocked. An issue the dispatcher
never reaches is not something the pass skipped, and shipped work is not a
backlog.

It is a read, and it costs what a read should: the statuses, the scope, what is
awaiting review, the open-question counts and the whole playbook matrix are each
read once for the pass rather than once per row, and the issues are projected in
one batch.

### Playbooks

A playbook row is `(from column, to column, issue types) → prompt, model,
effort`. A row naming the issue's type beats a row naming every type, and ties
go to the older row, so "Breakdown to Backlog, epics" can say something
different from "Breakdown to Backlog, anything else" without either having to
know about the other.

The matrix exists because **"do the next increment" is not one job**. Turning a
paragraph of intent into an epic with stories under it is the hardest thinking
in the flow and wants the largest model at the highest effort; picking up an
already-specified task and writing the code is ordinary work a smaller one does
well. Encoding that as rows rather than as branches in a script means the
operator retunes it from a page after watching a run go badly, which is the only
way anybody ever finds the right settings.

`Model` is an alias (`haiku`, `sonnet`, `opus`, `fable`) rather than a pinned
id, because a playbook says "the big one" and should still mean it a year from
now; a full `claude-…` id is accepted for an operator with a reason to pin.
`Effort` is exactly what the CLI's `--effort` accepts, because a value it does
not recognise is one it refuses at spawn time, long after the operator has
stopped looking at the page they typed it on.

Six rows are seeded, for the same reason the columns are: a Hatch whose agent
loop cannot run until somebody fills in a table is a Hatch that ships broken.
They cover every transition an agent owns, so `go-to-work` on a fresh install
needs no configuration beyond an origin and a key. They are joined on column
*name*, because a migration cannot know identity-generated ids — so an install
that renamed its columns first seeds nothing, which is the right failure: a
playbook wired to the wrong transition is worse than an empty table.

Nothing re-asserts them afterwards, and nothing overwrites one. `TheFlow` moves
three of them from `inbox → todo` onto `Breakdown → Backlog`, because that is
where specifying a draft happens now, but it writes no prompt, no model and no
effort — an operator who has retuned a row keeps every word of it, and this only
says which transition their playbook is the playbook for. Rows that are not
there are not restored either: deleting a playbook is how an operator takes a
column back from the loop, and a migration that re-seeded one would be handing
it back.

## The unattended loop

`hatch.sh go-to-work` is [the dispatcher](#the-dispatcher) run in a circle: read
the board, take the one issue an agent may advance, spend one increment on it,
and ask again. Given nothing else it runs until the board has nothing on it that
an agent may move, and then waits — asking again every interval, saying so once
and then rarely — which is what "leave it running overnight" has to mean if the
answer to "is anything left" can change while nobody is watching.

**The loop is the shell, not the model.** The alternative was one long session
told to keep going, and it was rejected for two reasons that point the same way:
it would carry six hours of context into its last ticket, and the earliest
decisions in that context are exactly the ones nobody can audit afterwards. A
process per increment starts each ticket cold, on the model and effort that
ticket's own [playbook](#playbooks) names rather than whatever the session
started as, and costs less for the privilege.

### What makes an issue actionable

Six conditions. An issue is the loop's to pick up when it meets every one, and
the sentence saying which one it failed is what `work/queue` reports:

1. **There is a column to its right, and that column is not terminal.** The end
   of the board is not a transition, and the step into a terminal column is the
   operator's: *only the operator decides that something shipped*.
2. **Its type is one an unattended run picks up** — `story` or `bug`, unless the
   caller widens it with `?types=`. An epic is a product call and a task is a
   seam inside a story; the story is the unit that ships.
3. **Its ready date has arrived**, read against the caller's calendar day. A
   card folded off the board is not one to spend an increment on tonight.
4. **It holds no unanswered question.** It is waiting on a person, and another
   agent sent at it would ask the same thing again or guess at the answer.
5. **No sibling of it is awaiting review.** Two open pull requests under one
   parent is one too many.
6. **A playbook covers that transition for that type.** Without one there is
   nothing to say to the session — and a column no playbook leads out of is
   exactly [how a column becomes the operator's](#status), which is why the
   absence is a fold rather than an error.

Three of them — 1, 4 and 6 — are facts about the issue, and `work/{key}` asks
them too. The other three are the loop's policy and are asked only when the pass
is asking; see [three more, on `next` alone](#three-more-on-next-alone).

**The board is worked right to left**, for the reason the dispatcher gives, and
overnight it is the difference between a shape and a mess: a loop working left
to right would open every draft in the house before it finished a single story,
and the morning after would be a board where everything had started and nothing
had shipped. Right to left, the run pushes whatever is furthest along over the
line before it opens anything new, and the thing furthest along is a pull
request somebody can read.

### One loop at a time

One loop at a time on one machine, and what says so is a directory under
`TMPDIR`, holding the pid of the run that took it. A directory because `mkdir` is atomic on every filesystem this could land on and a lock file
written with `>` is not; outside the repository because a lock in a tracked tree
is a lock somebody commits. A lock whose owner is gone — killed outright, or a
machine that rebooted out from under it — is cleared rather than honoured, which
is the difference between a loop that survives a crash and one somebody has to
let back in.

The alternative was a **claim on the issue row** — a "who is working this, and
since when" pair the dispatcher would fold past. It was rejected because a claim
is a lease: a lease needs an expiry, an expiry needs a heartbeat, and the first
run that dies mid-increment leaves a row nothing may touch until a timeout
nobody has ever tuned. It would also put the loop's scheduling state in the
tracker's schema, where the board would then have to draw it, to say something
about a process on one machine. The premise is one loop at a time, and a
directory says exactly that and nothing else. Two loops against one Hatch is
[parallelism](#deferred-on-purpose), and it is deferred rather than
half-answered here.

### When an increment does nothing

The one failure mode of an unattended loop that is dangerous rather than merely
disappointing: a session ends with the ticket in the column it found it in.
Nothing about the board changed, so the next pass picks the same issue, spends
the same money, and fails the same way — all night. Every other way an increment
can go badly costs one increment.

So a stall is written on the ticket: a comment naming the session that ran and
the transition it was trying to make — with the `claude --resume` command, since
resuming the conversation is most of why a stall is worth recording rather than
merely counting — and, if nothing is already open there, **a question**.

A question rather than a **flag field**, which was the obvious alternative and
would have had to be taught three things a question already does: it blocks the
issue from being dispatched again, it badges the card on the board, and it is
the list `hatch.sh answer` walks. Answering it clears the flag, which is the
right gesture, because the flag means "nobody has looked at this" and answering
is somebody having looked. A field would have been a fourth thing on the issue
row that only the loop writes and only the loop reads, and a second flag the
loop could read is one step from a flag the loop could set.

Neither of its options is recommended, and that is not modesty: `--recommend` is
for a choice something knows the answer to, and the whole content of a stall is
that nothing here knows why it happened. A ticket that is already waiting on a
question gets the comment and no second question — that question *is* the flag,
usually raised by the session's own way out.

### Where the loop's rules live

On the server. `work/queue` is the same walk `work/next` takes, reported rather
than acted on, and `GetNextWork` is the first clear row of that scan rather
than a second walk that happens to agree with it — so the shell asks two
questions and cannot get two different boards. Everything
above is decided in one place, in one order, in
[`WorkController.cs`](../src/Aerie.Api/Modules/Hatch/WorkController.cs).

The alternative was **the shell**, and it is the cheaper thing to write: `queue`
already parses the board, and folding the not-yet-ready and the wrong-typed out
of it is a few lines of `jq`. It was rejected because it puts the rules where
the *caller* is, and there is more than one caller — a terminal, a loop, and the
issue page — so the first renamed column would leave two of them disagreeing
about what is workable, silently, with nobody watching. The shell's whole job is
to spend increments and say what happened; it decides nothing about which.

That is also why the loop passes no `--model` or `--effort` of its own, though
`work` accepts both. An override typed for one increment is one operator's
opinion about one ticket, and a loop that carried it across a night would be
applying it to tickets nobody looked at.

### What it stops for

Nothing, by default, and that is the point: a run that stopped for a reason
nobody asked for is a run somebody has to check on. Every stop is asked for,
except the last one:

- `--once` — one pass, whatever it found, and out. The loop's own dry run
  against a board that is not a fixture.
- `--max-runs N` — that many increments.
- `--max-spend USD` — measured from what the increments reported, not estimated.
- `--until HH:MM` — wall clock. `--until 06:00` typed at eleven at night means
  the morning, because the alternative reading is a loop that stops seventeen
  hours before it started.
- `--stop-file PATH` — touch it and the loop ends. A path, so stopping needs
  nothing but a shell: no pid to find, and no signal that could land in the
  middle of a push. It is read between increments, so the one in flight finishes
  first, and a path that already exists is refused at startup rather than read
  as a board with nothing on it.
- **Three failures in a row** — the one nobody asks for. A failed increment is
  not a reason to stop; a ticket can be wrong and a test can be flaky, and the
  next ticket is a different question. Three in a row is something else:
  whatever is broken is broken for every ticket, and the loop is now spending
  money to prove it.

`--under AER-1` points a night at one project: the same rule, asked of one
epic's subtree. It and a bare key cannot be given together, and a bare key is
refused outright — `go-to-work` asks "what is next" over and over, and one
ticket cannot be the answer to that twice. One increment on one ticket is what
`work AER-12` is for.

The tally is printed from the exit path and nowhere else, because the ways a
loop ends include the ones nobody wrote code for — an interrupt, a terminal
closing — and those are the runs whose tally is most worth having. It counts the
increments, the elapsed time and the spend, and then lists the tickets in two
groups: the ones that moved, which is what the night got done, and the ones that
stalled, which is what is waiting on somebody.

## The level above the board

The board shows every card, which is the one thing it cannot do: say which of
the running projects is nearest the line. `GET /api/hatch/plan` and
`/api/hatch/plan/{key}` answer that, and the **Plan** page draws it.

**The unit is the leaf, and every leaf weighs the same.** A subtree's total is
the histogram of its leaf descendants by status; an issue with no children
counts as itself, one leaf in its own column. The alternative on the table was
child-weighted — each story 1/n of its epic whatever its size — which makes
nested meters agree by construction but says a twenty-task story and a two-task
one are the same size. They are not, and the bar is there to say how much work
is left. The agreement comes for free anyway: a parent's total is exactly the
sum of its children's, and a test pins it.

**A parent's own column never lands in its own total.** A story sitting in In
Review whose tasks are all in To Do reads as To Do, because the tasks are the
work. Ready dates are not consulted either — a card folded off the board is
still work, and a total that shrank and grew as dates arrived would not be a
total.

**The bar shows the distribution across the columns, not a filled fraction.**
One stacked segment per status, in the colour the operator painted that column,
so where the bulk sits reads at a glance: an epic whose every story is in In
Review looks nothing like one whose every story is in Draft, and a single fill
would have drawn both as 0%. It also disposes of the partial-credit question
underneath it — nothing has to invent a score per column when every column is
drawn. A subtree with nothing filed under it draws no bar rather than an empty
trough, because an epic with no stories is at the start of its life, not stalled
at the bottom of one.

Every total is the same shape: `leaves`, `done` (the leaves in a terminal
column), `waiting` (open questions on the issue and everything below it), and
`slices`, one `{ statusId, count }` per column in board order with the empty
ones left out.

[`Rollup.cs`](../src/Aerie.Api/Modules/Hatch/Rollup.cs) loads the tracker once
and folds post-order — O(n) for the tree rather than a walk per node — because
the Plan page asks about every epic in the house at once. It is server-side for
the reason the rank and the dispatcher's pick are: a browser that re-derived a
total would disagree with the CLI the first time a column was added.

## The importer

A read-only upload that turns a markdown document into a tree: the doc becomes
an **epic**, each `## Phase` heading a **story**, and each checkbox a **task**,
with a checked box landing in a terminal column. `preview` takes files,
`preview-text` takes a pasted `{ title, body }` through the same parser — the
title plays the filename's part, which is the provenance every issue carries and
the epic's title when the body has no `#` heading. Neither preview touches
anything; `POST /import` is what writes.

It exists because Hatch inherited a folder of plan files, and it stays because a
plan drafted in an editor is still the fastest way to think one through.

Nothing about the import is destructive at the source. **Retiring a source
document is a deliberate manual act** — verify the epic against the file it came
from, then `git rm` it — because an importer that deleted its input would be one
bad parse away from losing the only copy of a plan.

## The operator and Claude contract

This is the part [`CLAUDE.md`](../CLAUDE.md) mirrors, because that is where an
agent reads it. The rules are here so that they have somewhere to be argued
from.

### Reaching Hatch

[`scripts/hatch.sh`](../scripts/hatch.sh) wraps the calls a working session
actually makes — `board`, `next`, `queue`, `show`, `start`, `move`, `comment`,
`pr`, `ask`, `questions`, `answer`, `work`, `go-to-work`, and `api` for
everything else. It
finds a column by name rather than by id — on the letters and digits alone, so
`todo` at a terminal reaches the column the board calls `To Do` — and folds off
cards whose ready date has not arrived, exactly as the board does.

Its settings come from `scripts/.env` (mode 600, git-ignored, written by
`hatch.sh config`), and an exported `AERIE_BASE` or `AERIE_HATCH_KEY` wins over
the file. **The key lives outside the artifact** — never a tracked file, never a
value in a commit, never pasted into an issue. That is not ordinary secret
hygiene: Aerie ships to other operators, and a credential in the artifact is one
operator's credential inherited by everyone who clones it
([`ethos.md`](ethos.md)).

`hatch.sh work` reads `work/next`, then spawns a headless session with the
playbook's prompt, model and effort. It prints the session id first and last
with the `claude --resume` command beside it, and streams what the run is doing
as it happens — every tool call, a thinking-token pulse, and a heartbeat naming
what it is still waiting on. That last part is not a nicety: the CLI's default
output prints nothing until the run ends, so a four-minute increment was four
minutes of blank terminal indistinguishable from a hang, and the fix for "is it
working" is showing the work, not a spinner.

`hatch.sh go-to-work` is `work` in a circle, and is
[its own section](#the-unattended-loop) — what it may pick up, what it does
about a ticket that did not move, and what it stops for.

`hatch.sh queue` reads the scan and prints it, one issue a line — key, type,
column, and either the reason the pass would fold past it or the transition it
is clear for. `hatch.sh queue AER-1` scopes it to one epic's subtree. It spawns
nothing and writes nothing, and an empty board prints a sentence saying so
rather than a blank line: "there is nothing" and "something went wrong and
printed nothing" look identical otherwise, which is the one thing a run nobody
watched cannot afford to be unsure about.

### What an agent does with a ticket

- **Read it.** The description is the brief; the comments, the answered
  questions, and the event trail are the context.
- **Move it to *in progress* before starting.** The board saying what is being
  worked on right now is the board's whole job.
- **Comment the commit sha and the branch, and record the pull request.** The
  ticket is where somebody looks in six months, and a comment naming a commit is
  what makes that search short. The pull request is a field rather than a
  sentence — `hatch.sh pr AER-12 <url>` puts it there, and the issue page draws
  it as something to click.
- **Plan on the ticket, not in a chat log.** A planning session `PATCH`es
  acceptance criteria into the description and `POST`s the stories or tasks the
  work breaks into. An epic takes stories; a story takes tasks.
- **File the wait, don't write it down.** Something that cannot start until a
  soak test finishes or a renewal window opens gets a `readyAt`, not a sentence
  in a description saying "not until March".

Every one of those calls writes an event carrying the key's name as the actor,
so the trail says who did what without anybody being asked to record it.

### What only the operator does

- **Move work into a terminal column.** Implementation ends in *review*, with a
  comment saying what landed and what did not. Only the operator decides that
  something shipped, and the board enforces it: the dispatcher refuses a
  transition into a terminal column outright.
- **Edit a playbook.** The API refuses it, and the refusal is deliberate; if a
  playbook is wrong, say so on the ticket and stop.
- **Mint and revoke keys**, and everything else behind a plain `[RequireAdmin]`.
- **Answer a question**, which is the next section.

### When a decision is not the implementer's

Some things a ticket needs are not an implementer's to choose: a product call, a
name that will be lived with for years, a tradeoff with no technically correct
side. The rule is: do not guess, and do not quietly take whichever branch is
cheapest to build. Ask on the ticket, **name the choices**, and stop.

```
./scripts/hatch.sh ask AER-12 "How should drain retries be scoped?" \
    --recommend "Per-node: one budget each, so a slow node cannot starve the rest" \
    --option   "Global: one budget for the drain, simpler to reason about"
```

The body is the question alone, in a sentence; the tradeoffs go inside the
options they belong to; and `--recommend` marks the one the asker would take, of
which there may be one. A label is short enough to press and reads as a decision
on its own — `per-node`, not `we should scope them per node` — because the label
becomes the answer's own text. One call per question, so each can be answered on
its own. Prose (`ask` with no options) is for the answers that are genuinely
open-ended: a name, a description, a direction.

Then **stop**. An open question blocks the ticket from being dispatched at all,
so nothing further is spawned at it until somebody answers, and anything built
past the question is built on a guess.

The operator answers at a terminal (`hatch.sh answer` walks the open ones one at
a time, serially — a list of six printed at once gets answered in aggregate,
which is how a wrong assumption gets in) or on the issue page. The answer is a
comment bound to its question, so `work` carries the decisions already made into
the next session's prompt under **Decisions already made**. Those are settled;
build on them, and do not reopen them.

What the repository can answer, answer by reading the repository. A question the
code already settles is a round trip through a person for nothing.

## Deferred on purpose

- **Parallelism.** One loop at a time, on one machine, enforced by a directory
  in `TMPDIR` and by nothing else. Two loops against one Hatch would need a
  claim the dispatcher honours — with an expiry, and a heartbeat behind the
  expiry — and the lock exists precisely so that none of that has to be right
  before the first unattended night can run. It is also not obviously wanted:
  the [sibling rule](#three-more-on-next-alone) already says one open pull
  request per parent, and the operator reading them is the one thing that does
  not parallelise.
- **A second scope for agents**, which would stop a key answering its own
  question. Worth a column when somebody wants it; see
  [the one edge](#the-one-edge-that-is-deliberately-cut).
- **Reporting.** The events are there; nothing renders them. The Plan view
  answers the question that was actually being asked.
- **GitHub integration.** A branch and a PR are named in a comment by whoever
  made them.
- **A deleted issue takes its events with it.** Hard delete, confirmed in the
  UI, and an accepted gap.
- **Live updates.** Refetch on action and focus.
