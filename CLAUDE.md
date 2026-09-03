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

**The key lives outside this repo** — a local secrets file, an environment
variable, a password manager entry. Never a file in the working tree, never a
value in a commit, and never pasted into a plan or an issue. That is not
ordinary secret hygiene: Aerie is headed for release to other operators, and a
credential in the artifact is one operator's credential inherited by everyone
who clones it. The `aerie_ak_` prefix exists so that a key which slips into a
diff is recognisable on sight.

The key carries the `hatch` scope and reaches `/api/hatch/*` and nothing else.
Every other operator endpoint — minting keys, revoking sessions, editing the
house — refuses it, by design. If a request comes back `403`, the key is
working and the route is not one a key may take; ask the operator.

### Given a ticket

A `hatch.${DOMAIN}/issues/AER-12` link, or a bare `AER-12`, means:

```
GET  ${AERIE_BASE}/api/hatch/issues/AER-12          # title, description, status, parent, children
GET  ${AERIE_BASE}/api/hatch/issues/AER-12/comments
GET  ${AERIE_BASE}/api/hatch/issues/AER-12/events   # what has happened to it, newest first
```

Read it, then get to work. The description is markdown and is the brief.

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
