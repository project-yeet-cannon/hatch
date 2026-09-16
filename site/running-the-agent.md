---
title: Running the agent
lede: The runner on your own machine. One increment, the loop, what it does to your checkout, and how to stop it.
permalink: /running-the-agent/
---

The board is a board; the **runner** is what works the tickets. It is one
binary called `hatch`, it runs on your machine, and it talks to your Hatch
over HTTP. Each increment it spawns one headless `claude` session inside your
checkout with the prompt, model and effort the ticket's column and type call
for, streams what that session does, and hands the ticket back.

This page is the working knowledge. The [Agent manual](agent-manual.md) is the
exhaustive reference for every flag and behaviour.

## What it needs

- **git** on the `PATH`. The loop fetches, resets and prunes with it, and the
  session branches, commits and pushes with it.
- **The `claude` CLI**, logged in, on the `PATH` or named in
  `HATCH_CLAUDE_BIN`. The runner refuses to take a ticket if it cannot find
  one, before claiming anything.
- **A git checkout** of the repository the board is about, with a remote
  called `origin`. Only `work` and `go-to-work` need one; every other command
  is one request and a sentence about the answer.

Nothing else. The binary is self-contained: no .NET, no Node, no `gh`, no
`jq`.

## Getting the binary

The **Runner** page in your board's nav offers the download for your platform
(`win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`) and prints the revision it
was built from, which is the same commit as the image serving the page. Put it
on your `PATH` as `hatch` (`hatch.exe` on Windows); `chmod +x hatch` on macOS
and Linux.

Inside a checkout of Hatch itself, `./scripts/hatch.sh` reaches the same
commands and builds the CLI on demand with the .NET SDK. `make build-hatch`
builds it once so every command after is fast, and `make publish-hatch`
produces the four self-contained binaries the Runner page serves.

## Pointing it at a board

```
hatch config --origin <your-hatch-origin>     the origin alone, asking nothing
hatch config                                  asks for origin, key and claude path
hatch config --show                           what is set, and which layer it came from
```

Settings are read in three layers, highest first:

1. An exported environment variable.
2. `scripts/.env` in the checkout you are standing in.
3. The per-user file `hatch config` writes: `~/.config/hatch/config` on
   macOS and Linux, `%APPDATA%\hatch\config` on Windows, mode 600, outside
   every repository.

So you configure once and every checkout on the machine is reached, and a
repository that wants to pin its own origin still can. Both files are parsed,
not sourced: `KEY=value` lines, and only these names:

| Variable | Meaning |
|---|---|
| `HATCH_BASE` | The origin of your Hatch |
| `HATCH_KEY` | `hatch_ak_…`, optional against a Hatch with its wall off |
| `HATCH_CLAUDE_BIN` | The `claude` CLI, if it is not on `PATH` |
| `HATCH_BASE_BRANCH` | The trunk `go-to-work` resets to, if `origin/HEAD` does not say |
| `HATCH_RUNNER` | What the board calls this runner. Default `host:/path/to/checkout` |

**The key lives outside the artifact**: never a tracked file, never a value in
a commit, never pasted into a ticket. Hatch is built to be cloned by other
operators, and a credential in the repository is one operator's credential
inherited by everyone who clones it.

**With no key** the runner names itself with an `X-Hatch-Runner` header, which
is a name and not a proof, and only a Hatch started with its wall off reads
one. That is every Hatch started with `docker compose up -d`. So a `401` says
one of two things, and says which: a key that was sent and refused, or no key
against a Hatch that wants one. A `403` means the key is working and the route
is not one a key may take.

## One increment: `hatch work`

```
hatch work                        the next actionable issue anywhere on the board
hatch work AER-12                 ...or this one, whatever else is above it
hatch work --under AER-1          ...or the next one under that epic
hatch work --dry-run              print the prompt and exit: claims nothing, spawns nothing
hatch work -i AER-12              a session you sit in, rather than a headless one
hatch work --quiet                say nothing until it is finished
hatch work --model opus --effort xhigh AER-12
```

What it does, in order: checks that the `claude` CLI can be found; sends one
heartbeat so a hand-run increment shows on the Runners page; takes a claim on
the ticket, so no other runner is sent at it; spawns the session with the
prompt on stdin; posts the work-log row; reads back where the ticket ended
up; prints any question the session asked; and releases the claim, every way
out including Ctrl-C.

Two things worth knowing that the loop does differently:

- **`work` does not touch git.** It runs against the tree exactly as it finds
  it. Only `go-to-work` fetches, stashes and resets before an increment.
- **`--model` and `--effort` beat the playbook for this run only.** They are
  one opinion about one ticket, which is why `go-to-work` has neither.

What it prints: the session id first and last with the `claude --resume`
command beside it, every tool call as it happens, a thinking-token pulse, and
a heartbeat naming what it is still waiting on. The `claude` CLI's own default
output prints nothing until the run ends, and four minutes of blank terminal
is indistinguishable from a hang.

Exit codes:

