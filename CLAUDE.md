# Working on Aerie

Notes for Claude. Read [`README.md`](README.md) for what Aerie is,
[`docs/ethos.md`](docs/ethos.md) for the one rule that constrains every commit
(*nothing in this repo may be true of exactly one installation*), and
[`docs/hatch.md`](docs/hatch.md) for the tracker every ticket below comes off.

## Hatch: the ticket is the unit of work

[Hatch](docs/hatch.md) is the house project tracker, at
`hatch.${DOMAIN}`. It is where work is described, and it is reachable
programmatically — so a link to a ticket is a complete instruction, and the
ticket is where the answer goes back.

### The key

Access is an API key: `Authorization: Bearer aerie_ak_…`, minted by the
operator on the admin app's **API keys** page and shown exactly once.

**The key lives outside the artifact** — never a tracked file, never a value
in a commit, and never pasted into a plan or an issue. That is not ordinary
secret hygiene: Aerie is headed for release to other operators, and a
credential in the artifact is one operator's credential inherited by everyone
who clones it. The `aerie_ak_` prefix exists so that a key which slips into a
diff is recognisable on sight.

Where it does live is one command:

```
./scripts/hatch.sh config       # asks for the origin and the key
./scripts/hatch.sh config --show
```

That writes `scripts/.env` — mode 600, ignored by git, read by every `hatch.sh`
command. An exported `AERIE_BASE` or `AERIE_HATCH_KEY` still wins over the
file, so a one-off origin is a prefix on the command line. A shell profile or a
password manager works too; the file exists so that a credential does not have
to sit in the environment of everything you run all day.

The key carries the `hatch` scope and reaches `/api/hatch/*` and nothing else.
Every other operator endpoint — minting keys, revoking sessions, editing the
house — refuses it, by design. If a request comes back `403`, the key is
working and the route is not one a key may take; ask the operator.

### From a terminal

[`scripts/hatch.sh`](scripts/hatch.sh) wraps the calls a working session
actually makes — `board`, `next`, `queue`, `show`, `start`, `move`, `comment`,
`pr`, `ask`, `questions`, `answer`, `work`, `go-to-work`, and `api` for
everything else. It reads its settings from `scripts/.env` or the environment, finds a
column by name rather than by id — on the letters and digits alone, so `todo`
reaches the column the board calls `To Do` — and folds off the cards whose ready
date has not arrived, exactly as the board does. Prefer it to raw `curl`; the
raw calls below are what it is doing.

### One increment, unattended

```
./scripts/hatch.sh work              # the next thing due, whatever it is
./scripts/hatch.sh work AER-12       # ...or this one
./scripts/hatch.sh work --under AER-1 # ...or the next thing under one epic
./scripts/hatch.sh work --dry-run    # print the instruction, spawn nothing
```

`--under` points an evening at one project: the same rule picks what is next,
asked of one epic's subtree instead of the whole board. It and a bare key are
the two ways of naming the work and cannot be given together.

`work` asks the server what to do next and how, then spawns a headless session
to do it. It streams what that session is doing as it happens — every tool call,
a thinking-token pulse, and a line every twenty seconds of silence saying what
it is still waiting on — because a print-mode run that says nothing for four
minutes is indistinguishable from a hung one. `--quiet` restores the old
behaviour; `HATCH_HEARTBEAT` sets the silence before it speaks up, and `0` turns
that off.

The first line it prints is the session id:

```
hatch: session 764ca76a-…
hatch:   join it with  claude --resume 764ca76a-…
```

That is how you prod a run without throwing it away — `claude --resume <id>`
opens the same conversation, with everything it has done in context, so you can
redirect it instead of starting over.

What that session is told, which model it runs on, and how much
effort it spends are a **playbook**: a row per (status transition, issue types)
that the operator edits on Hatch's Playbooks page. Turning a paragraph of a
draft into an epic with stories under it is not the same job as implementing an
already-specified task, and the matrix is where that difference is written
down.

Two things about it are worth knowing before working on this repo:

- **The board is worked right to left.** `work` with no argument takes the top
  of the rightmost column that still has something an agent may advance.
- **Playbooks are readable by a key and writable only by a person.** If a
  playbook is wrong, say so on the ticket. Do not try to route around it: the
  API refuses, and it refuses on purpose. The same is true of a single ticket's
  own model and effort — an operator can pin either on the issue page, where it
  beats every playbook that could speak for that issue, and an agent cannot set
  one for the same reason it cannot write a playbook.

### The same thing, all night

