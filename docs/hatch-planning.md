# Hatch: the shapes a planning session needs

Read this when the increment **files or reshapes work** — turning a draft into
an epic with stories under it, breaking a story into tasks, dating something
that cannot start yet, or moving a batch of issues at once.

An implementation increment does not need any of it: it reads one ticket, writes
code, comments the sha, and stops. [`CLAUDE.md`](../CLAUDE.md) carries what every
session needs and points here for the rest, so that a session which will never
file an issue does not pay for the shapes on every increment.

The canonical route table, with every verb and every refusal, is
[`hatch.md`](hatch.md#api-surface). This is the working subset.

## Planning a ticket

A planning session leaves the plan **on the ticket**, not in a chat log:

- `PATCH /api/hatch/issues/AER-12` with a `description` carrying the acceptance
  criteria — what "done" means, in a form somebody else could check.
- `POST /api/hatch/issues` with `parentKey: "AER-12"` for each story or task the
  work breaks into. An epic takes stories; a story takes tasks.
- `./scripts/hatch.sh depends <waits> <waited on>` for each pair that must land
  in order. Filing them is not saying so: the loop takes siblings in whatever
  order the board puts them in, so five stories that are a sequence are a chain
  of four edges, and five that are independent get none and go in parallel over
  several nights.
- Move it out of the drafting column: `POST /api/hatch/issues/AER-12/move` with
  the `statusId` of the column that holds specified work awaiting selection
  (**Backlog** on a stock board). `GET /api/hatch/board` names the columns.

## The two dates

Both go on the same `PATCH` as the description, and either may be a date
(`2027-08-15`) or an instant (`2027-09-01T17:00:00Z`); `""` clears one.

- `readyAt` — the day the work *can* start. An issue whose ready date has not
  arrived is folded off the board, so anything waiting on a soak test, a renewal
  window or a date on a calendar is filed now and surfaces on its own. Prefer
  this to a note in a description saying "not until March", which is a note
  nobody will see in March.
- `dueAt` — the day it is *owed*, drawn on the card and warming from three days
  out. A past date is accepted without comment.

## Finding tickets, and editing a lot of them at once

```
GET  ${HATCH_BASE}/api/hatch/issues?ancestorKey=AER-12&type=task&statusId=2
POST ${HATCH_BASE}/api/hatch/issues/bulk
```

The filters are `projectId`, `type`, `statusId`, `parentKey`, `ancestorKey` and
`text`; they combine with AND and every one is optional. Two are worth knowing:
`ancestorKey` returns everything below an issue at any depth — an epic's stories
and their tasks in one request — and `parentKey=` (empty) finds the issues with
no parent at all.

The bulk endpoint takes `keys` and any of `type`, `statusId`, `parentKey`,
`readyAt`, `dueAt`, with the same rules a single `PATCH` follows: a field left
out is left alone, and `""` clears one. It answers with `changed`, `unchanged`
and `failures` — a key that refuses the edit is reported with its reason and
left exactly as it was, while the rest of the batch goes through. Re-applying
the same edit writes nothing, so it is safe to run twice.

Prefer it to a loop of `PATCH`es when moving a whole epic's worth of work: one
request, one audit timestamp, and one place to read what did not apply.

## Where a project stands

```
GET  ${HATCH_BASE}/api/hatch/plan             # every epic, and what it adds up to
GET  ${HATCH_BASE}/api/hatch/plan/AER-12      # one issue, and each of its children
```

Both count **leaves** — issues with no children — so a subtree's total is the
histogram of its leaf descendants by status, a parent's total is exactly the sum
of its children's, and a parent's own column never lands in its own total: a
story in In Review whose tasks are all in To Do reads as To Do, because the
tasks are the work. Ready dates are not consulted, because a card folded off the
board is still work.

Every total is the same shape: `leaves`, `done` (the leaves in a terminal
column), `waiting` (open questions on the issue and everything below it), and
`slices`, one `{ statusId, count }` per column in board order with the empty
ones left out.

`/api/hatch/plan` answers with `epics` — the ones with no parent, each carrying
the epics beneath it, so the tree is drawn once — and `loose`, the total of
everything hanging under no epic at all; `?projectId=` narrows both halves.
`/api/hatch/plan/{key}` answers with the issue's own total and its direct
children in rank order, each with its own; a child's `isLeaf` says whether it
has work beneath it or is the work.

Read the plan when the question is *which* project to further; then
`work --under AER-12` takes the next thing inside the one you picked.
[The level above the board](hatch.md#the-level-above-the-board) is where the
arithmetic is argued from.
