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

**The wire contract: its own project.** `src/Aerie.Hatch.Contracts` holds the
records the API serves and the records a client sends it — nothing else, and
nothing that needs a `DbContext` to compile. It is a project rather than a file
in the module because there are now two kinds of client compiled against it: the
API on one side, and the runner below on the other. A runner that redeclared the
shape of a dispatch would drift from the server the first time somebody added a
field, and drift *silently*, because JSON does not complain about what it did
not find.

**The runner: a program.** `src/Aerie.Hatch` is `work` and `go-to-work` — see
[where the loop lives](#where-the-loop-lives), which is about why they stopped
being shell.

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

**At home: the same images, on one laptop.** There is a second place Hatch
lives, and it is somebody else's machine — `deploy/local/compose.yaml`, published
to the registry as an OCI artifact beside the two images it names, so one
`docker compose -f oci://…` line brings up a board with no checkout of this
repository and no account anywhere. There is no Traefik in front of it and so
none of the rewriting above: one container serves every app on one origin, the
wall is off, and the runner comes from the Runner page inside the board rather
than from a build. That install is a friend's whole tracker, and
[`hatch-at-home.md`](hatch-at-home.md) is what it is handed — the one line, the
name, the runner, and the block to paste into their own `CLAUDE.md`.

## Domain model

Seven tables in the `hatch` schema, all carrying the house `Ef` prefix. The
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
| To Do | 40 | | **agent** | Analyse it until implementing it is mechanical. |
| In Progress | 50 | | **agent** | Write the code, get it green, push it, put it up for review. |
| In Review | 60 | | operator | Read the pull request, wait for green, merge. |
| Done | 70 | ✓ | operator | Terminal. |

**Which column belongs to whom is not a field.** It is derived: a column an
agent may leave is a column some [playbook](#playbooks) names as its `from`, for
that issue's type. A missing playbook row is how a column becomes the
operator's, and it is also how an epic in Breakdown can stay the operator's
while a story in Breakdown is the agent's, because a playbook names types. A
second "whose column is this" flag would be free to disagree with the first, and
a flag the loop could read is one step from a flag the loop could set.

**The loop reads the matrix and holds no list of its own.** It once held both,
and the copy in the source was asked *first*, so an epic in Breakdown was
folded on its type while the row the operator had written for exactly that move
sat unread. Two statements of which types a move applies to is one too many,
and the one that goes is the one nobody can edit.

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
`DueAt`/`DueAtHasTime`, `PullRequestUrl`, `ModelOverride`, `EffortOverride`,
`AssigneePersonId`/`AssigneeApiKeyId`, `CreatedBy`, `CreatedAt`, `UpdatedAt`.

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

#### Assignee

**Who owns a ticket, said on the ticket.** An issue is assigned to a person, or
to an API key, or to nobody — two nullable columns, `AssigneePersonId` and
`AssigneeApiKeyId`, of which at most one is ever set. The controller clears one
when it writes the other, and a check constraint refuses a row wearing both, so
the constraint is a backstop and never the error a caller sees.

It is deliberately **not a claim**. A claim is a machine lease that comes and
goes with an increment, and Hatch does not have one for the reasons [one loop at
a time](#one-loop-at-a-time) gives. An assignee is a durable statement written
by a person, changing rarely and surviving every restart — which is why it is a
column here and a lease would not have been.

Neither column is a foreign key, and neither could be: Hatch owns its own schema
and its own migration history, and a constraint from a module into
`public.People` is the coupling `Modules/README.md` exists to prevent — the same
reason `CreatedBy` is a name. `ON DELETE SET NULL` would not have worked anyway,
because [revoking a key](auth-architecture.md) sets a column and keeps the row
on purpose.

What does the work instead is **one predicate every reader applies**: an
assignee resolves only to a *live* identity — a person row that still exists, or
a key that exists and is not revoked — and an id that does not resolve reads as
**unassigned**. In the issue payload, on the card, in the board filter and at
the dispatcher, at the same instant. Nothing sweeps, nothing is cleaned up, and
nothing can be half-migrated: a predicate cannot fail to run, which is the same
argument [one loop at a time](#one-loop-at-a-time) makes for a lock whose owner
is gone being cleared rather than honoured. The rule lives in one place,
`Services/Auth/ActorDirectory.cs`, so that the day a second module wants an
owner there is one thing to ask.

The trade is stated rather than papered over: an issue assigned to a key that is
later revoked reads as unassigned without announcing it. The `assignee_changed`
event still names who it was — both sides carry a name beside the id, for the
reason `CreatedBy` is a name — so the trail answers "whose was this in March".

Setting one is closed to an API key: under the loop's `people only` rule an
assignee is a dispatch gate, so a key that could write one could hand itself
work somebody had reserved. See [The one edge that is deliberately
cut](#the-one-edge-that-is-deliberately-cut), and [what makes an issue
actionable](#what-makes-an-issue-actionable) for what a name on a ticket does to
a pass.

**`ModelOverride` and `EffortOverride` are what this ticket costs**, and null on
every issue until somebody says otherwise. A playbook prices a *transition*,
which is the right unit almost always; what it cannot say is that *this* story
is the hard one. Where one is set it beats every playbook that could speak for
the issue — all of them, not one transition's worth — and where it is null the
playbook decides exactly as it did before. The two are independent: an issue may
carry a model and no effort, an effort and no model, both, or neither.

They reach the issue and nothing beneath it. An epic set to `opus` does not
spend `opus` on its stories; a task that needs the big model says so itself, and
the alternative is one expensive decision made at the top of a tree and
inherited by work nobody weighed.

The values are a playbook's own values — the four aliases or a pinned
`claude-…` id for a model, the five efforts — validated by the playbook's own
rule and refused in the playbook's own sentence, so what an issue may be set to
and what a playbook may be set to cannot drift apart. Setting one is closed to
an API key; see [The one edge that is deliberately cut](#the-one-edge-that-is-deliberately-cut).

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

### Dependency

`EfHatchIssueDependency` — `IssueId` (the one that waits), `DependsOnId` (the
one waited on), `CreatedBy`, `CreatedAt`. Unique on the pair, and indexed on
`DependsOnId` alone for the reverse direction, which the **Blocks** list and the
dispatcher both read.

**An edge is what serialises work — not shared parentage.** The board used to
guess: no unattended run started an issue while a sibling of it was awaiting
review, which was right about the epics whose stories touch the same files and
wrong about every epic whose stories are independent, and said nothing at all
about two issues that must land in order under different parents. The guess is
gone and the fact is written down. An epic whose five stories must land one
after another gets a chain of them, a linked list the loop walks in order; an
epic whose five stories are independent gets none, and the loop takes them in
whatever order the board puts them in.

**A dependency gates implementation, and nothing else.** The old rule folded an
issue past every move it could make; this one holds exactly one. An issue
waiting on another still goes through Breakdown, still lands in Backlog, and is
still analysed — everything left of the writing keeps moving, which is the
point. What it does not do is get picked up and written.

That column is **measured rather than named**: the implementation column is the
one whose own next move is into the awaiting-review column, which is itself the
column immediately left of the first terminal one — exactly where the migration
that added `review` placed it. On a stock board that is `To Do` to `In
Progress`, the one transition where code gets written. Named, both would be
rules that quietly stopped applying the day an operator renamed a column.

An issue **already in** the implementation column is never gated. The move into
review is not a dependency's to refuse, so work that started finishes rather
than stalling half-written.

**Satisfied means done.** An edge clears when the issue it names is in a
terminal column — merged, not merely up for review. Anything softer and story
two starts on top of story one's unmerged branch, which is the failure the whole
feature exists to prevent, and it is why the chain advances at the operator's
merge rather than at an agent's move.

**A dependency an ancestor holds reaches everything below it.** A task under a
blocked story is blocked and every story under a blocked epic is blocked, which
is what lets "phase two after phase one" be said once at the top. The fold names
the *nearest* holder and stops: clearing that one is what the reader has to do
first, and the pass after it says what is behind it.

An issue may wait on any number of issues, **including issues in another
project** — a dependency is not containment, and the same-project rule that
governs a parent does not govern this. It may not wait on itself, on an issue
above or below it in the tree (a parent is not done until its work is, so an
edge either way round could never be satisfied), or on anything that already
waits on it directly or through a chain. Each is refused with the sentence
saying which, and nothing is written. Adding an edge that is already there
writes nothing and is not an error, as re-applying any edit here is not.

**Writing one is open to a key**, unlike a [playbook](#playbooks) or a per-issue
override. An edge is a statement about the work rather than about an agent's
budget, and a planning session that has just filed five stories is exactly who
should chain them. Both verbs — `POST` and `DELETE` on
`/api/hatch/issues/{key}/dependencies` — answer with the whole issue, and there
is no `GET`: both lists ride `IssueDto`, where the board, the page and a client
at a terminal need them anyway. Both directions land in the issue's history, on the issue that
waits and on it alone.

### Claim

Seven nullable columns on the issue row — `ClaimToken`, `ClaimedBy`,
`ClaimRunner`, `ClaimedAt`, `ClaimHeartbeatAt`, `ClaimChatter`, `ClaimChatterAt`
— and a lease is all of them set or all of them null.

**A claim is a lease held by a running dispatcher**: taken before an increment,
refreshed while it runs, released after it, and expiring on its own when the
runner dies. It is what makes two checkouts against one Hatch safe, which is the
whole of why it exists. Columns rather than a table because a claim is a fact
about the issue with at most one of it at a time, and because taking one is then
a single conditional `UPDATE` against a row the dispatcher is already reading.

**A claim and an assignee are different nouns.** An assignee is a person's
intent, answered in days and written by a person. A claim is a process's grip,
answered in minutes and written by a machine, and it is gone the moment the
machine is. Putting them in one field would mean a crashed runner erasing
somebody's plan for the week.

**Expiry is lazy, and there is no sweeper.** A claim is dead when its heartbeat
is older than the TTL, which is a predicate every reader evaluates rather than a
state anything writes: nothing has to run for a dead runner's ticket to become
claimable again, and there is no job to tune, schedule or notice has stopped.
The columns stay as they are until somebody takes the lease over, so the trail
of who last held it outlives the lease. The TTL is `Hatch:ClaimTtlSeconds`, five
minutes by default, and it is **returned to the client with the claim** — on
every read a claim rides, not only on the take — rather than configured on both
sides, because the server is what honours it and so is what says what it is. A
client holding a heartbeat four minutes old cannot otherwise tell a runner about
to go from one that is fine.

**The token is a fencing token, and it is a capability.** Every heartbeat and
every release presents it, and a write whose token is not the one on the row is
refused — which is what stops a runner whose lease expired mid-increment from
clearing the lease that replaced it. It is handed out exactly once, in the
response to the claim that minted it, and **it is on no read anywhere**: a board
read that carried it would let anybody holding a board read steal or refresh
somebody else's lease. What a client gets instead is who holds it, from where,
when it was taken, when it was last heard from and what it last said — and
nothing at all where the claim has expired, because the arithmetic is the
server's and a card drawing a holder that stopped existing four hours ago is
worse than a card drawing nothing.

**A claim is drawn where the work is looked at.** A claimed card carries a dot
in its head row, green while the holder is being heard from and amber once it
has gone quiet, with who holds it, from where and how long since a word on the
hover. The issue page draws a **Claim** section saying the same things at
length: the holder, the `host:/path/to/checkout` they hold it from, when it was
taken, when it was last heard from, and the last line the runner printed with
how long ago it printed it. *Quiet* is half the lease without a word — a
fraction rather than a count of minutes, so changing `Hatch:ClaimTtlSeconds`
moves the warning with it — and it is not expiry: a claim past its lease is
drawn as no claim at all, on the card and on the page, because the server has
already stopped sending one. Neither polls. Both go stale with the read they
came from and come back current on the next one.

**Clearing a claim does not stop the runner.** The operator's
`DELETE .../claim` takes the lease off the row and nothing else: that session
keeps running, keeps pushing, and may still move the ticket, because moves are
not gated on a token. What clearing does is make the issue claimable again, so
the next pass may spawn a second session at it and the two would then both be
writing to it. It is the sentence the confirmation on the issue page leads with,
and the reason to press it is a runner known to be gone or known to be wrong.

**The guarantee lives in a predicate on the write.** Reading the row first is
unavoidable — a refusal has to name who holds it, and that sentence can only be
written from the row — but nothing is decided by the read. Each write is one
conditional `UPDATE`: a take matches only a row nothing live holds, a refresh
and a release only a row still carrying the caller's own token. Two runners that
both looked at the same unclaimed row resolve to one claim, and the loser wrote
nothing rather than overwriting a lease it lost.

**What the claim fences is the claim, not the writes.** Two runners are never
*dispatched* at one ticket, which is the failure it exists to prevent. A runner
whose lease expired mid-increment can still push its branch and still move the
ticket, because moves are not gated on a token — and gating them would break
every move a person makes, which is not a trade worth taking for a window that
opens only when a runner stops answering for five minutes and then comes back.
It is named here rather than papered over.

Taking, releasing and clearing each write an event naming the actor, so the
trail says who took a ticket and who let it go. A heartbeat writes none: it is a
meter reading rather than a decision, and the trail would otherwise be a row a
minute for every running increment.

**Its tests need a real Postgres**, and `make test-api-db` is how they get one —
`make test-api` runs the same suite and skips them, saying so. That is not
fastidiousness: the guarantee here is a `WHERE` clause, EF's in-memory provider
refuses a conditional `UPDATE` outright, and the SQLite provider cannot
translate the `DateTimeOffset` comparison the expiry rule is. Either substitute
would be green while the SQL was wrong, which is the one failure mode the lane
exists to prevent — the same argument the trading silo's queue makes about its
own. CI runs it on every pull request.

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
`model_override_changed`, `effort_override_changed`, `assignee_changed`,
`dependency_added`, `dependency_removed`, `commented`, `asked`, `answered`,
`imported`.

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

**So is writing an issue's override**, and it is the same edge rather than a
second one: `PATCH /api/hatch/issues/{key}/playbook` sets the model and the
effort every increment on that ticket runs on, which is a playbook's power
routed through another table. It lives on its own controller
(`IssuePlaybookController`) for exactly that reason — `IssuesController` accepts
the `hatch` scope on everything it holds, so two more fields on the ordinary
issue patch would have been an agent that can raise its own budget. Reading an
override is open, like everything else a dispatch needs: an agent is entitled to
know what it is being spent on, and both fields ride `IssueDto`.

This is the one edge in the graph that would close a loop. A playbook chooses
the model, the effort and the prompt for the next agent, so an agent able to
edit one could widen its own instructions and raise its own budget — and that
failure is unbounded spend rather than a wrong answer, with no fixed point to
settle at. Cutting it costs nothing, and it is cut *in the route* rather than
asked for in a prompt, because a rule an agent is merely told is a rule an agent
can reason its way past.

**And so is setting an assignee**, which is a *related* edge rather than the
same one. A playbook widens what an agent may spend; an assignee widens what it
may be **sent at** — because under the loop's `people only` rule, a name on a
ticket is what holds it off the night shift. A key that could write one could
clear a person's name off a ticket and hand itself work somebody had reserved.
So `PUT /api/hatch/issues/{key}/assignee` lives on its own controller
(`AssigneeController`) carrying no class-level scope, for the same reason and by
the same means. Reading is open, and deliberately: an agent has to be able to
say whose ticket it is leaving alone, so both `GET /api/hatch/assignees` and the
`assignee` on `IssueDto` are Hatch-scoped like everything else.

One related edge is **not** cut, and is stated rather than papered over:
**nothing stops a key answering its own question.** A key is what
`hatch answer` types with and it is also what a spawned agent inherits; the
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
| `/issues/{key}/playbook` | PATCH | **Person only** — plain `[RequireAdmin]`. The issue's own model and effort; `""` hands either back to the playbook |
| `/assignees` | GET | Every person and every live key, plus who the caller is — the picker's rows and *Assign to me* in one read |
| `/issues/{key}/assignee` | PUT | **Person only** — plain `[RequireAdmin]`. `{ kind, id }`, or both null to unassign — see [Assignee](#assignee) |
| `/issues/{key}/claim` | POST | Takes the [lease](#claim). `{ runner }`; answers with the token, the holder, when it was taken and the TTL. `409` naming the holder where something live already has it — including the same runner asking twice |
| `/issues/{key}/claim/heartbeat` | POST | `{ token, chatter? }` — refreshes it, `204`. `409` on a token that is not the row's, and on a lease that is over. `chatter` absent leaves the carried line alone, `""` clears it, anything longer than the column is truncated rather than refused |
| `/issues/{key}/claim?token=…` | DELETE | Releases it, `204`. A mismatched token is `409` and clears nothing; an issue holding no claim is `204` and writes nothing. **With no token at all it is person-only** — an agent that could clear another runner's claim could take a ticket off it mid-increment |
| `/questions` | GET | Every open question in the house |
| `/plan`, `/plan/{key}` | GET | See [the level above the board](#the-level-above-the-board) |
| `/work/next`, `/work/{key}` | GET | See [the dispatcher](#the-dispatcher). `?heldToken=` names a [claim](#claim) of one's own, so it is not folded past as somebody else's |
| `/work/queue` | GET | The same walk `next` takes, reported rather than acted on — see [what a pass skipped](#what-a-pass-skipped) |
| `/playbooks` | GET | **Reads only.** POST/PATCH/DELETE are plain `[RequireAdmin]` |
| `/import/preview`, `/import/preview-text`, `/import` | POST | See [the importer](#the-importer) |
| `/utilization` | GET | The account's Claude headroom, read by the server. `204` when no token is configured; `?refresh=true` bypasses the cache — see [the battery](#the-battery) |
| `/local-person` | GET | What to call whoever is sitting here, and whether anybody said so. `204` wherever the wall is up |
| `/settings` | GET, PUT | **Person only** — plain `[RequireAdmin]`, so a key is refused the read as well as the write. The two settings a Hatch install of its own has — see [the credential](#the-credential). PUT follows the bulk rule: a field left out is left alone, `""` clears it |
| `/issues/{key}/work-log` | GET, POST | What each session on this issue cost. **POST is a key only** — a browser is refused outright, because the only honest writer of a meter reading is the dispatcher that read it. See [the leaderboard](#the-leaderboard) |
| `/work-log/sessions` | GET | The sessions in a range, ranked, with the range's own totals — see [the leaderboard](#the-leaderboard) |
| `/work-log/history` | GET | The same rows folded into equal buckets of time, for the graph |

Two of the filters are worth knowing: `ancestorKey` returns everything below an
issue at any depth — an epic's stories and their tasks in one request — and
`parentKey=` (empty) finds the issues with no parent at all.

The bulk endpoint follows the same rules a single `PATCH` does: a field left out
is left alone, `""` clears one, and re-applying the same edit writes nothing. It
answers with `changed`, `unchanged` and `failures`. That "one refusal does not
take the batch with it" property is not a `try`/`catch` per key — the
single-issue edit path was pulled apart so that everything refusable is looked
up before anything is written, and the bulk path shares it.

## The battery

The nav strip carries one element the board does not: how much Claude the
account has left, and how long until it comes back. It is read from every page
without opening another app, and it is drawn from `GET
/api/hatch/utilization`.

**Absent is a supported state, and it is the default.** An installation with no
Claude subscription token gets a Hatch with no battery — no element, no
placeholder, no reserved space, and nothing anywhere reporting an error. This
is an enhancement to a tracker, not a dependency of one, and the `204` the
endpoint answers with is what says so.

### The credential

The token is a **secret-valued site setting**, `ClaudeSubscriptionToken`, set on
Hatch's own **Settings** page and stored the way the Immich key and the
Anthropic key already are: obfuscated at rest, redacted on read, never leaving
the API. That page holds two settings and no others: this token, and
`LocalPersonName` — what Hatch calls whoever is sitting at the machine
([auth-architecture.md](auth-architecture.md), "Local mode"), shown only where
there is no wall, because with one the name comes from the grant. It lives in
Hatch rather than on the admin app's Settings page so that an installation with
no admin app — which is every installation that is only somebody's tracker —
can still set both. It is an OAuth token for the operator's own Claude subscription, and
like every other credential in Aerie it is the operator's to supply
([`docs/ethos.md`](ethos.md)) — nothing about one household's account may be
true of the artifact.

A setting rather than an environment variable for a reason beyond habit: this
credential is genuinely optional, the provisioning path cannot express an
optional cluster secret, and an expiring token is re-pasted far more often than
a cluster is provisioned. Leave it blank and there is no battery, which is a
working configuration.

The token reaches the app through one interface, `IClaudeCredential`, whose one
member answers "the token, or null". Null is an ordinary value there rather
than a startup failure, and moving where the token lives is a change to one
class.

### What the endpoint answers

`GET /api/hatch/utilization` is `[RequireAdmin(AcceptScope = "hatch")]` like the
rest of the module. It answers `204 No Content` when no token is configured, and
otherwise:

```json
{
  "state": "ok",
  "readAt": "2026-09-07T08:12:03Z",
  "limits": [
    { "window": "session", "label": "Session", "percent": 17,
      "tone": "normal", "resetsAt": "2026-09-07T12:00:00Z", "isActive": true }
  ],
  "credits": { "isEnabled": true, "monthlyLimit": null, "usedCredits": 0,
               "currency": "USD", "spendLimitReached": false }
}
```

Every name in it is Hatch's own. The account's spelling stops at
`ClaudeUsageClient`, so the day a field is renamed upstream there is one file to
fix and no page that has gone blank — and a test asserts that not one of the
account's field names survives into the body.

- `state` is the whole of the degraded story. `ok` — read within the freshness
  window. `stale` — the account could not be reached and this is the last good
  reading, whose age `readAt` gives. `unknown` — could not be reached and there
  has never been a good reading, so `limits` is empty and `readAt` is null.
- `window` is `session`, `weekly`, `weeklyModel` or `other`, in the order the
  account listed them. `other` is the carry-through for a kind Hatch has never
  seen: it is rendered, not dropped and not thrown on.
- `label` is what the row is called on screen, decided on the server —
  `Session`, `Weekly`, the account's own display name for a model-scoped row,
  and for `other` the account's own kind with its underscores turned to spaces.
  So a fourth kind draws a row with a plain name rather than a blank one, and
  **no model name is written down in this repository**.
- `tone` is `normal`, `warn` or `danger` — the *decision*, not the account's
  word for it, so the rule lives in one file on the server and the client paints
  what it is told. Severity upstream is an open vocabulary and only `normal` has
  ever been observed, so a severity Hatch knows maps and anything else falls
  back to the percentage (90 and up is danger, 75 and up is warn). Without that
  fallback the first new word upstream would paint a spent window calm.
- `credits` is null when the account reports no extra usage block at all, and
  the modal then says nothing about credits.

### How often it is actually read

The reading is held by a process-wide cache, and the cache holds the last good
one **forever** — not an `IMemoryCache` entry, because "the last good reading,
however old" is exactly what an eviction policy would throw away and it is what
a `stale` answer is made of.

Two floors bound how often somebody else's endpoint is asked:

- **Five minutes of freshness**, with a semaphore and a re-check inside it, so
  twenty open tabs polling every two minutes are one upstream read.
- **Sixty seconds after a failure.** Without it the failure path would be the
  only path with no rate limit on it, and an outage would become a request per
  tab per poll.

`?refresh=true` ignores both. It is the modal's refresh control: one person
pressing a button once, which is not what the floors exist to bound.


## The leaderboard

The battery says how much Claude is left. The leaderboard says **what the nights
cost, and on what** — which is a question account-wide utilization cannot answer
at all, because it is honest and anonymous and no arithmetic over it can say
which epic ate the evening.

Every number on the page comes from one table: `hatch.work_log_entries`, the row
the dispatcher writes at the end of every unattended increment. It carries the
session id, the issue, both instants, the duration, the turns, the four token
counts, the notional USD, whether the run errored, and what the session said it
did in its `work-log` block. **No Claude credential is anywhere in this path.**
A leaderboard on an installation with no subscription token is the whole
leaderboard rather than a reduced one.

The page is at `/leaderboard` in the Hatch app, beside Plan in the primary nav —
the two pages that read across the whole board rather than about one ticket. It
holds three things over one window and one optional issue filter:

- a **ranking** of the top-billing sessions, captioned in the house's own voice.
  A leaderboard of most expensive agent runs is funnier than it is useful, and
  being funny is how it gets read; it should still be exactly right.
- a **table** of the sessions in the window, filterable to one issue and
  everything beneath it, sortable by tokens, by notional USD or by when it ran.
- a **graph** of spend over time, one bar per bucket.

Tokens are the headline everywhere and notional USD is beside them, sortable —
so the day an account is billed per token, the money becomes the headline and
nothing has to be rebuilt.

### The two reads behind it

```
GET /api/hatch/work-log/sessions?from=&to=&ancestorKey=&sort=&limit=
GET /api/hatch/work-log/history?from=&to=&ancestorKey=&bucket=&offsetMinutes=
```

They take the same range and the same `ancestorKey`, and **`ancestorKey` means
the same thing on both**: the issue itself *and* everything beneath it at any
depth, from `Rollup.DescendantIdsAsync`. That is deliberately not how the same
word reads on `GET /api/hatch/issues`, and the reason is that a planning session
run against an epic is money no child holds. It is also what keeps the ranking,
the table and the graph from ever describing different populations.

Four rules worth knowing before reading either:

- **A session is in the range when `from ≤ EndedAt < to`**, half-open on both
  reads, so a session on a boundary lands the same way in the table and in the
  graph. `EndedAt` is the instant the spend was known.
- **The totals cover the whole filter, not the page that came back.** `sessions`
  answers at most `limit` rows (100 by default, 500 at the most) and folds every
  row in the filter for its totals, so a capped table adds up honestly — and a
  caller tells the two apart by comparing `totals.sessions` with the rows it
  got. The page says so on screen rather than leaving a larger total to read as
  a bug.
- **`firstSessionAt` and `lastSessionAt` ignore the range** and respect the
  filter. They are what tell *nothing has ever been logged here* apart from
  *nothing ran in the window you asked for* — two different sentences, and the
  page says whichever is true once rather than in each empty section.
- **`sessions` uses the range as given; `history` snaps it outward onto the
  bucket grid** and names the bucket size it chose. The page's range presets are
  computed on that same grid, so for every range it offers the two reads
  describe one window and their totals are equal. A `from`/`to` typed into the
  URL by hand may be off-grid, and then the graph covers a little more than the
  table — so the page labels its window from what came back rather than from
  what it asked for.

An errored session's spend counts in all of it. It ran, and it was billed for
running; `totals.errors` says how many, and the table marks them.

### The graph

`history` folds the same rows along a time axis instead of the hierarchy, and
the page draws one bar per bucket, oldest at the left.

- **The page asks for no bucket size.** The server chooses one from the length
  of the range — hourly up to two days, daily past it — and names it back, and
  the graph labels its axis from the answer. A caller that asked in days and got
  hours has to say hours.
- **A bucket in which nothing ran is drawn as a zero, not as a gap**, and its
  slot stays hoverable. A poll's history has gaps because a missing reading
  means nobody looked; a work log has none, and drawing an empty hour as a break
  would assert an absence where there is a measurement.
- **Tokens or notional USD, one at a time, never on a shared axis.** They differ
  by six orders of magnitude and a second y-axis would draw two lies crossing,
  so the control rescales the whole graph. The choice is in the URL as `measure`
  with the rest of the page's state.
- `offsetMinutes` puts a daily bucket on the reader's midnight. An overnight run
  split across UTC midnight is two half-nights nobody worked.

It is hand-cut SVG in the house's own tokens — the Hatch app carries no charting
dependency, and the geometry lives in a tested function rather than in the
component.

**Nothing on this page writes**, and the absence is the guarantee rather than a
convention: there is no control that could, and the browser API client has no
function that could. A work log row is written by the dispatcher with an API
key, and `POST /api/hatch/issues/{key}/work-log` refuses a person outright — see
`IssueWorkLogController.NotAKey`.


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

**The playbook a dispatch carries holds the *effective* model and effort.**
Where the issue names one of its own it is folded in over the matched row's,
and everything else on that row — the transition, the types, the prompt — stays
its own, so the answer names the playbook that spoke *and* the values that won.
In place rather than in a second pair of fields beside them, deliberately: a
client that had to remember to check the second pair is a client that will one
day spawn `sonnet` on a ticket set to `opus`, and that failure is silent.
Nothing is hidden by it — `issue.modelOverride` rides the same payload, and is
what a printed line reads to say where the value came from.

An override changes what a dispatch costs and never whether one happens.
Nothing in the refusals below consults one: an issue with no playbook for its
next move is refused in the same sentence whether it names a model or not.

**Six refusals**, and two of them are rules of the whole loop rather than
missing configuration:

1. The issue is already in a terminal column — there is nothing after it.
2. There is no column to its right.
3. The next column **is** terminal — *only the operator decides that something
   shipped*.
4. Something else holds a live [claim](#claim) on it — *somebody is working this
   right now*, named with the runner it is being worked from and when it was
   last heard from.
5. The issue holds an unanswered question — *it is waiting on a person, not on
   an agent*.
6. Something it [depends on](#dependency) is unfinished, and the move is into
   the column where the code gets written.

…and then, if none of those, the ordinary one: no playbook covers this
transition for this type.

The claim is fourth rather than last because it is the only one of these that
says work is happening *now*; everything under it is about whether the issue
could be worked at all, and "no playbook covers this" is a true sentence about
the wrong thing when another runner is three minutes in.

The question is checked before the playbook, because "nobody has answered you"
is a more useful sentence than "no playbook covers this" when both are true. It
is also what keeps a question from being asked twice: without it, the loop's
only reaction to an open question would be to spawn another session that asks
it again.

`work/next` folds past everything blocked in silence — "nothing to do" is the
useful answer and a list of reasons is not — while `work/{key}`, which somebody
asked for by name, returns the refusal rather than a 404, because a person who
named a ticket is owed the sentence saying why it cannot move.

### One more, on `next` alone

The refusals above are facts about an issue. Two further rules are the *loop's
policy* — what an unattended run may **start**, as opposed to what may move — so
they are asked on `work/next` and not on `work/{key}`. A person who names a
ticket is giving an instruction, and housekeeping does not overrule it.

The issue's **type** is not among them, and was never the loop's to decide:
which types a move applies to is [the playbook row's](#playbooks) to state, and
a type an unattended run does not pick up is a transition no playbook covers
for that type. That fold is the last one in the list above, and it names the
Playbooks page because that is where the fix is.

1. **Its ready date has arrived**, read against the caller's calendar day
   (`?offsetMinutes=`) rather than the server's. A card folded off the board is
   not one an unattended pass should be spending an increment on — but somebody
   who names a ticket ahead of its date has said the date is not the point
   today, and is given the dispatch rather than a lecture about it.
2. **Nobody's name is on it** — nobody's meaning no *person's*. An issue
   [assigned](#assignee) to somebody is theirs to do, and a pass that took it
   anyway would be taking work off a person who had said they wanted it.
   Assigning something to yourself is therefore how you take it off the night
   shift, which is a gesture you already had a reason to make.

   **People only**, and the asymmetry is the point: an issue assigned to an API
   key is picked up exactly as it always was, because assigning a ticket to
   Claude and having Claude stop working on it would read backwards. An
   assignee whose person has been deleted, or whose key has been revoked, is
   nobody at all — the [liveness rule](#assignee) reaches the dispatcher the
   same as every other reader, so there is no such thing as a ticket held off
   the board by a name that no longer resolves.

An [unmet dependency](#dependency) is deliberately **not** here. It looks like
housekeeping and is not: a ready date is a decision about scheduling, while an
edge is a fact about the work, so `work/{key}` refuses on one too. Somebody who
disagrees takes the edge off, which is one press.

### What a pass skipped

`work/next` folding in silence is right for one increment and backwards for an
unattended loop. Nobody is watching, and the one thing worth having afterwards
is what the pass skipped and why — above all a column and type nobody has
written a playbook for, which reads as a finished board from the outside and is
not one.

`GET /api/hatch/work/queue` answers with every issue the dispatcher considers,
in the order it considers them, each carrying the sentence saying why it cannot
be advanced — or nothing, where it can. It takes the same `ancestorKey` and
`offsetMinutes` and means the same things by them, and each row is what a
dispatch carries minus the playbook prompt, the children and the questions:
those are the payload of one agent working one issue, and loading them for
every row would make a whole-board read expensive for nothing.

**The rows arrive in the board's own order.** Rightmost column first, and
within a column the `(rank, id)` that `GET /api/hatch/board` serves that column
in — so a card's line in the queue is its position on the board, and nothing
between the two re-sorts. That matters because most of a column being folded in
silence is indistinguishable from a column being read out of order, and a
report of one is usually the other.

**The first entry with no reason is what `work/next` returns**, because it is
the same walk. `GetNextWork` is the first clear row of the scan rather than a
second loop that happens to agree with it — two walks that could disagree about
the order of the board is precisely the bug this endpoint exists to expose.

Every fold therefore lives in one place and in one order, most fundamental
first: a next column that is terminal, then a ready date, then an unanswered
question, then an unmet dependency, and last the missing playbook — last because
it is only worth saying about an issue that is otherwise a candidate. The one
that is the *loop's* policy rather than a fact about an issue is asked only when
the pass is asking, so `work/{key}` still ignores it.

The columns with nowhere to go — a terminal one, and a rightmost one that is not
terminal — are absent rather than listed as blocked. An issue the dispatcher
never reaches is not something the pass skipped, and shipped work is not a
backlog.

It is a read, and it costs what a read should: the statuses, the scope, which
dependencies are unmet, the open-question counts and the whole playbook matrix
are each read once for the pass rather than once per row, and the issues are
projected in one batch.

**And it is read rather than left on the server.** Everything needed to tell a
jammed board from a finished one was computed and served here long before
anything printed it, and for a while nothing did unless somebody thought to run
`queue` by hand — so ten minutes of an idle loop saying "nothing on the board is
an agent's to move" read as a broken loop. `hatch` now groups the scan's
sentences and prints them with a count each whenever a pass finds nothing, and
a board with nothing on the dispatcher's path at all says *that* instead. Two
different boards, two different sentences, no second command.

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

**One issue can say the matrix is wrong about it.** An issue carries an optional
`ModelOverride` and an optional `EffortOverride` of its own, and where one is
set it beats whichever playbook speaks for the issue's next move — every one of
them, not a single transition's worth, since a story that is harder than its
column suggests is still the hard one wherever it sits. One pair per issue, good
for every move it ever makes; independent of each other; reaching that issue and
nothing beneath it. They take exactly what a playbook takes, refused in the same
sentence, because it is the same rule called twice. There is no per-issue
*prompt*: a prompt is the method for a transition, and a per-issue method is a
paragraph, which is what the description is for. Setting one is a person's
(`PATCH /api/hatch/issues/{key}/playbook`); reading one is anybody's who can
read the issue.

Seven rows are seeded, for the same reason the columns are: a Hatch whose agent
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

`hatch go-to-work` is [the dispatcher](#the-dispatcher) run in a circle: read
the board, take the one issue an agent may advance, spend one increment on it,
and ask again. Given nothing else it runs until the board has nothing on it that
an agent may move, and then waits — asking again every interval, saying so once
and then rarely — which is what "leave it running overnight" has to mean if the
answer to "is anything left" can change while nobody is watching.

**The loop is a program, not the model.** The alternative was one long session
told to keep going, and it was rejected for two reasons that point the same way:
it would carry six hours of context into its last ticket, and the earliest
decisions in that context are exactly the ones nobody can audit afterwards. A
process per increment starts each ticket cold, on the model and effort the
ticket itself names — or, where it names neither, the ones its
[playbook](#playbooks) does — rather than whatever the session started as, and
costs less for the privilege.

A `--model` or `--effort` typed at `hatch work` still beats both, and is
still not carried across a night: a flag is one operator's opinion about one
increment, while an override is a fact recorded on a ticket somebody read. The
line naming the two values says which of them the issue chose.

### What makes an issue actionable

Seven conditions. An issue is the loop's to pick up when it meets every one, and
the sentence saying which one it failed is what `work/queue` reports:

1. **There is a column to its right, and that column is not terminal.** The end
   of the board is not a transition, and the step into a terminal column is the
   operator's: *only the operator decides that something shipped*.
2. **No live [claim](#claim) is held by somebody else.** It is the only fold
   that says *this is being worked right now*; everything below it is about
   whether the issue could be worked at all, which is why nothing else is said
   about a ticket another runner is three minutes into. A caller naming a claim
   token of its own with `?heldToken=` is not folded by its own lease:
   re-reading the dispatch for a ticket one already holds is not a conflict.
3. **Its ready date has arrived**, read against the caller's calendar day. A
   card folded off the board is not one to spend an increment on tonight.
4. **Nobody's name is on it.** An issue [assigned](#assignee) to a person is
   somebody's to do, and an unattended pass leaves it alone. An issue assigned
   to an API key, or to nobody, is picked up exactly as it always was.
5. **It holds no unanswered question.** It is waiting on a person, and another
   agent sent at it would ask the same thing again or guess at the answer.
6. **Nothing it depends on is unfinished** — and only when the move is into the
   column where the code gets written. Everything left of that still moves; an
   edge is satisfied only once the issue it names is in a terminal column. See
   [Dependency](#dependency).
7. **A playbook covers that transition for that type.** Without one there is
   nothing to say to the session — and a column no playbook leads out of is
   exactly [how a column becomes the operator's](#status), which is why the
   absence is a fold rather than an error. **This is also where the issue's
   type is decided**, and the only place: a type an unattended run does not
   pick up is a type no row names for that move, said in the words that name
   the fix.

Five of them — 1, 2, 5, 6 and 7 — are facts about the issue, and `work/{key}`
asks them too. The other two are the loop's policy and are asked only when the
pass is asking; see [one more, on `next` alone](#one-more-on-next-alone).

**The board is worked right to left**, for the reason the dispatcher gives, and
overnight it is the difference between a shape and a mess: a loop working left
to right would open every draft in the house before it finished a single story,
and the morning after would be a board where everything had started and nothing
had shipped. Right to left, the run pushes whatever is furthest along over the
line before it opens anything new, and the thing furthest along is a pull
request somebody can read.

### One loop at a time

**One loop per checkout**, and what says so is a directory under `TMPDIR`,
holding the pid of the run that took it. A directory because creating one is
atomic on every filesystem this could land on and a file written with a redirect
is not; outside the repository because a lock in a tracked tree is a lock
somebody commits, and one that outlives a reboot is one somebody has to come and
clear by hand. A lock whose owner is gone — killed outright, or a machine that
rebooted out from under it — is cleared rather than honoured, which is the
difference between a loop that survives a crash and one somebody has to let back
in. The refusal names the pid *and* the checkout, because "already running here"
is ambiguous the moment there are two heres.

Per checkout rather than per machine, and that is the whole of what makes a
second loop possible. It was per machine while the [claim](#claim) did not
exist, because two loops with no way to divide the board between them would both
take the same ticket. The board divides it now, and the only thing two loops in
*one tree* would still collide over is the tree — one increment's reset landing
in the middle of another's branch.

**Two spellings of one path are one checkout.** On a case-insensitive filesystem
`/x/code/Aerie` and `/x/code/aerie` are one directory, and a lock keyed on the
string would let two loops into it, which is the one thing the lock is for. So
the key is the canonical path: each segment resolved to the name the directory
it sits in actually holds, with symlinks followed first. That is the answer
device-and-inode would give on Unix and is a question Windows can also answer —
and where a filesystem *is* case-sensitive, both spellings are separate entries
and both survive untouched, which is right there too.

So the two questions the `TMPDIR` directory used to answer together are answered
separately, each by the thing that can: the claim says one runner per ticket,
and the lock says one runner per tree.

The claim was once deferred, and each reason it was deferred has an answer.
A lease needs an expiry, and the expiry is lazy: a claim is dead when its
heartbeat is older than the TTL, evaluated by whoever reads the row, so there is
no timeout to tune and no sweeper to notice has stopped. An expiry needs a
heartbeat, and the heartbeat is the runner's own — it is sent by the process
holding the lease, not by anything the board has to run. And a run that dies
mid-increment leaves a row nothing may touch for exactly one TTL, after which
the next pass takes it with no operator action — and the lock it left behind is
cleared by the next loop in that checkout, so a `kill -9` needs nobody to tidy
up after it.

### What a runner does, in order

One pass, from the board to the release:

1. **Read the queue.** `work/queue` applies the loop's policy — the ready date,
   the person-assignee fold — and answers with every row and the reason it was
   folded. It has already folded past everything under somebody else's live
   claim, so what comes back clear is a shortlist.
2. **Claim the first clear row.** A `409` is an answer and not a fault: somebody
   took it between the scan and the take, and the pass moves on to the next clear
   row. Up to **five** attempts, because a board whose first five are all being
   worked is a board where waiting an interval is the honest thing to do — and an
   unbounded walk would take and release leases down a thousand-card column.
   Five refusals is a **busy** board, reported in its own sentence, naming the
   keys and who holds them. That is deliberately not the sentence an empty board
   gets: one means wait a minute, and the other means the night is over.
3. **Read the dispatch, by name.** `work/{key}?heldToken=…` and never
   `work/next`, which would answer with the first clear row *as it stands now* —
   a different ticket from the one just claimed, once another runner's lease has
   folded the row. The token is what stops the runner's own lease folding its own
   dispatch. A dispatch that comes back blocked is a ticket that changed under
   us: the lease goes straight back, and the walk goes on.
4. **Reset the workspace**, then spawn. In that order, and after the claim: a
   ticket held is a ticket nothing else will start, and a reset before the claim
   would be a fetch spent on an increment that never happens.
5. **Heartbeat**, carrying the last line the runner printed — and only when it
   has changed, so the time a card draws is when the line was printed rather than
   when a heartbeat happened to fire. A `--quiet` increment renders nothing and
   so carries nothing, which is what `--quiet` is.
6. **Release**, however the increment ended.

**A refused heartbeat ends the increment.** The lease expired underneath the run,
was taken over, or was cleared by the operator; the session is stopped rather
than left spending money on a ticket this runner no longer holds, the bill is
still posted — money was spent on that ticket, and a meter reading is not a claim
to have done the work — and nothing else is written. In particular no
[stall](#when-an-increment-does-nothing) is flagged: that would mark another
runner's increment as ours, and the ticket did not move because we stopped, which
the loop already knows. **A lost lease is not one of the three failures that end
a night**, for the same reason: it is the loop working correctly on a busy board.

Every other way a heartbeat can fail — a timeout, a `500`, an origin that did not
answer — changes nothing. The next tick tries again.

### The workspace, between increments

Every increment starts on the trunk, at the tip the remote has it at right now.
The loop fetches, stashes anything the tree was carrying, and puts the checkout
back on the default branch before it spawns anything — so a session's first act
is cutting a branch, and the thing it cuts from is not in question.

This is the loop's job rather than a [playbook](#playbooks)'s for the reason
every load-bearing sentence in a prompt eventually demonstrates: a playbook is
prose, edited by an operator who cannot be expected to know which of its
sentences the next four hours depend on, and read by a model that has a ticket
to think about. A night of unattended increments cannot have one run branching
off the last run's leftovers — that is how one session's unpushed commits arrive
inside another session's pull request — and the way to make something certain is
to stop asking for it. A playbook that says nothing at all about git now gets a
correct base.

Nothing it does destroys work that cannot be got back:

- **Uncommitted and untracked changes go into a stash**, named for the hour it
  was taken, and stay on the machine that took them. A stash rather than a
  discard because whatever is in the tree at two in the morning was probably
  left there by a person, and `git stash pop` is how they get it back. Ignored
  files are not touched, so a dependency directory or a local `.env` is never
  swallowed by one.
- **Committed work is never at risk.** A checkout moves `HEAD`; branches are
  refs, and the branch the last increment pushed is still under its own name.
- The exception is **a commit on the trunk itself and nowhere else**, which the
  reset moves off. The count is printed while there is still something to count,
  and `git reflog` holds the commits.
- **A branch whose upstream on origin has been deleted is deleted here too**,
  named on the terminal with the short sha it pointed at, so `git branch <name>
  <sha>` puts it back. Only that shape: a branch that never had an upstream is
  somebody's half-finished local work and is never touched, nor is one whose
  upstream is still there, nor one tracking a remote that is not origin — only
  origin was fetched and pruned, so a `[gone]` anywhere else is a fact the loop
  did not establish. Otherwise a checkout that has run a week accumulates a
  branch for every ticket it ever worked, long after the pull requests merged.

It runs once an increment is known to be due, not once per pass — on an idle
board the difference is a `git fetch` every interval until morning, against a
remote with nothing to say. The guarantee is about the spawn either way.

Two things can go wrong, and they are not the same thing. A fetch that does not
answer is **a minute of network**, and the loop waits its interval and asks
again, exactly as it does for a board that did not answer. A tree that cannot be
made current — not a repository, a trunk nobody can name, changes that will not
stash because something is half-merged — **ends the night**, because it is the
one condition under which every remaining ticket would be built wrong and no
further increment could tell the difference. Nothing is spawned and nothing is
spent on either.

The branch the trunk is is asked of the repository, not written down here:
`origin/HEAD` first, the remote itself for a checkout that never got one, and
`HATCH_BASE_BRANCH` for an installation that calls it something else.

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
the list `hatch answer` walks. Answering it clears the flag, which is the
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
than a second walk that happens to agree with it — so a runner asks two
questions and cannot get two different boards. Everything
above is decided in one place, in one order, in
[`WorkController.cs`](../src/Aerie.Api/Modules/Hatch/WorkController.cs).

The alternative was **the client**, and it is the cheaper thing to write:
`queue` already parses the board, and folding the not-yet-ready and the
wrong-typed out of it is a few lines. It was rejected because it puts the rules
where the *caller* is, and there is more than one caller — a terminal, a loop,
and the issue page — so the first renamed column would leave two of them
disagreeing about what is workable, silently, with nobody watching. A runner's
whole job is to spend increments and say what happened; it decides nothing about
which.

The second of those two reads is `work/{key}`, named, carrying the runner's own
claim token — never `work/next`. The claim between them is what closes the
window the old pair had: a row read as clear and taken by somebody else in the
meantime comes back from the named read carrying their sentence, and the pass
walks on instead of spawning into it.

That is also why the loop passes no `--model` or `--effort` of its own, though
`work` accepts both. An override typed for one increment is one operator's
opinion about one ticket, and a loop that carried it across a night would be
applying it to tickets nobody looked at.

### Where the loop lives

The CLI is one program — [`src/Aerie.Hatch`](../src/Aerie.Hatch), published as a
single binary called `hatch`. Every command is a command of it: `board`, `next`,
`queue`, `show`, `start`, `move`, `comment`, `pr`, `depends`, `ask`,
`questions`, `answer`, `api`, `config`, `work` and `go-to-work`.

It got there in two steps, for two different reasons.

`work` and `go-to-work` moved first, because of what they do rather than what
they say. Every other command is one request and a sentence about the answer.
These two are a lease with a clock on it, a heartbeat on a background thread, a
session that has to be killed the moment the lease goes, and three signals — and
none of it could be tested, because nothing in this repository tested a shell
script and a race is precisely the thing a hand check cannot catch twice. The
question was asked as "how is the claim verified", and the honest answer was
that in a shell it could not be: the alternative on the table was a stub harness
of some six hundred lines driving `hatch.sh` through a fake `curl` and a fake
`claude` on `PATH`, which is a second program to maintain and one that can only
assert what a recorded request log happens to show.

The other fourteen followed, and not because shell was the wrong language for
`curl | jq` — it was a perfectly good one, and the ported code says the same
things. They followed because an operator who clones Aerie into their own house
has no copy of `scripts/hatch.sh` on their `PATH`, and a tracker reachable from
one checkout is a tracker for one person. `hatch` installs once, runs from
wherever somebody is standing, and keeps its settings with the person rather
than in one repository's `scripts/` directory. The seeded playbooks name that
command, so an agent working a friend's repository is told how to reach the
board in words that are true there.

[`scripts/hatch.sh`](../scripts/hatch.sh) is still here, and every command still
works through it — it finds or builds the binary and hands over. What is left in
it is the two things that are genuinely its own: that door, and the restart
supervisor below, which a process cannot be for itself.

So: a console program in the solution, compiled against
`src/Aerie.Hatch.Contracts` — the same records the API serves, so a fixture that
goes stale does not quietly deserialise into a dispatch with empty fields, it
stops compiling — and asserted in
[`src/Aerie.Hatch.Tests`](../src/Aerie.Hatch.Tests), which `make test` and CI
already run because the solution already builds and tests everything in it. Two
loops racing one ticket, a lease refused mid-session, an interrupt between a
claim and its release, two spellings of one checkout: each of those is a test
that runs in milliseconds against a stub wire and a stub session, with no server,
no key and no agent.

Being cross-platform came along with it, and is worth naming because the shell
was never going to be: `hatch.sh` is Bash 3.2 on purpose, for macOS, and a
Windows operator had no runner at all.

Two things in the shell version existed only because it was a shell, and are
gone rather than translated. The session id and the bill used to travel back
through the render pipeline wearing a control character, because every stage of
a pipeline is a subshell and nothing set in one survives; they are now an object
the renderer writes to. And the line a claim carries used to go through a file
under `TMPDIR` for the same reason, which forced the heartbeat to defend against
reading one mid-write; it is now a field.

`hatch.sh` prefers a built binary and falls back to `dotnet run`, so a checkout
with the SDK needs no build step — `make build-hatch` is the optimisation, and
`HATCH_RUNNER_BIN` names a binary for a machine with no SDK at all. That target
is deliberately a single fast Release build of this machine's own platform,
because the restart path below calls it between increments; `make publish-hatch`
is the other thing, and makes the self-contained single-file binary an operator
downloads — `win-x64`, `osx-arm64`, `osx-x64` and `linux-x64`, all four built on
every pull request.

**A fresh install does not run that target; it opens the Runner page.** The API
image publishes the same four binaries from the same build as itself
(`Dockerfile.api`) and Hatch's **Runner** page hands out the one matching the
browser's platform, beside the revision all of them were built from, the three
things to have installed first, and the two commands to type:

```
hatch config --origin https://hatch.<your domain>
hatch go-to-work
```

`config --origin` is the non-interactive third mode of `hatch config`: it writes
the origin alone and leaves the key and the claude path exactly as they were, so
it is safe to paste on a machine that is already configured. The page fills in
this Hatch's own origin, because the address bar is the one thing about an
install nobody can get wrong.

So `make publish-hatch` is how *this repository* builds the artifact, and the
Runner page is how *a person* gets it. A friend with the stack running needs the
image and nothing else — no SDK, no clone, no copy of this Makefile.

One consequence of publishing trimmed is worth naming, because it fails nowhere
before the operator's machine: a trimmed .NET application has reflection-based
JSON switched off outright, so every wire record is registered in a
source-generated context (`HatchJson`). A record nobody registered refuses by
name at the call that needed it, rather than deserialising into a dispatch with
empty fields.

One difference between the two commands is worth naming, because it is the whole
of how a loop restarts itself. `work` is `exec`'d: one increment has nothing to
carry forward and nothing to come back as, so it replaces the shell rather than
being watched by it. `go-to-work` is *run*, in a loop, because a process cannot
exec itself into a newer build — relaunching the same binary relaunches the same
code, and the new source has to be compiled by something that outlives the
process being replaced. `hatch.sh` was already that something.

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
- **A workspace that cannot be made current** — the other one nobody asks for,
  and the only condition that ends a night without an increment having failed. A
  tree that will not reset is a tree every ticket would be built wrong on, and
  the loop has no way to make it right. A fetch that merely did not answer is
  not this: that is a wait, and the next pass tries again.

One thing that looks like a stop is not: **a restart**. A loop that spends the
night improving this repository is running the version it started with, and
would be until somebody came and stopped it — work that lands at one in the
morning never reaching the run that wrote it. So the loop watches its own source
and comes back as the new version, and the terminal says which trigger fired
rather than going quiet and back in a way that reads as a crash.

The runner asks for it by exiting **75** (`EX_TEMPFAIL`, "try again", which
collides with nothing else it answers with), and `hatch.sh` rebuilds it and runs
it again. Two triggers, because the answer to which one on the ticket was both:

- **Its own source changed on the trunk.** The set is `scripts/hatch.sh` and
  everything under `src/Aerie.Hatch` and `src/Aerie.Hatch.Contracts` — the loop,
  the wire records it is compiled against, and the script that resolves and
  launches it — hashed on disk rather than read out of git, since the files that
  are there are the files that run. The baseline is taken once at startup, and
  the check happens after the reset, which is the only moment new source can
  have arrived. The changed paths are named on the way out.
- **Age**, `--restart-after MINUTES`, thirty by default and `0` to turn it off.
  It is the backstop for the loop this would otherwise miss entirely: an idle
  loop never resets — a fetch every interval all night against a remote with
  nothing to say is a fetch for nothing — so it never sees a change, and would
  sit there on the old code until morning. Read where no claim is held, so a
  restart is never something a ticket is waiting behind.

What survives the restart is what was typed and what has been spent.
`--max-runs`, `--max-spend`, `--until`, `--under`, `--interval`, `--quiet` and
`--stop-file` are all still in argv, which the supervisor re-runs verbatim; the
increments, the spend, the failure streak, the night's start and the two lists
the tally prints travel in a state file the supervisor names once per night. So
a restart cannot outspend `--max-spend` or outrun `--max-runs` by starting over,
and the tally at the end covers the whole night and says how many times the loop
came back. `--until` is carried as the instant it resolved to rather than
re-read, which is the one bound that would otherwise be wrong: `--until 23:59`
typed at 23:58 and re-read at 00:01 means tomorrow, and adds a day to the night.

Three things it will not do. It will not restart holding a claim — the ticket
the deciding pass claimed goes back before the process exits, and nothing is
spawned on that pass. It will not restart-loop on a build that failed: the
supervisor says so and runs the version that is there, and that incarnation took
its baseline from the source already on disk, so it does not ask again for the
same change. And it will not restart at all under `--once`, under
`--no-restart`, or when started by hand rather than through `hatch.sh` — the
state path is what tells the runner somebody is standing over it, and a runner
with nobody to rebuild it is the loop it started as.

Three things that look like reasons to stop are not. **A lost lease** is the
loop working correctly on a busy board — the ticket went to a runner already
further into it — and does not count toward the three. **A board that did not
answer** is a minute of bad network, and a loop that ended on one is a loop
somebody has to sit with. And **a busy board** is a wait, not an ending: every
candidate being worked elsewhere is a report, and it is deliberately not the
sentence an empty board gets.

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

That mirror is in two halves, because it is loaded on every increment and most
increments only write code. `CLAUDE.md` carries what every session needs;
[`hatch-planning.md`](hatch-planning.md) carries the shapes only a session that
files or reshapes work does — `parentKey`, the two dates, the bulk endpoint and
the plan reads — and is read on demand.

### Reaching Hatch

`hatch` is the calls a working session actually makes — `board`, `next`,
`queue`, `show`, `start`, `move`, `comment`, `pr`, `depends`, `ask`,
`questions`, `answer`, `config`, `work`, `go-to-work`, and `api` for everything
else. It finds a column by name rather than by id — on the letters and digits
alone, so `todo` at a terminal reaches the column the board calls `To Do` — and
folds off cards whose ready date has not arrived, exactly as the board does.
`hatch --help` lists the surface and every subcommand takes `-h` for its own.

Only `work` and `go-to-work` need to be run inside a git checkout, because only
those two are about a codebase. The other fourteen are one request and a
sentence about the answer, and `hatch board` from a directory that has never
been a repository is the ordinary case.

Its settings are read in three layers, highest first: an exported `AERIE_BASE`
or `AERIE_HATCH_KEY`, then `scripts/.env` in the checkout you happen to be
standing in, then the per-user file `hatch config` writes — mode 600, under the
platform's application-data directory, outside every repository. An operator
configures once and every checkout on that machine is reached; a repository that
wants to pin its own origin still can. **The key lives outside the artifact** —
never a tracked file, never a value in a commit, never pasted into an issue.
That is not ordinary secret hygiene: Aerie ships to other operators, and a
credential in the artifact is one operator's credential inherited by everyone
who clones it ([`ethos.md`](ethos.md)).

The key is optional. Against a Hatch started with its wall off
([`auth-architecture.md`](auth-architecture.md), "Local mode") there is no
credential to present, and every call names itself with an `X-Hatch-Runner`
header instead — which is a name and not a proof, and only that Hatch reads one.
So a `401` is two different sentences, and says which happened: a key that was
sent and refused is a key to go and look at, and no key at all is a Hatch with
its wall on and nothing to look at yet.

In this repository, [`scripts/hatch.sh`](../scripts/hatch.sh) still answers to
every one of these commands and hands them to the program, so nothing anybody
has typed here stops working.

`hatch work` reads `work/next`, then spawns a headless session with the
playbook's prompt, model and effort. It prints the session id first and last
with the `claude --resume` command beside it, and streams what the run is doing
as it happens — every tool call, a thinking-token pulse, and a heartbeat naming
what it is still waiting on. That last part is not a nicety: the CLI's default
output prints nothing until the run ends, so a four-minute increment was four
minutes of blank terminal indistinguishable from a hang, and the fix for "is it
working" is showing the work, not a spinner.

`hatch go-to-work` is `work` in a circle, and is
[its own section](#the-unattended-loop) — what it may pick up, what it does
about a ticket that did not move, and what it stops for.

`hatch queue` reads the scan and prints it, one issue a line — key, type,
column, and either the reason the pass would fold past it or the transition it
is clear for, in the dispatcher's order: rightmost column first, and within a
column the order the board itself draws that column in. `hatch queue AER-1`
scopes it to one epic's subtree. It spawns nothing and writes nothing, and an
empty board prints a sentence saying so rather than a blank line: "there is
nothing" and "something went wrong and printed nothing" look identical
otherwise, which is the one thing a run nobody watched cannot afford to be
unsure about.

This is the long form, and it is no longer the only way to see the folds.
`work` and `go-to-work` group the same sentences and print them with a count
each — worst first, one line per distinct reason, so a column of two hundred
cards folded for four reasons is four lines — whenever a pass has an increment
to run or finds nothing at all. A board with nothing on the dispatcher's path
says *that* instead, which is the difference between a finished board and a
jammed one, said without anybody having to run a second command. The idle loop
reprints the digest when it changes and otherwise says it is still alive every
ten minutes; `queue` is where the counts turn back into tickets.

### What an agent does with a ticket

- **Read it.** The description is the brief; the comments, the answered
  questions, and the event trail are the context.
- **Move it to *in progress* before starting.** The board saying what is being
  worked on right now is the board's whole job.
- **Comment the commit sha and the branch, and record the pull request.** The
  ticket is where somebody looks in six months, and a comment naming a commit is
  what makes that search short. The pull request is a field rather than a
  sentence — `hatch pr AER-12 <url>` puts it there, and the issue page draws
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
hatch ask AER-12 "How should drain retries be scoped?" \
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

The operator answers at a terminal (`hatch answer` walks the open ones one at
a time, serially — a list of six printed at once gets answered in aggregate,
which is how a wrong assumption gets in) or on the issue page. The answer is a
comment bound to its question, so `work` carries the decisions already made into
the next session's prompt under **Decisions already made**. Those are settled;
build on them, and do not reopen them.

What the repository can answer, answer by reading the repository. A question the
code already settles is a round trip through a person for nothing.

## Deferred on purpose

- **The loop lifted onto the Aerie platform, headless.** Two checkouts on one
  box and several boxes in the house are what the [claim](#claim) makes
  possible; a loop that is a service rather than a terminal somebody left open
  is the run after that, and it is not this one. What is deferred is the
  scheduling, the credentials and the place the output goes — not the mutex,
  which is built.
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