```
./scripts/hatch.sh go-to-work                 # increments, back to back, until told to stop
./scripts/hatch.sh go-to-work --once          # ...one pass, and out
./scripts/hatch.sh go-to-work --under AER-1   # ...inside one epic, all night
./scripts/hatch.sh go-to-work --interval 300  # ...asking this often when there is nothing
./scripts/hatch.sh go-to-work --max-runs 5 --max-spend 20 --until 08:00
./scripts/hatch.sh go-to-work --stop-file /tmp/stop   # touch it to end the loop
```

`go-to-work` is `work` in a circle: the next actionable issue, one increment,
and ask again. It takes `--under` or nothing at all, never a ticket — its
question is "what is next", asked over and over, and one ticket cannot be the
answer to that twice. One increment on one ticket is `work AER-12`. Nothing
stops it by default; the flags above are how a night is bounded, and three
failed increments in a row stop it on their own.

**This is most likely how you got here.** Assume nobody is reading the terminal,
and that the next increment starts the moment yours ends. Four things follow,
and they are why this section is in a file an agent reads:

- **Leave the ticket somewhere new.** An increment that ends with the ticket in
  the column it started in is a *stall*: the loop writes a comment naming the
  session that ran, opens a question against the issue, and moves on — and
  nothing further is dispatched there until a person answers it. That guard
  exists so a bad ticket costs one increment instead of a night, but a sentence
  you wrote about why you stopped is worth more than the one it writes for you.
- **Asking is a full stop, not a pause.** An open question blocks the ticket
  from being dispatched at all. The loop takes the next thing and yours waits
  for a person, so ask and stop — do not ask and keep building.
- **Record the pull request**: `./scripts/hatch.sh pr AER-12 <url>`. The loop
  will not start a second story under the same parent while one is awaiting
  review, so the operator reading yours is what unblocks the rest of that epic.
  A URL somebody has to find in a comment makes that read take longer than it
  needs to.
- **Read the board before assuming it is empty.** `./scripts/hatch.sh queue`
  prints every issue a pass would look at, in the order it looks, each with the
  reason it would be folded past — or the transition it is clear for.
  `queue AER-1` scopes it to one epic. It spawns nothing and writes nothing. A
  column and type nobody has written a playbook for reads as a finished board
  from outside and is not one, and this is where that shows up.

The rules deciding all of this live on the server, not in the script:
[`docs/hatch.md`](docs/hatch.md) has the six conditions that make an issue
actionable and the reasoning behind each.

### When a decision is not yours to make

Some things a ticket needs are not an implementer's to choose: a product call, a
name that will be lived with for years, a tradeoff with no technically correct
side. Do not guess, and do not quietly take whichever branch is cheapest to
build. Ask on the ticket, and **name the choices**:

```
./scripts/hatch.sh ask AER-12 "How should drain retries be scoped?" \
    --recommend "Per-node: one budget each, so a slow node cannot starve the rest" \
    --option   "Global: one budget for the drain, simpler to reason about"
```

Each `--option` becomes something the operator presses — a card in the web UI, a
number at a terminal — instead of a paragraph they have to read twice and then
compose a reply to. So the body is the question alone, in a sentence; the
tradeoffs go inside the options they belong to; and `--recommend` marks the one
you would take, of which there may be one. A label is short enough to press and
reads as a decision on its own — `per-node`, not `we should scope them per
node` — because the label becomes the answer's own text.

Ask in prose (`ask AER-12 "…"` with no options) only when the answer is
genuinely open-ended — a name, a description, a direction.

One call per question, so each can be answered on its own. Then **stop**.
An unanswered question blocks the ticket from being dispatched at all, so
nothing further will be spawned at it until somebody answers, and anything built
past the question is built on a guess.

The operator answers on the terminal (`./scripts/hatch.sh answer`, which walks
them one at a time) or on the issue page, and the answer is a comment bound to
the question — so `work` carries the decisions already made into the next
session's prompt, under **Decisions already made**. Those are settled. Build on
them; do not reopen them.

What the repository can answer, answer by reading the repository. A question the
code already settles is a round trip through a person for nothing.

### Given a ticket

A `hatch.${DOMAIN}/issues/AER-12` link, or a bare `AER-12`, means:

```
GET  ${AERIE_BASE}/api/hatch/issues/AER-12          # title, description, status, parent, children
GET  ${AERIE_BASE}/api/hatch/issues/AER-12/comments
GET  ${AERIE_BASE}/api/hatch/issues/AER-12/questions?open=false   # decisions asked for, and given
GET  ${AERIE_BASE}/api/hatch/issues/AER-12/events   # what has happened to it, newest first
```

Read it, then get to work. The description is markdown and is the brief.

### Finding tickets, and editing a lot of them at once

```
GET  ${AERIE_BASE}/api/hatch/issues?ancestorKey=AER-12&type=task&statusId=2
POST ${AERIE_BASE}/api/hatch/issues/bulk
```

