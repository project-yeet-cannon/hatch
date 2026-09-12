# Working on Hatch

Notes for Claude. Read [`README.md`](README.md) for what Hatch is,
[`docs/ethos.md`](docs/ethos.md) for the one rule that constrains every commit
(*nothing in this repo may be true of exactly one installation*), and
[`docs/hatch.md`](docs/hatch.md) for the tracker every ticket below comes off.

## Hatch: the ticket is the unit of work

[Hatch](docs/hatch.md) is the house project tracker, at `hatch.${DOMAIN}`. It is
where work is described, and it is reachable programmatically — so a link to a
ticket is a complete instruction, and the ticket is where the answer goes back.

`hatch` is the calls a working session actually makes — `board`, `next`,
`queue`, `show`, `start`, `move`, `comment`, `pr`, `depends`, `ask`,
`questions`, `answer`, `config`, `work`, `go-to-work`, and `api` for everything
else. It finds a column by name rather than by id, on the letters and digits
alone, so `todo` reaches the column the board calls `To Do`. Prefer it to raw
`curl`; the raw calls below are what it is doing. `hatch --help` lists the
surface, and every subcommand takes `-h` for its own.

It is one program — [`src/Hatch.Cli`](src/Hatch.Cli), published as a single
binary for macOS, Windows and Linux — because an operator who clones Hatch has
no copy of this repository's scripts on their `PATH`. Only `work` and
`go-to-work` need to be run inside a git checkout.

**In this checkout, [`scripts/hatch.sh`](scripts/hatch.sh) reaches every one of
those commands**, finding or building the binary and handing over. Use it where
`hatch` is not installed; the two are the same commands and the same arguments,
and this file writes `./scripts/hatch.sh` below for exactly that reason.

Access is an API key — `Authorization: Bearer hatch_ak_…` — read from three
layers, highest first: an exported `HATCH_BASE`/`HATCH_KEY`, then
`scripts/.env` in this checkout (ignored by git), then the per-user file
`hatch config` writes. **The key lives outside the artifact**: never a tracked
file, never a value in a commit, never pasted into a plan or an issue. Hatch is
headed for release to other operators, and a credential in the artifact is one
operator's credential inherited by everyone who clones it. The key carries the
`hatch` scope and reaches `/api/hatch/*` and nothing else, so a `403` means the
key is working and the route is not one a key may take — ask the operator.

The key is optional against a Hatch running with its wall off, where calls name
themselves with a runner header instead. So a `401` says which of two things
happened: a key that was sent and refused, or no key against a Hatch that wants
one.

### If the loop spawned you

**This is most likely how you got here.** `go-to-work` runs increments back to
back — the next actionable issue, one increment, ask again — so assume nobody is
reading the terminal, and that the next increment starts the moment yours ends.

- **The tree is already the trunk, and it is current.** The loop fetches and
  resets the checkout onto the default branch before it spawns you, so branch
  straight from where you are and do not go looking for a base. Anything you
  leave uncommitted is stashed before the next increment starts — recoverable
  with `git stash pop`, but not where you left it — so work that matters is work
  that is committed and pushed. And a tree that cannot be reset ends the night:
  clear a half-finished merge or a conflicted file before you stop, rather than
  leaving it for whoever runs next.
- **Leave the ticket somewhere new.** An increment that ends with the ticket in
  the column it started in is a *stall*: the loop comments, opens a question
  against the issue, and moves on — and nothing further is dispatched there
  until a person answers it. That guard exists so a bad ticket costs one
  increment instead of a night, but a sentence you wrote about why you stopped
  is worth more than the one it writes for you.
- **Asking is a full stop, not a pause.** An open question blocks the ticket
  from being dispatched at all. Ask and stop — do not ask and keep building.
- **Record the pull request**: `./scripts/hatch.sh pr AER-12 <url>`. It is a
  field on the issue, not a URL somebody has to find in a comment.
- **Say what has to land in order.** The loop takes siblings in whatever order
  the board puts them in, so work that must land in order says so:

  ```
  ./scripts/hatch.sh depends AER-13 AER-12   # AER-13 waits on AER-12
  ```

  It gates one move — the one into the column where the code gets written — and
  clears only when the issue it names is merged. Everything left of that keeps
  moving: a blocked issue is still broken down, still lands in the backlog, and
  is still analysed.
- **Read the board before assuming it is empty.** `./scripts/hatch.sh queue`
  prints every issue a pass would look at, in the order it looks, each with the
  reason it would be folded past — or the transition it is clear for. It spawns
  nothing and writes nothing. A column and type nobody has written a playbook
  for reads as a finished board from outside and is not one.