```
0    an increment ran, whatever the session itself exited with
1    a refusal: no claude CLI, an unreadable board, a flag it does not take
2    nothing to do: the board is idle, the ticket is blocked, or somebody else has it
130  interrupted
```

## The loop: `hatch go-to-work`

`work` in a circle: next actionable issue, one increment, ask again, until the
board has nothing an agent may move, and then wait and ask again every
interval. A process per increment rather than one long session, because a
session that ran all night would carry six hours of context into its last
ticket, and the earliest decisions in that context are the ones nobody can
audit afterwards.

```
hatch go-to-work                  until told to stop
hatch go-to-work --once           one pass, and out
hatch go-to-work --under AER-1    only inside that epic's subtree
hatch go-to-work --interval 300   seconds to wait when there was nothing to do (default 60)
hatch go-to-work --quiet          no per-increment stream, only what each one ended as
```

A ticket key is refused here. This command's question is "what is next", and
one ticket cannot be the answer twice. One increment on a named ticket is
`hatch work AER-12`.

### One pass, in order

1. **Heartbeat.** Says this runner is here, and reads back what the Runners
   page would like it to do: paused, stopping, a different scope, a different
   cap. Read at the top of a pass, the one moment no claim is held.
2. **Stop conditions**, below.
3. **Pick.** The first issue the dispatcher clears, folding past everything it
   does not, and a claim on it. No claim, no spawn.
4. **Reset the checkout.** Fetch, and put the tree back on the default branch
   at the tip the remote has right now.
5. **Check its own source.** If the loop was rebuilt on the trunk under it, it
   stops here and asks to come back as the new build, holding no ticket.
6. **Spawn the increment**, and record what it cost and whether the ticket
   moved.

An increment that ran is followed by the next one immediately. The interval is
only what to do when there was nothing to do. While idle it prints the reasons
the board is folded, grouped and counted, once, then again only when they
change, with a one-line "still nothing" every ten minutes.

### What it does to your checkout

Before every increment, so a session's first act is cutting a branch and the
thing it cuts from is not in question:

- It fetches with prune, and puts the checkout on the default branch at the
  tip `origin` has it at. The trunk is whatever `origin/HEAD` names, or
  `HATCH_BASE_BRANCH`.
- **Uncommitted and untracked changes go into a stash**, named for the hour it
  was taken. Nothing is discarded, `git stash pop` is how you get it back, and
  ignored files are never touched, so a dependency directory or a local `.env`
  is safe.
- **Committed work is never at risk.** A checkout moves `HEAD`; the branch the
  last increment pushed is still under its own name. If the local trunk was
  ahead of origin, the loop says so and `git reflog` has the commits.
- A local branch whose upstream on origin has been deleted is deleted too,
  named on the terminal with the short sha it pointed at, so
  `git branch <name> <sha>` puts it back.

The practical consequence: work that matters is work that is committed. Do
not leave something half-finished in the tree and then start the loop in the
same checkout.

### Stalls

A ticket that did not move is a **stall**: the session ended with the ticket
in the column it found it in. The loop comments on it, with the
`claude --resume` command for that session, and opens a question against it
with two options, *leave it* and *try again*, neither recommended. Nothing
further is dispatched there until somebody answers. One bad ticket costs one
increment instead of a night.

### Bounds, timeouts and stopping

None are set by default. An unattended run that stopped for a reason nobody
asked for is a run somebody has to go and check on.

```
hatch go-to-work --max-runs 5             stop after five increments
hatch go-to-work --max-spend 20           stop once the night has cost $20
hatch go-to-work --until 08:00            stop at that wall-clock hour (tomorrow, if it has gone by today)
hatch go-to-work --stop-file /tmp/stop    stop once that path exists
```

They are checked between increments and every five seconds through a wait,
so the increment in flight always finishes, is committed and is pushed.
`--stop-file` is the one to reach for from another terminal or another
machine: `touch /tmp/stop` ends the loop after the increment in flight, with
no pid to find and no signal that could land mid-push. A stop file that
already exists when the loop starts is refused, because otherwise an empty
board and a stale stop file would look identical.

The same bounds are controls on the **Runners** page, and a value set there
is folded in on the next heartbeat, including being cleared. Pause, resume and
"stop after this one" live there too. Nothing on the server starts or stops a
process; the loop picks each of these up itself, between increments, which is
what makes them work for a runner behind a router nothing can reach. The cost
of that: a press takes effect at the top of the next pass, after whatever
increment is in flight has finished.

**Ctrl-C** lets go of the claim on the way out and exits 130. A second one is
immediate, and leaves the ticket claimed until the lease ages out, five
minutes by default.

Four things end a run without a bound having been reached:

```
three increments in a row failed        whatever is broken is broken for every ticket
the workspace could not be reset        every ticket after it would be built on the wrong tree
the board asked this runner to stop     the Runners page, mid-night
there is no claude CLI to spawn         nothing was ever going to run
```

It finishes by printing the night: why it stopped, how many increments and
what they cost, what moved, and what stalled.

### Restarts

A loop whose own source changed on the trunk asks to be restarted as the new
build, because a process cannot exec itself into one. It exits 75 and
something standing over it has to rebuild and run it again.

