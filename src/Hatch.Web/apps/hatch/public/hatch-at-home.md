# Hatch at home

**The two sentences to send a friend:** *Clone this repository, install Docker
Desktop, then run `docker compose up -d` from the repository root and open
http://localhost:8080/apps/hatch/. That is a project board of your own, with a
page inside it that hands you the runner that works your tickets for you.*

Everything below is the long version of those two sentences — what to have
first, the one line, where the runner comes from, what the loop does to a
checkout, and the block to paste into your own repository's `CLAUDE.md` so your
agents know how to reach the board. It is written for a person and for a
Claude, in that order.

**Quick links.** The `hatch` CLI — the runner that works your tickets — is
handed out by your own Hatch, from the **Runner** page in the nav:

- [**Runner → download `hatch`**](/apps/hatch/runner) · or
  `<your-hatch-origin>/apps/hatch/runner` in a browser
- [What to do with it once it is on your `PATH`](#the-hatch-cli-command-by-command)
- [`hatch work` — one increment](#one-increment-hatch-work) ·
  [`hatch go-to-work` — the loop](#the-loop-hatch-go-to-work)

## Prerequisites

- **A checkout of this repository**, and **Docker Desktop**, running. `docker
  compose` builds the board's own image from source, so there is nothing else
  to install to bring it up.
- **On Windows, either PowerShell** — 5.1, the blue one that ships with the OS,
  or 7 (`pwsh`). Every line on this page runs in both, with two exceptions that
  say so where they appear: the restore line, and the backup line beside it,
  which needs 7 because 5.1 writes UTF-16 through `>` and `psql` will not read
  that back.
- **git**, and the **`claude` CLI, logged in**. Neither is needed to run the
  board; both are needed by the runner, which branches, commits and pushes from
  a checkout and spends each increment inside a `claude` session. The Runner
  page inside Hatch links all three.

## The one line

From the repository root:

```
docker compose up -d
```

Then open **http://localhost:8080/apps/hatch/**. Compose says nothing about the
address once it has gone to the background, so that part is on this page rather
than in your terminal.

The same line in both PowerShells and in a macOS terminal, character for
character.

The first run takes a few minutes — it builds the image, runs the migration to
completion, and only then starts the API.

Port 8080 is only the default. If something on your machine already has it,
`HATCH_PORT` moves it — and that is one of the two places on this page
where the two shells differ (the other is the backup and restore pair, further
down):

macOS / Linux:

```
HATCH_PORT=9090 docker compose up -d
```

PowerShell:

```
$env:HATCH_PORT=9090; docker compose up -d
```

Wherever this page writes `<your-hatch-origin>` below, it means whatever that
address turned out to be — `http://localhost:8080` unless you moved it.

## Opening the board and setting your name

Open that address. That is the board, and it is empty in a particular way: the
columns are there — Draft through Done, the ones every Hatch ships with — and
there is no project yet, so there is nothing for an issue key to be made of.

**Projects → New project** is therefore the first thing to do. A project is a
key namespace rather than a container: give it a short key and every issue
filed under it is numbered from that key onwards (`HOME` gives you `HOME-1`,
`HOME-2`). One board holds every project you make, because switching boards to
find out what is next is the thing a folder of plan files already did badly.

There is no sign-in. A Hatch started this way runs with its wall off, which
means it does not ask who you are and does not have anywhere to look it up — so
it signs what you do with whatever your shell already called you (`USER` on
macOS and Linux, `USERNAME` on Windows), and calls you `friend` when neither is
set. The nav strip says which it used.

**Settings → Your name** changes it, and is worth doing before you file
anything. Every
comment, every move and every event on this board is signed with that name
forever afterwards, and a board where half the trail says `friend` is a board
that cannot answer "who did this." The field appears only on an install with
its wall off, which is every install that started from the line above.

## Getting the runner and pointing it at the board

The board is a board; the **runner** is what works the tickets. It is one
binary, it runs on your machine, and it talks to this Hatch over HTTP.

**Runner** in the nav is where it comes from. That page detects your platform
and offers the matching download — with the other three underneath, for the
machine you are not sitting at — and prints the revision it was built from,
which is the same commit as the image serving the page. The two are never a
version apart, because they came out of one build.

Put the file on your `PATH` as `hatch` (`hatch.exe` on Windows). On macOS and
Linux it needs marking executable first:

```
chmod +x hatch
```

Then the two commands the Runner page shows, with your own address already
filled into the first:

```
hatch config --origin <your-hatch-origin>
hatch go-to-work
```

`hatch config` writes the origin to a per-user file — mode 600, under the
platform's application-data directory, outside every repository — so it follows
you between checkouts and you do it once. A Hatch with its wall off needs no
key; `hatch config` asks for one anyway, and against this stack you can leave
it blank.

Run `go-to-work` from inside a checkout of the repository the board is about.

[The `hatch` CLI, command by command](#the-hatch-cli-command-by-command) is
the rest of what it takes — one increment, the loop, the bounds that stop it,
and the reads that spend nothing. The Runner page carries the same reference,
beneath the download.

## Running the runner as a container instead

The section above is the primary path, and this one is the shortcut. Rather
than putting a binary on your `PATH` and keeping a terminal open, the same
stack can start a **container** that carries the runner, `git` and the `claude`
CLI, mounts one of your checkouts, and works tickets under the control of the
Runners page like any other runner.

It is off unless you ask for it. Nothing about the stack changes if you never
type the word `runner`.

**What it can and cannot do, first, because it decides whether this section is
for you.** The container carries `git` and the `claude` CLI and no language
runtimes at all — no .NET, no Node, no Python, no compilers. So it can plan,
break work down, analyse a repository and write code in any repository at all,
and it can commit and push what it wrote. It *cannot build or test* a
repository whose toolchain it does not have, and no general image has
everybody's. For a repository where "done" means a green build — which is most
of them — the base-OS runner above is the one to use, because it works with
whatever you already have installed. This one is for the planning, breaking
down and analysing that make up a good part of a board's work, and for
repositories whose tooling is `git` and a text editor.

### Starting it

Two things it has to be told: which checkout to work in, and whose name goes on
the commits it makes.

macOS / Linux:

```
HATCH_CHECKOUT=/path/to/your/checkout \
HATCH_GIT_NAME="Your Name" \
HATCH_GIT_EMAIL=you@example.org \
docker compose --profile runner up -d
```

PowerShell:

```
$env:HATCH_CHECKOUT="C:\path\to\your\checkout"
$env:HATCH_GIT_NAME="Your Name"
$env:HATCH_GIT_EMAIL="you@example.org"
docker compose --profile runner up -d
```

It appears on the **Runners** page within a minute, called `hatch-runner`
(`HATCH_RUNNER_NAME` calls it something else), and the controls there — pause,
stop after this one, a spend cap, an hour to stop at — work on it exactly as
they do on a runner you started in a terminal. Stopping it from that page stops
the container too, rather than Docker restarting it behind your back.

`docker compose ... logs -f runner` is what it is saying while it works.

**Its Claude credential comes from the Settings page**, and from nowhere else.
Paste a token from `claude setup-token` into **Settings → Claude subscription
token** and the container picks it up on its next look. If none is saved when
it starts, it says so once and then waits, looking again every minute — so the
order you do these two things in does not matter.

**One container works one checkout.** For a second repository, copy the
`runner:` block in the compose file under a second service name with its own
`HATCH_RUNNER_NAME` and its own checkout mounted, and both appear on the
Runners page as themselves.

### Letting it push

The container starts with no credential of its own, so pushing needs one of
these three. All three are the ones you already have — none of them is a new
account or a new token unless you want one.

**A token in the environment.** The simplest, and the only one that needs no
edit to the compose file. `HATCH_GIT_TOKEN` is handed to `git` when a push over
HTTPS asks for a password:

macOS / Linux:

```
HATCH_GIT_TOKEN=ghp_... HATCH_CHECKOUT=... docker compose --profile runner up -d
```

PowerShell:

```
$env:HATCH_GIT_TOKEN="ghp_..."
docker compose --profile runner up -d
```

**The credential helper you already have.** If `git push` works from your own
terminal over HTTPS without asking, something is already holding that
credential, and it can be mounted read-only. Edit the checked-out
`compose.yaml` and uncomment these two lines under the runner's `volumes:`:

```
      - "${HOME}/.gitconfig:/root/.gitconfig:ro"
      - "${HOME}/.git-credentials:/root/.git-credentials:ro"
```

On Windows the same two files live under `%USERPROFILE%`, and `${HOME}` there
is `${USERPROFILE}`:

```
      - "${USERPROFILE}/.gitconfig:/root/.gitconfig:ro"
      - "${USERPROFILE}/.git-credentials:/root/.git-credentials:ro"
```

This shape only carries what a *file* holds. A helper that keeps the
credential somewhere else — macOS's Keychain (`credential.helper = osxkeychain`),
Windows' Credential Manager (`manager`) — has nothing in `.git-credentials` to
mount, and there a token in the environment is the answer.

**An SSH key.** If your remote is `git@…` rather than `https://…`, the
container needs a key. On macOS, Docker Desktop bridges your own running
`ssh-agent` into a container at a fixed path, so no key ever leaves the host —
uncomment under `volumes:`, and add the matching line under `environment:`:

```
      - "/run/host-services/ssh-auth.sock:/ssh-agent"
```

```
      SSH_AUTH_SOCK: /ssh-agent
```

On Windows that bridge is not available to a Linux container, so the key itself
is mounted instead — read-only, and pointed at with `GIT_SSH_COMMAND`:

```
      - "${USERPROFILE}/.ssh/id_ed25519:/root/.ssh/id_ed25519:ro"
```

```
      GIT_SSH_COMMAND: "ssh -i /root/.ssh/id_ed25519 -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new"
```

The same two lines work on macOS and Linux for a key that has no agent holding
it, with `${HOME}` in place of `${USERPROFILE}`.

## The `CLAUDE.md` block

The runner spawns a `claude` session per increment, inside your checkout. What
that session knows about the board is whatever your repository's `CLAUDE.md`
says — so this is the block to paste into it. It is the workflow contract:
which calls to make, what an agent may do to a ticket, what only you may do,
and when to stop and ask rather than guess.

Paste it as its own section. The commands in it are the runner's own, so they
work from any directory once `hatch config` has run; nothing in it names a path
in your repository, and nothing in it needs editing to fit one.

<!-- claude-contract:start -->
````markdown
## The operator and Claude contract

These rules come in two halves, because this file is loaded on every increment
and most increments only write code. This half carries what every session
needs; `hatch-planning.md` — on your Hatch's own Docs page — carries the shapes
only a session that files or reshapes work does: `parentKey`, the two dates,
the bulk endpoint and the plan reads. Read it on demand.

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

Its settings are read in three layers, highest first: an exported `HATCH_BASE`
or `HATCH_KEY`, then `scripts/.env` in the checkout you happen to be
standing in, then the per-user file `hatch config` writes — mode 600, under the
platform's application-data directory, outside every repository. An operator
configures once and every checkout on that machine is reached; a repository that
wants to pin its own origin still can. **The key lives outside the artifact** —
never a tracked file, never a value in a commit, never pasted into an issue.
That is not ordinary secret hygiene: Hatch ships to other operators, and a
credential in the artifact is one operator's credential inherited by everyone
who clones it (`ethos.md`, on your Hatch's own Docs page).

The key is optional. Against a Hatch started with its wall off
(`auth-architecture.md` on your Hatch's own Docs page, "Local mode") there is no
credential to present, and every call names itself with an `X-Hatch-Runner`
header instead — which is a name and not a proof, and only that Hatch reads one.
So a `401` is two different sentences, and says which happened: a key that was
sent and refused is a key to go and look at, and no key at all is a Hatch with
its wall on and nothing to look at yet.

`hatch work` reads `work/next`, then spawns a headless session with the
playbook's prompt, model and effort. It prints the session id first and last
with the `claude --resume` command beside it, and streams what the run is doing
as it happens — every tool call, a thinking-token pulse, and a heartbeat naming
what it is still waiting on. That last part is not a nicety: the CLI's default
output prints nothing until the run ends, so a four-minute increment was four
minutes of blank terminal indistinguishable from a hang, and the fix for "is it
working" is showing the work, not a spinner.

`hatch go-to-work` is `work` in a circle, and is
`hatch.md`'s "The unattended loop" on your Hatch's own
Docs page — what it may pick up, what it does
about a ticket that did not move, and what it stops for.

`hatch queue` reads the scan and prints it, one issue a line — key, type,
column, and either the reason the pass would fold past it or the transition it
is clear for, in the dispatcher's order: every expedited row first whatever
column it sits in, then the rest, and inside each half the rightmost column
first and the order the board itself draws that column in. A row marked `!` is
one somebody expedited. `hatch queue AER-1`
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
````
<!-- claude-contract:end -->

Two things in it point at documentation rather than restating it —
`hatch-planning.md` and `auth-architecture.md` — and both are on your Hatch's
own **Docs** page, in the nav beside this one. Every Hatch image carries the
whole of this documentation, so the sessions your board dispatches can read it
without leaving the machine.

## The `hatch` CLI, command by command

Everything below runs against the board you pointed it at with `hatch config`.
Only `work` and `go-to-work` need to be run inside a git checkout — the rest are
one request and a sentence about the answer, so `hatch board` from anywhere is
the ordinary case.

```
hatch --help                 every command, from the program itself
hatch <command> -h           ...and what one of them takes
```

### Pointing it at a board

```
hatch config                              ask for the origin and the key, and write them
hatch config --origin <your-hatch-origin> ...without being asked for the origin
hatch config --show                       what is set, and which layer it came from
```

Written to a per-user file — mode 600, under the platform's application-data
directory, outside every repository — so it follows you between checkouts and
you do it once. A Hatch with its wall off needs no key; leave it blank.

### One increment: `hatch work`

Asks the board for a ticket, claims it, spawns one headless `claude` session
with the prompt, model and effort that ticket's column and type call for,
streams what the session does, and exits when the session ends. One ticket, one
process.

```
hatch work                        the next actionable issue anywhere on the board
hatch work AER-12                 ...or this one, whatever else is above it
hatch work --under AER-1          ...or the next one under that epic's subtree
```

A key and `--under` together are refused: one names the ticket, the other names
where to look for it.

```
hatch work --dry-run              print the prompt and exit - claims nothing, spawns nothing
hatch work -i AER-12              a session you sit in, rather than a headless one
hatch work --quiet                say nothing until it is finished
hatch work --model opus --effort xhigh AER-12
```

`--model` and `--effort` beat the playbook for this run only. They are one
opinion about one ticket, which is why `go-to-work` has neither.

What it prints, and why: the session id first and last with the
`claude --resume` line beside it, then every tool call, a thinking-token pulse,
and a heartbeat naming what it is still waiting on. The `claude` CLI's own
default output prints nothing until the run ends, and four minutes of blank
terminal is indistinguishable from a hang.

It also beats once against the Runners page, so an increment run by hand shows
up beside the loops rather than being a session nobody can see.

Exit codes:

```
0    an increment ran - whatever the session itself exited with
1    a refusal: no claude CLI, an unreadable board, a flag it does not take
2    nothing to do: the board is idle, the ticket is blocked, or somebody else has it
130  interrupted
```

### The loop: `hatch go-to-work`

`work` in a circle: next actionable issue, one increment, ask again — until the
board has nothing an agent may move, and then it waits and asks again every
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

A ticket key is refused here: this command's question is "what is next", asked
again and again, and one ticket cannot be the answer to it twice. One increment
on a named ticket is `hatch work AER-12`.

#### One pass, in order

1. **Heartbeat.** Says this runner is here, and reads back what the Runners
   page would like it to do — paused, stopping, a different scope, a different
   cap. Read at the top of a pass, which is the one moment no claim is held.
2. **Stop conditions**, below.
3. **Pick.** The first issue the dispatcher clears, folding past everything it
   does not, and takes a claim on it. No claim, no spawn — that is what keeps
   two runners off one ticket.
4. **Reset the checkout.** Fetch, and put the tree back on the default branch
   at the tip the remote has right now.
5. **Check its own source.** If `go-to-work` was rebuilt on the trunk under it,
   it stops here and asks to come back as the new build, holding no ticket.
6. **Spawn the increment**, and record what it cost and whether the ticket
   moved.

An increment that ran is followed by the next one immediately; the interval is
only what to do when there was nothing to do.

**A ticket that did not move is a stall.** The loop comments on it saying so and
opens a question against it, and nothing further is dispatched at that ticket
until somebody answers. One bad ticket costs one increment instead of a night.

#### Bounds, timeouts and stopping

None of these are set by default. An unattended run that stopped for a reason
nobody asked for is a run somebody has to go and check on.

```
hatch go-to-work --max-runs 5             stop after five increments
hatch go-to-work --max-spend 20           stop once the night has cost $20
hatch go-to-work --until 08:00            stop at that wall-clock hour (tomorrow, if it has gone by today)
hatch go-to-work --stop-file /tmp/stop    stop once that path exists
hatch go-to-work --max-runs 5 --max-spend 20 --until 08:00
```

They are checked between increments and through every wait, so the increment in
flight always finishes, is committed and is pushed. `--stop-file` is the one to
reach for from another terminal or another machine — stopping a loop then needs
nothing but a shell and a path, no pid to find and no signal that could land
mid-push:

```
touch /tmp/stop                           # the loop ends after the increment in flight
```

The same three bounds are controls on the **Runners** page, and a value set
there is folded in on the next heartbeat — including being cleared. Pause,
resume and "stop after this one" live there too.

**Ctrl-C** lets go of the claim on the way out and exits 130. A second one is
immediate, and leaves the ticket claimed until the lease ages out.

And four things end a run without a bound having been reached:

```
three increments in a row failed        whatever is broken is broken for every ticket
the workspace could not be reset        every ticket after it would be built on the wrong tree
the board asked this runner to stop     the Runners page, mid-night
there is no claude CLI to spawn         nothing was ever going to run
```

It finishes by printing the night: what moved, what stalled, how many
increments and what they cost.

#### Restarts

A loop whose own source changed on the trunk asks to be restarted as the new
build, because a process cannot exec itself into one.

```
hatch go-to-work --restart-after 60   come back as a newer build at least that often (default 30)
hatch go-to-work --restart-after 0    ...only when its own source actually changed
hatch go-to-work --no-restart         ...never
```

**This needs something standing over the process**, because a process cannot
rebuild itself. A `hatch` you started by hand has nothing standing over it, so
it is the loop it started as and these three flags do nothing there. The
supervisor is Hatch's own `scripts/hatch.sh`, which catches the 75, compiles
the new source and runs it again. The container runner is a third case: it
carries the binary its image was built with, so `docker compose pull` is how it
becomes a newer one.

Exit codes:

```
0    the night ended, for one of the reasons above
1    a refusal: a flag it does not take, a stop file that already exists, a lock it could not take
75   asking the supervisor to rebuild and run it again
130  interrupted
```

#### One loop per checkout

A second `go-to-work` in the same tree is refused, naming the pid of the one
that has it. Two loops no longer collide over the board — the claim divides it —
but they would still collide over the tree, one increment's reset landing in the
middle of another's branch. Two checkouts, two loops, and both are welcome.

#### What it does to your checkout

Before every increment, so that a session's first act is cutting a branch and
the thing it cuts from is not in question:

- It fetches, and puts the checkout back on the default branch at the tip the
  remote has it at right now.
- **Uncommitted and untracked changes go into a stash**, named for the hour it
  was taken. Nothing is discarded — `git stash pop` is how you get it back —
  and ignored files are never touched, so a dependency directory or a local
  `.env` is safe.
- **Committed work is never at risk.** A checkout moves `HEAD`; the branch the
  last increment pushed is still under its own name.
- A branch whose upstream on origin has been deleted is deleted locally too,
  named on the terminal with the short sha it pointed at, so
  `git branch <name> <sha>` puts it back.

The practical consequence: work that matters is work that is committed. Do not
leave something half-finished in the tree and then start the loop in the same
checkout.

### Reading the board, spending nothing

```
hatch board                       the columns, and how many cards in each
hatch queue                       every card a pass would look at, in the order it looks
hatch queue AER-1                 ...under one epic
hatch next                        top workable card of "todo"
hatch next "in progress"          ...or of any column
hatch show AER-12                 the brief, plus its comments
hatch questions                   everything waiting on an answer
hatch questions AER-12            ...or just this ticket's
```

`hatch queue` is the dry run for the loop, and the answer to "why did it not
pick up the ticket I meant": each line is a key, a type, a column, and either
the reason the pass would fold past it or the transition it is clear for. It
spawns nothing and writes nothing. An empty board says so in a sentence rather
than printing a blank line — "there is nothing" and "something went wrong and
printed nothing" look identical otherwise.

Columns are found by name rather than by id, on the letters and digits alone,
so `todo` reaches the column the board calls `To Do`.

### Driving a ticket by hand

```
hatch start AER-12                move it to "in progress"
hatch move AER-12 todo            ...or to any non-terminal column
hatch comment AER-12 "sha abc123 on branch aer-12-thing"
hatch pr AER-12                   where it is being reviewed
hatch pr AER-12 https://...       ...or say where, having opened one
hatch pr AER-12 --clear           ...or take it off the one it has
hatch depends AER-13 AER-12       AER-13 waits on AER-12
hatch depends AER-13 --remove AER-12        ...no longer
hatch answer                      answer the open questions, one at a time, here
hatch api GET /api/hatch/issues?statusId=2
hatch api PATCH /api/hatch/issues/AER-12 '{"dueAt":"2026-10-01"}'
```

`hatch answer` walks the open questions serially on purpose: six printed at once
get answered in aggregate, which is how a wrong assumption gets in.

### Settings

Read in three layers, highest first: an exported variable, then `scripts/.env`
in the checkout you are standing in, then the per-user file `hatch config`
writes.

```
HATCH_BASE         the origin of your Hatch
HATCH_KEY          hatch_ak_... Optional against a Hatch with its wall off
HATCH_CLAUDE_BIN   the claude CLI, if it is not on PATH
HATCH_BASE_BRANCH  the trunk go-to-work resets to between increments
HATCH_RUNNER       what the board calls this runner (default host:/path)
HATCH_ROOT         the checkout to work in (default: upwards from here)
HATCH_HEARTBEAT    seconds of silence before the renderer says what it is waiting on
```

### Where the prompt comes from

The prompt, model and effort of every increment come from a **playbook** — a row
per (column transition, issue type). The board ships with a working set, and the
Playbooks page is where you change what your sessions are told. They are yours
to edit and not your agents': the API refuses the write from a key, on purpose,
because an agent that could widen its own instructions and its own budget is a
loop with no end.

## Upgrading

```
git pull
docker compose up -d --build
```

`git pull` brings the source up to date; `--build` is what makes `up -d`
rebuild the image from it rather than reusing the one you started with. **Your
data is untouched** — it is in a volume, and a volume is not part of an image.
The migration re-runs on every start and does nothing when there is nothing to
do.

Download the runner again from the Runner page after an upgrade. The page
prints the revision it was built from, which is how you can tell whether the
one on your `PATH` is the one this board expects.

If you run the container runner, add `--profile runner` to the line above:
Compose only rebuilds and only restarts the services the profile it was given
turns on, and without it the runner container would sit on the version you
started with.

## Where the data is, and how to back it up

Everything Hatch knows — every issue, comment, event and playbook — is in one
Postgres database called `hatch`, inside a named volume called
`hatch-local_pgdata`. Nothing lives in the images and nothing lives in a file
on your desktop, which is the whole reason `down` is safe and `down -v` is not.

**Do not back it up by copying the volume.** A file-level copy of a database
directory that is being written to has no consistency guarantee, so what you
would be keeping is a file that restores *sometimes*. Ask Postgres for the dump
instead — it is one command, the stack stays up, and what comes back is a text
file you can read.

```
docker compose exec -T db pg_dump -U user --clean --if-exists hatch > hatch-backup.sql
```

That is a board's worth of history in well under a megabyte. Put it wherever
you already put things you would be sad to lose.

Restoring is the same in reverse, with the API stopped for the length of it —
the dump drops every table before it recreates them, and an API holding
connections to those tables is an API answering a page out of a schema that is
being replaced underneath it:

```
docker compose stop api
docker compose exec -T db psql -U user -d hatch -v ON_ERROR_STOP=1 < hatch-backup.sql
docker compose start api
```

`ON_ERROR_STOP=1` is what turns a half-restore into a refusal. Without it
`psql` reports each failure and carries on to the next statement, and you find
out which half arrived by using the board.

**In PowerShell, the restore line is different**, because PowerShell has no
`<` input redirection at all:

```
Get-Content hatch-backup.sql | docker compose exec -T db psql -U user -d hatch -v ON_ERROR_STOP=1
```

The backup line's `>` works in either shell, as long as the PowerShell is 7 or
newer — 5.1 writes UTF-16 there, which `psql` will not read back.

## How to stop

```
docker compose down
```

**`down` keeps your data.** The containers go; the volume outlives them, so
`down` and then `up -d` again — even after pulling newer images — brings back
the same board. `down` takes the runner container with it whether or not you
name its profile, because it removes everything in the project.

```
docker compose down -v
```

**`down -v` throws your data away.** The `-v` removes the volume, and there is
no undo. Use it when you want to start over from an empty board, and not
otherwise.

## If something is wrong

**`the system cannot find the file specified`, naming a pipe or a socket.** In
full it is `open //./pipe/dockerDesktopLinuxEngine` on Windows, or
`/var/run/docker.sock` on macOS. Nothing about the stack failed: Docker Desktop
is installed and not running. Start it and wait for its dashboard to say
**Engine running** — a fresh install does not start itself, and the first start
brings WSL2 up with it and can take a couple of minutes. `docker version`
printing a **Server** block as well as a **Client** one is how you know it is
ready. If the installer asked for a sign-out or a reboot and did not get one,
that is the other half of this: the group membership it added is not in effect
until then.

```
docker compose ps
docker compose logs api
docker compose logs migrate
```

`migrate` showing as exited is correct — that is a finished migration, not a
crash. `api` restarting in a loop usually means the migration did not finish,
and its log says why.