What a session is told, which model it runs on and how much effort it spends are
a **playbook**: a row per (status transition, issue types), readable by a key and
writable only by a person — as is a model or effort pinned on a single ticket. If
a playbook is wrong, say so on the ticket. The API refuses to let you route
around it, and it refuses on purpose.

Work on the loop itself — `src/Hatch.Cli`, `scripts/hatch.sh`,
`src/Hatch.Contracts` — reaches the next increment rather than the next
night: when its own source changes on the trunk, the loop rebuilds and comes
back as the new version.

The five conditions that make an issue actionable live on the server, and
[`docs/hatch.md`](docs/hatch.md) has them with the reasoning behind each.

### When a decision is not yours to make

A product call, a name that will be lived with for years, a tradeoff with no
technically correct side: do not guess, and do not quietly take whichever branch
is cheapest to build. Ask on the ticket, and **name the choices**:

```
./scripts/hatch.sh ask AER-12 "How should drain retries be scoped?" \
    --recommend "Per-node: one budget each, so a slow node cannot starve the rest" \
    --option   "Global: one budget for the drain, simpler to reason about"
```

Each `--option` becomes something the operator presses, so the body is the
question alone and the tradeoffs go inside the options they belong to;
`--recommend` marks the one you would take, of which there may be one. A label
reads as a decision on its own — `per-node`, not `we should scope them per
node` — because the label becomes the answer's own text. Ask in prose
(`ask AER-12 "…"`, no options) only when the answer is genuinely open-ended: a
name, a description, a direction.

One call per question, so each can be answered on its own. Then **stop**.
Answers arrive in the next session's prompt under **Decisions already made**.
Those are settled: build on them, and do not reopen them.

What the repository can answer, answer by reading the repository.

### Given a ticket

A `hatch.${DOMAIN}/issues/AER-12` link, or a bare `AER-12`, means:

```
GET  ${HATCH_BASE}/api/hatch/issues/AER-12          # title, description, status, parent, children
GET  ${HATCH_BASE}/api/hatch/issues/AER-12/comments
GET  ${HATCH_BASE}/api/hatch/issues/AER-12/questions?open=false   # decisions asked for, and given
GET  ${HATCH_BASE}/api/hatch/issues/AER-12/events   # what has happened to it, newest first
```

Read it, then get to work. The description is markdown and is the brief.

### Filing work: see `docs/hatch-planning.md`

An increment that **files or reshapes work** — turning a draft into an epic with
stories under it, breaking a story into tasks, dating something that cannot
start yet, or moving a batch at once — reads
[`docs/hatch-planning.md`](docs/hatch-planning.md) first. It has the shapes:
`POST /api/hatch/issues` with a `parentKey`, the `readyAt` and `dueAt` dates,
the `issues` filters and the bulk endpoint, and the `/api/hatch/plan` reads that
say how far along a project is.

It is a separate file because an implementation increment never needs any of it,
and this one is loaded on every increment. Two rules from it are worth carrying
here, because getting them wrong is not recoverable by reading further:

- **An epic takes stories; a story takes tasks.** Plan on the ticket, not in a
  chat log — acceptance criteria go in the description, in a form somebody else
  could check.
- **Filing stories in order is not saying they are ordered.** Work that must
  land in sequence says so with `./scripts/hatch.sh depends`, one call per edge.

### Implementing a ticket

- Move it to **in progress** before starting, so the board says what is being
  worked on right now. That is the board's whole job.
- `POST /api/hatch/issues/AER-12/comments` with the commit sha and the branch —
  the ticket is where somebody looks in six months, and a comment naming a
  commit is what makes that search short.
- `./scripts/hatch.sh pr AER-12 <url>` if you opened a pull request. `pr AER-12`
  with no URL reads back the one that is set, and `--clear` takes it off.
- **Never move a ticket to a terminal or a deferred status.** Only the operator
  decides that something shipped, and only the operator decides that something
  is not worth doing now. Implementation ends in *in progress*, with a comment
  saying what landed and what did not; work that should be shelved is said on
  the ticket.
- **End by saying what you did**, in a fenced block:

  ~~~
  ```work-log
  A title naming what this session did

  The summary, under 100 words.
  ```
  ~~~

  `hatch` lifts that out of the last thing the session says and posts it,
  with what the increment cost, as one row of the **work log** on the ticket —
  the only record anywhere that knows *which* ticket the money went on. Write it
  last, and write it once: whichever block comes last wins.

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
