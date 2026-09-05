# Working on Aerie

Notes for Claude. Read [`README.md`](README.md) for what Aerie is,
[`docs/ethos.md`](docs/ethos.md) for the one rule that constrains every commit
(*nothing in this repo may be true of exactly one installation*), and
[`docs/plans/README.md`](docs/plans/README.md) for how plans are worked.

## Hatch: the ticket is the unit of work

[Hatch](docs/plans/pjm.md) is the house project tracker, at
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
actually makes — `next`, `show`, `start`, `comment`, `ask`, `answer`, and `api`
for everything else. It reads its settings from `scripts/.env` or the environment, finds the
todo column by name rather than by id, and folds off the cards whose ready date
has not arrived, exactly as the board does. Prefer it to raw `curl`; the
raw calls below are what it is doing.

### One increment, unattended

```
./scripts/hatch.sh work            # the next thing due, whatever it is
./scripts/hatch.sh work AER-12     # ...or this one
./scripts/hatch.sh work --dry-run  # print the instruction, spawn nothing
```

`work` asks the server what to do next and how, then spawns a headless session
to do it. What that session is told, which model it runs on, and how much
effort it spends are a **playbook**: a row per (status transition, issue types)
that the operator edits on Hatch's Playbooks page. Turning a paragraph in the
inbox into an epic with stories under it is not the same job as implementing an
already-specified task, and the matrix is where that difference is written
down.

Two things about it are worth knowing before working on this repo:

- **The board is worked right to left.** `work` with no argument takes the top
  of the rightmost column that still has something an agent may advance.
- **Playbooks are readable by a key and writable only by a person.** If a
  playbook is wrong, say so on the ticket. Do not try to route around it: the
  API refuses, and it refuses on purpose.

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

### Planning a ticket

A planning session leaves the plan **on the ticket**, not in a chat log:

- `PATCH /api/hatch/issues/AER-12` with a `description` carrying the acceptance
  criteria — what "done" means, in a form somebody else could check.
- `POST /api/hatch/issues` with `parentKey: "AER-12"` for each story or task the
  work breaks into. An epic takes stories; a story takes tasks.
- Move it out of the inbox: `POST /api/hatch/issues/AER-12/move` with the
  `statusId` of **todo**. `GET /api/hatch/board` names the columns.

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
- `POST /api/hatch/issues/AER-12/comments` with the commit sha, the branch, or
  the PR — the ticket is where somebody looks in six months, and a comment
  naming a commit is what makes that search short.
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