The filters are `projectId`, `type`, `statusId`, `parentKey`, `ancestorKey`,
and `text`; they combine with AND and every one is optional. Two of them are
worth knowing: `ancestorKey` returns everything below an issue at any depth —
an epic's stories and their tasks in one request — and `parentKey=` (empty)
finds the issues with no parent at all.

The bulk endpoint takes `keys` and any of `type`, `statusId`, `parentKey`,
`readyAt`, `dueAt`, with the same rules a single `PATCH` follows: a field left
out is left alone, and `""` clears one. It answers with `changed`, `unchanged`
and `failures` — a key that refuses the edit is reported with its reason and is
left exactly as it was, while the rest of the batch goes through. Re-applying
the same edit writes nothing, so it is safe to run twice.

Prefer it to a loop of `PATCH`es when moving a whole epic's worth of work: one
request, one audit timestamp, and one place to read what did not apply.

### Where a project stands

Two reads answer "how far along is this" without walking the tree yourself:

```
GET  ${AERIE_BASE}/api/hatch/plan             # every epic, and what it adds up to
GET  ${AERIE_BASE}/api/hatch/plan/AER-12      # one issue, and each of its children
```

A **leaf** is an issue with no children, and it is the unit both of them count:
a subtree's total is the histogram of its leaf descendants by status, and an
issue with no children counts as itself, one leaf in its own column. So a
parent's total is exactly the sum of its children's, and a stack of them agrees
with the one above it. A parent's own column never lands in its own total — a
story sitting in In Review whose tasks are all in To Do reads as To Do, because
the tasks are the work — and ready dates are not consulted, because a card folded
off the board is still work.

Every total is the same shape: `leaves`, `done` (the leaves in a terminal
column), `waiting` (open questions on the issue and everything below it), and
`slices`, one `{ statusId, count }` per column in board order with the empty
ones left out.

`/api/hatch/plan` answers with `epics` — the ones with no parent, each carrying
the epics beneath it, so the tree is drawn once — and `loose`, the total of
everything hanging under no epic at all. `?projectId=` narrows both halves.
`/api/hatch/plan/{key}` answers with the issue's own total and its direct
children in rank order, each with its own; a child's `isLeaf` says whether it
has work beneath it or is the work.

Read the plan when the question is *which* project to further; then
`work --under AER-12` takes the next thing inside the one you picked.

### Planning a ticket

A planning session leaves the plan **on the ticket**, not in a chat log:

- `PATCH /api/hatch/issues/AER-12` with a `description` carrying the acceptance
  criteria — what "done" means, in a form somebody else could check.
- `POST /api/hatch/issues` with `parentKey: "AER-12"` for each story or task the
  work breaks into. An epic takes stories; a story takes tasks.
- Move it out of the drafting column: `POST /api/hatch/issues/AER-12/move` with
  the `statusId` of the column that holds specified work awaiting selection
  (**Backlog** on a stock board). `GET /api/hatch/board` names the columns.

Two optional dates go on the same `PATCH`, and either may be a date
(`2027-08-15`) or an instant (`2027-09-01T17:00:00Z`); `""` clears one:

- `readyAt` — the day the work *can* start. An issue whose ready date has not
  arrived is folded off the board, so anything that has to wait for a soak
  test, a renewal window, or a date on a calendar is filed now and surfaces on
  its own. Prefer this to a note in a description saying "not until March".
- `dueAt` — the day it is *owed*, drawn on the card and warming from three days
  out. A past date is accepted without comment.

### Implementing a ticket

- Move it to **in progress** before starting, so the board says what is being
  worked on right now. That is the board's whole job.
- `POST /api/hatch/issues/AER-12/comments` with the commit sha and the branch —
  the ticket is where somebody looks in six months, and a comment naming a
  commit is what makes that search short.
- `./scripts/hatch.sh pr AER-12 <url>` if you opened a pull request. It is a
  field on the issue and a link on the issue page, not a URL somebody has to go
  looking for in a comment; `pr AER-12` with no URL reads back the one that is
  set, and `--clear` takes it off.
- **Never move a ticket to a terminal status.** Only the operator decides that
  something shipped. Implementation ends in *in progress*, with a comment
  saying what landed and what did not.

Every one of those calls writes an event carrying the key's name as the actor,
so the trail says who did what without anybody being asked to record it.

## House rules

- Build with `make` (`make build`, `make test-api`, `make test-web`), never a
  bare `dotnet` — the npm step needs the shell profile.
- Never commit unless asked.
- No hardcoded domains, addresses, hostnames, or people, anywhere — including
  in plans and comments. See [`docs/ethos.md`](docs/ethos.md).
- The operator does all browser and UI verification. The implementer's
  definition of done is lint, build and tests green.