```
hatch go-to-work --restart-after 60   come back as a newer build at least that often (default 30)
hatch go-to-work --restart-after 0    ...only when its own source actually changed
hatch go-to-work --no-restart         ...never
```

**A `hatch` you started by hand has nothing standing over it**, so it is the
loop it started as and these flags do nothing. This only matters when the
board is about Hatch's own repository: there, `./scripts/hatch.sh go-to-work`
is the supervisor. It catches the 75, runs `make build-hatch`, and starts the
loop again with the night's totals carried forward, so a restart cannot
outspend `--max-spend`. The container runner is a third case: it carries the
binary its image was built with, and a rebuild of the image is how it becomes
a newer one.

### One loop per checkout

A second `go-to-work` in the same tree is refused, naming the pid of the one
that has it. Two loops do not collide over the board, the claim divides it,
but they would collide over the tree. Two checkouts, two loops, and both are
welcome. They appear on the Runners page under their own names.

## Reading the board, spending nothing

```
hatch board                       the columns, and how many cards in each
hatch queue                       every card a pass would look at, in the order it looks
hatch queue AER-1                 ...under one epic
hatch next                        top workable card of "todo"
hatch next "in progress"          ...or of any column
hatch show AER-12                 the brief, its edges, and its comments
hatch questions                   everything waiting on an answer
hatch questions AER-12            ...or just this ticket's
```

`hatch queue` is the dry run for the loop and the answer to "why did it not
pick up the ticket I meant". A row marked `!` is expedited. Columns are found
by name on the letters and digits alone, so `todo` reaches `To Do`.

## Driving a ticket by hand

```
hatch start AER-12                move it to "in progress"
hatch move AER-12 todo            ...or to any non-terminal column
hatch comment AER-12 "sha abc123 on branch aer-12-thing"
hatch pr AER-12                   where it is being reviewed
hatch pr AER-12 https://...       ...or say where, having opened one
hatch pr AER-12 --clear           ...or take it off the one it has
hatch depends AER-13 AER-12       AER-13 waits on AER-12
hatch depends AER-13 --remove AER-12
hatch ask AER-12 "the question" --recommend "Label: why" --option "Label: why"
hatch answer                      answer the open questions, one at a time, here
hatch api GET /api/hatch/issues?statusId=2
hatch api PATCH /api/hatch/issues/AER-12 '{"dueAt":"2026-10-01"}'
```

`hatch answer` walks the open questions serially on purpose: six printed at
once get answered in aggregate, which is how a wrong assumption gets in. A
number takes that option and the ticket records the option's label as the
answer, not the number.

`move` refuses a terminal column and a deferred column. Only the operator
decides that something shipped or shelved, and the board enforces it on the
server as well.

## What the session is told

Every increment's prompt is assembled from the playbook row for that column
transition and issue type, then the ticket (key, type, title, the move it is
making, dates, description), its children, the questions already answered
under **Decisions already made**, and a fixed tail: how to reach Hatch from
inside the session, how to ask when it cannot decide, where the increment
ends, and the instruction to finish with a `work-log` block. `hatch work
--dry-run` prints the whole thing.

The session runs the `claude` CLI in print mode with permission prompts
bypassed, because nothing is there to answer one. The runner puts its own
directory at the front of the session's `PATH`, so `hatch` inside the session
is the same binary that spawned it, already pointed at the same board.

What the session knows about the board beyond that is your repository's
`CLAUDE.md`. Your Hatch ships the block to paste under **Docs** in the nav.

## The `work-log` row

When the session ends, the runner lifts the last fenced `work-log` block out
of what it said, a title line and a summary, and posts it with the session
id, the duration, the four token counts and the notional cost as one row on
the ticket's work log. That row is the only record anywhere that knows which
ticket the money went on, and the **Leaderboard** page adds them up.

## Troubleshooting

**`hatch: no claude CLI on PATH.`** Install it, or `export
HATCH_CLAUDE_BIN=/path/to/claude`. The runner looks on `PATH` only; it does
not find an editor extension's bundled copy.

**`hatch: 401 - no key was sent and this Hatch has its wall on.`** This
board wants a key. Mint one on its API keys page and run `hatch config`.

**`hatch: 403 - the key is good and this route is not one it may take.`**
A key is never an administrator. Playbooks, expedite, assignees, settings and
the Runners page controls are a person's to change, in the browser.

**`hatch: cannot tell which branch is the trunk here`.** `origin/HEAD` is
not set in this clone. `git remote set-head origin -a` fixes it, or name the
branch in `HATCH_BASE_BRANCH`.

**`hatch: the tree has changes in it that would not stash`.** A merge in
progress or a conflicted file. Clear it by hand; the loop will not guess.

**`hatch: a go-to-work is already running in <path>, pid <n>`.** One loop
per checkout. Join that one, stop it, or use another checkout. A lock left by
a dead process is cleared automatically.

**It picked up nothing.** `hatch queue` says why, per card. The common
reasons: no playbook leads out of that column for that type, an open
question, a person's name on it, a ready date not yet arrived, or another
runner's claim.
