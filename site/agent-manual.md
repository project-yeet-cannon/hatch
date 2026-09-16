---
title: Agent manual
lede: The technical reference for the hatch runner. Every command, flag, environment variable, exit code and behaviour, as the source has it.
permalink: /agent-manual/
---

`hatch` is one self-contained binary, published for `win-x64`, `osx-arm64`,
`osx-x64` and `linux-x64`, built from `src/Hatch.Cli` in the repository. This
page is written from that source. Where the wording of a message matters it
is quoted as the program prints it.

## Invocation

```
hatch <command> [arguments]
hatch --help                  every command; exits 0
hatch                         the same text; exits 1
hatch <command> -h            the command's own usage
```

There are no global flags. Every command recognises `-h` or `--help` **as its
first argument only**, because four commands take prose bodies and a comment
reading `--help` is a body somebody meant to post. Usage asked for goes to
stdout and exits 0. A refusal prints `hatch: <what>`, a blank line and the
usage block, all on stderr, and exits 1.

A name that is not a command is refused before anything is loaded:

```
hatch: no such command "<x>" - `hatch --help` lists them.
```

### The seventeen commands

| Command | Needs a checkout | Writes to the board |
|---|---|---|
| `config` | no | no |
| `board` | no | no |
| `next` | no | no |
| `queue` | no | no |
| `show` | no | no |
| `start` | no | yes |
| `move` | no | yes |
| `comment` | no | yes |
| `pr` | no | read: no; set: yes |
| `depends` | no | read: no; add/remove: yes |
| `ask` | no | yes |
| `questions` | no | no |
| `answer` | no | yes |
| `api` | no | as the method says |
| `work` | **yes** | yes |
| `go-to-work` | **yes** | yes |
| `runner-claude-token` | no | no |

### Start-up, in order

1. Resolve the checkout: `HATCH_ROOT` if set and it exists, else the nearest
   ancestor of the working directory holding a `.git` file or directory. Only
   `work` and `go-to-work` require one:
   ```
   hatch: this is not a git repository, and a ticket is about a codebase.
   hatch:   run it inside a checkout, or name one in HATCH_ROOT.
   ```
2. `config` runs before settings are loaded, since it is the command that
   fixes a missing configuration.
3. Load settings from the three layers. A missing origin is a refusal, exit 1.
4. Derive the runner name.
5. Register SIGINT and SIGTERM. The first signal prints
   `hatch: stopping - letting go of the ticket first` and cancels, so every
   claim is released on the way out. A second signal is immediate.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | It worked, or usage was asked for. For `work`, an increment ran, whatever the session exited with. |
| `1` | A refusal: bad arguments, unknown command, not configured, not a repository, an HTTP fault, an unreadable board. |
| `2` | Nothing to do, as an answer rather than a fault: `next` with nothing workable; `pr` read with no URL set; `work` when the board is idle, the ticket is blocked, or another runner has it; `runner-claude-token` when no token is saved. |
| `75` | `go-to-work` asking its supervisor to rebuild and run it again. |
| `130` | Interrupted. |
| the session's | `work -i` returns the exit code of the attached `claude` process. |

### Error reporting

HTTP failures carry the server's own sentence where it wrote one. Five cases
are named exactly:

```
hatch: could not reach <origin> - <detail>
hatch: 401 - no key was sent and this Hatch has its wall on. Run `hatch config`.
hatch: 401 - the key was not accepted. Minted, not revoked, copied whole?
hatch: 403 - the key is good and this route is not one it may take (CLAUDE.md).
hatch: 404 - no such issue or route: <path>
```

Anything else is `hatch: <code> - <body>`. Normal output goes to stdout;
complaints go to stderr; both share one lock so the session's stream and the
runner's own lines never interleave.

## Settings

### The three layers

Highest precedence first:

1. **An exported environment variable.**
2. **`<checkout>/scripts/.env`**, the checkout the command was run from.
3. **The per-user file**: `~/.config/hatch/config` on macOS and Linux,
   `%APPDATA%\hatch\config` on Windows. Written by `hatch config` with mode
   600.

An empty value at one layer does not win; the next layer is tried. The files
are parsed rather than sourced: `KEY=value`, blank lines and `#` comments
skipped, one layer of matching quotes stripped. A name not on the allow-list
is skipped loudly:

```
hatch: ignoring "<NAME>" in <path> - not one of: HATCH_BASE HATCH_KEY HATCH_CLAUDE_BIN HATCH_BASE_BRANCH HATCH_RUNNER
```

### Every variable the binary reads

| Variable | Layers | Meaning |
|---|---|---|
| `HATCH_BASE` | all three | **Required.** The origin. A trailing slash is trimmed. There is no `HATCH_ORIGIN`; `--origin` is a flag of `hatch config`. |
| `HATCH_KEY` | all three | Optional. A `hatch_ak_…` bearer key. Empty means keyless mode. |
| `HATCH_CLAUDE_BIN` | all three | The `claude` executable, if it is not on `PATH`. |
| `HATCH_BASE_BRANCH` | all three | The trunk `go-to-work` resets to. Overrides `origin/HEAD` detection. |
| `HATCH_RUNNER` | all three | What the board calls this runner. Default `<short hostname>:<checkout path>`, clipped to 240 characters from the front. |
| `HATCH_HEARTBEAT` | environment only in practice | Seconds of session silence before the terminal prints a pulse. Default `20`; `0` turns it off; anything else invalid falls back to 20. Not on the file allow-list. |
| `HATCH_ROOT` | environment | The checkout to work in. Default: walk upwards from the working directory. |
| `HATCH_NIGHT_STATE` | environment | A path where a night's totals cross a restart. Set only by the supervisor in `scripts/hatch.sh`. Its absence is how the loop knows nothing could restart it. |
| `PATH` | environment | Searched for `claude`; on Windows for `claude.exe`, `claude.cmd`, `claude`. Rewritten for the spawned session, below. |

Variables the wrapper and the container read, not the binary:

| Variable | Where | Meaning |
|---|---|---|
| `HATCH_RUNNER_BIN` | `scripts/hatch.sh` | A prebuilt `hatch` to run instead of building one. Must be executable. |
| `HATCH_CHECKOUT` | `compose.yaml` | Host path mounted at `/checkout` in the container runner. |
| `HATCH_RUNNER_NAME` | `compose.yaml` | The container runner's `HATCH_RUNNER`. Default `hatch-runner`. |
| `HATCH_GIT_NAME`, `HATCH_GIT_EMAIL` | `compose.yaml` | Become `GIT_AUTHOR_NAME` and `GIT_AUTHOR_EMAIL` in the container. |
| `HATCH_GIT_TOKEN` | `compose.yaml`, the entrypoint | An HTTPS push token, answered to git through `GIT_ASKPASS`. |
| `HATCH_PORT` | `compose.yaml` | The host port for the API. Default `8080`. |

### Authentication

The client sends one of two things, never both. With a key,
`Authorization: Bearer hatch_ak_…`. Without one, the header
`X-Hatch-Runner: <runner name>`, which only a Hatch running with its wall off
reads. The HTTP client's base address is the origin, and every call has a
two-minute timeout.

### The runner name

`HATCH_RUNNER` if set, else `<hostname up to the first dot>:<checkout path>`.
It is the same string every claim carries and the Runners page keys on. A
related fingerprint, twelve hex characters of a SHA-256 over the canonical
checkout path with symlinks resolved and each segment respelled as the disk
holds it, names the per-checkout lock, so two spellings of one path take one
lock.

## `hatch config`

```
usage: hatch config [--show | --origin <origin>]

  hatch config                    asks for the origin and the key, and writes them
  hatch config --show             says what is set, and which layer it came from
  hatch config --origin <origin>  writes the origin alone, asking nothing
```

- **`--show`** prints the per-user file path (and whether it exists yet), the
  checkout `.env` path or `<not in one>`, then one line per allow-listed
  variable with its value and, in parentheses, the layer it came from. The key
  is masked to its first twelve and last four characters. Always exits 0.
- **`--origin <origin>`** writes the origin, keeping the key and claude path
  already folded from the three layers, then probes.
- **No arguments** needs a terminal (`hatch: config asks questions and needs
  a terminal`, exit 1). Three prompts, each defaulting to what is loaded: the
  origin, the API key (read without echo), and the `claude` path for `work`.
- Refusals: an empty origin (`hatch: an origin is required`); an origin
  without `http://` or `https://` (`hatch: "<x>" has no scheme - every call
  will fail. Write it as https://...`).
- Warnings, non-fatal: no key (`hatch: no key - calls will name themselves
  "<runner>", which only a Hatch with its wall off reads.`); a key not
  starting `hatch_ak_`.
- **The write** goes to a temporary file beside the target, is set to mode
  600 on Unix, and is moved into place. The file carries `HATCH_BASE=`,
  `HATCH_KEY=` (written even when empty, so the omission reads as deliberate)
  and `HATCH_CLAUDE_BIN=` only when set. `config` never writes `scripts/.env`.
- **The probe** is `GET /api/hatch/board`; success prints `reached <origin> -
  columns: <names>`. On failure the file is still written and the message says
  so: `hatch: the file is written but that call did not go through - fix it
  and run config again.` Exit 1.

## Reading commands

### `hatch board`

Takes nothing. `GET /api/hatch/board`, then one line per column, non-deferred
columns first: `<Name>[ (terminal)| (deferred)]: <count>[  (n expedited)]`.

### `hatch next [<column>]`

The top workable card of a column, `todo` by default. Columns are matched on
ASCII letters and digits only, lowercased, so `todo`, `to-do` and `To Do` are
one column. Unknown: `hatch: no column called "<x>" - there is <names>`.
The board arrives already ordered `(status, expedited desc, rank, id)` and
nothing is re-sorted client-side. The first card whose ready date has arrived
on the caller's calendar day is printed as
`<Key>  [<type>]  <Title>[  (expedited)][  (due <date>)]`. Nothing workable:
`hatch: nothing workable in "<column>"` on stderr, exit 2.

### `hatch queue [<ancestor key>]`

`GET /api/hatch/work/queue?offsetMinutes=<utc offset>[&ancestorKey=<key>]`.
Every issue a dispatch pass would look at, in the order it looks, each with
the reason it would be folded past or `-> <next column>`. A row marked `!`
is expedited; the column only appears when some row is. An empty answer is a
sentence, not a blank line: `hatch: nothing on the board is on the
dispatcher's path`, or the same `under <key>`. Exit 0.

### `hatch show AER-12`

Three reads: the issue, the statuses, the comments. Prints the header
`<Key>  [<type>]  <Title>`, `status:`, then only when set `expedite: yes -
this one goes first`, `parent:`, `children:`, `depends:`, `blocks:`, `ready:`,
`due:`; the description verbatim; then `--- N comment(s) ---` with each as
`[<timestamp>] <author>:` and its body. A missing issue: `hatch: <key> -
there is nothing there`, exit 1.

### `hatch questions [AER-12]`

`GET /api/hatch/questions?open=true`, or the one issue's. None: `hatch:
nothing is waiting on an answer[ on <key>]`. Otherwise `--- N open
question(s) ---`, each as `<KEY>  #<id>  <title>`, who asked and when, the
body, and numbered options with `(recommended)` where marked, then
`answer them: hatch answer[ <key>]`.

### `hatch pr AER-12` (read) and `hatch depends AER-12` (read)

Described with their writing forms below.

## Writing commands

### `hatch start AER-12`

Exactly `hatch move AER-12 "in progress"`.

### `hatch move AER-12 <column>`

`POST /api/hatch/issues/<key>/move` with the matched column's id. Refused with
exit 1 for an unknown column, a terminal column (`hatch: "<x>" is a terminal
column - only the operator moves a ticket there`) and a deferred column
(`hatch: "<x>" is a deferred column - only the operator shelves a ticket`).
Prints `<key> -> <column>`.

### `hatch comment AER-12 "the body"`

`POST /api/hatch/issues/<key>/comments`. Prints `commented on <key> as
<author>`.

### `hatch pr AER-12 [<url>|--clear]`

- One argument: prints the URL alone on stdout, so `open "$(hatch pr
  AER-12)"` is the whole of "show me the review". No URL set: `hatch: <key>
  points at no pull request` on stderr, exit 2.
- A URL: `PATCH /api/hatch/issues/<key>` with only that field. The server
  requires an absolute `http` or `https` address and parses nothing else about
  it.
- `--clear`: the same patch with an empty string. A literal empty argument is
  refused: `hatch: give a url, or --clear to take <key> off the one it has`.

### `hatch depends AER-12 [<key>|--remove <key>]`

- One argument: reads the issue.
- A second key: `POST /api/hatch/issues/<key>/dependencies`, meaning the first
  waits on the second. Re-adding an existing edge writes nothing and is not an
  error. Self, ancestor, descendant and cycle-forming edges are refused by the
  server by name.
- `--remove <key>`: `DELETE /api/hatch/issues/<key>/dependencies/<other>`.

Output is always two lines: `<key> waits on a, b` or `<key> waits on
nothing`, then `a, b wait on <key>` or `nothing waits on <key>`. An edge
gates one move, into the implementation column, and clears when the issue it
names is in a terminal column.

### `hatch ask AER-12 "the question" [--option "Label: detail"]... [--recommend "Label: detail"]`

Flags may repeat and interleave; each consumes the next argument. The first
bare argument is the key, the second the body; a third is refused (`ask takes
one issue and one question - put the choices in --option`). Any other
`-`-prefixed token: `hatch: ask does not take <flag>`. An option is split on
the first `: ` into label and detail; no separator gives a bare label.

Posts a comment with `Kind: "question"` and the options, then prints:

```
asked question #<id> on <KEY> as <author>[ with N options]

<KEY> will not be dispatched again until it is answered:
  hatch answer <KEY>
  <origin>/issues/<KEY>
```

### `hatch answer [AER-12]`

Needs a terminal (`hatch: answer reads your replies and needs a terminal`).
Walks every open question, or the one issue's, one at a time: a header
`i/N  <KEY>  <title>`, who asked, the body, numbered options, then a `> `
prompt. A reply that is a number in range is replaced by **that option's
label**, so the ticket records the decision and not an index. A line ending
in `\` continues onto the next. Enter alone leaves the question open. Ctrl-D
ends the whole session. Each answer is a comment with `Kind: "answer"` bound
to the question's id. Ends with `answered N of M` and, if any were, `the
tickets that are now clear can be worked: hatch work[ <key>]`.

### `hatch api <METHOD> <path> [<json body>]`

The method is upper-cased, the path normalised to one leading `/`, and the
body sent as typed with `Content-Type: application/json`. A 2xx prints the
body and exits 0; anything else prints the refusal to stderr and exits 1,
because here a non-2xx is the answer. The shapes for filing and reshaping
work are in `docs/hatch-planning.md` in the repository.

### `hatch runner-claude-token`

For the container runner's entrypoint, not for interactive use. `GET
/api/hatch/settings/claude-token`; prints the decoded token on stdout. Exits
0 having printed one, 2 when the Hatch holds none yet, 1 when the call failed
or the value could not be decoded (`hatch: <origin> answered with a token
this build cannot read - is it newer than this runner?`). A Hatch with its
wall up refuses the route to every caller. The stored value is obfuscated,
not encrypted, and the CLI carries the fifteen-line decoder deliberately so
the container needs no `jq`, `python` or `openssl`.

## `hatch work`

```
usage: hatch work [<issue key>] [--under <epic key>] [-i] [--quiet]
                  [--model <model>] [--effort <effort>] [--dry-run]
```

| Flag | Takes | Default | Meaning |
|---|---|---|---|
| positional | an issue key | none: take the next | Names the ticket outright |
| `--under <epic key>` | value | none: whole board | Look only under that epic's subtree |
| `--dry-run` | | off | Print the prompt and exit. Claims nothing, spawns nothing. |
| `-i`, `--interactive` | | off | A session you sit in, rather than a headless one |
| `--quiet` | | off | Say nothing until the increment is finished |
| `--model <model>` | value | the playbook's | Beat the playbook, for this run only |
| `--effort <effort>` | value | the playbook's | Likewise |

A key and `--under` together are refused. Any other `-`-prefixed token:
`hatch: work does not take <flag>`.

### `--dry-run`

Reads `GET /api/hatch/work/<key>` for a named ticket, or
`GET /api/hatch/work/next?offsetMinutes=…[&ancestorKey=…]` otherwise, and
never the claim walk. Prints a header of `# <KEY> <From> -> <To>`,
`# model <m>, effort <e>`, optionally `# <model and/or effort> from <KEY>,
not the playbook`, and then the whole composed prompt. Nothing to do or
blocked: exit 2.

### The increment, in order

1. **Find the `claude` CLI** before any claim, so an increment that cannot
   start does not take a ticket off the board to find that out.
   `HATCH_CLAUDE_BIN` must exist if set (`hatch: HATCH_CLAUDE_BIN is not
   there: <path>`); otherwise `PATH` is searched. Nothing: `hatch: no claude
   CLI on PATH.` and exit 1.
2. **One heartbeat** to `POST /api/hatch/runners/<name>` with kind `once`, so
   a hand-run increment shows on the Runners page. The answer is not read.
3. **Pick the issue.**
   - A named key: `GET /api/hatch/work/<key>`. Nothing there: exit 2. Blocked:
     `hatch: <key> - <why>`, plus the open questions and how to answer them
     when that is the block, exit 2. Then take a claim; a refusal is exit 2.
   - No key: the same walk `go-to-work` uses. Read the queue; take the rows
     with no block, in board order; for the first five of them, `POST
     /api/hatch/issues/<key>/claim`. A `409` means another runner has it,
     noted and skipped. With a claim held, re-read `GET
     /api/hatch/work/<key>?heldToken=<token>`, so the runner's own lease does
     not fold its own dispatch; if that read is now blocked, release and move
     on. Five refusals is a busy board. Idle or busy: exit 2. Unreadable:
     exit 1.
4. **Model and effort**: the flag, else the playbook's. The server has already
   folded the issue's own override into the playbook it returns, so a flag
   beats an override and an override beats the row.
5. **Spawn** the session (below), or attach with `-i`.
6. **Release the claim** in a `finally`, every way out.

`hatch work` does not fetch, stash or reset the checkout. Only the loop does.

### The claim

`POST /api/hatch/issues/<key>/claim` returns a token and a TTL, five minutes
by default. The runner heartbeats `POST …/claim/heartbeat` every
`max(5s, ttl/5)`, carrying the last line it printed only when it changed. A
`409` on a heartbeat means the lease was lost: the session is killed with its
whole process tree and the increment ends with
`hatch: <key> - <sentence>` and `the session was stopped; nothing further was
written there`. Every other heartbeat failure is weather, retried next tick.
Release is `DELETE …/claim?token=<token>`, idempotent.

### The spawned process

The `claude` CLI is spawned directly, not through a shell. The base arguments,
with the checkout as the working directory:

```
<claude> --model <model> --effort <effort> --add-dir <checkout>
```

Headless adds `-p --permission-mode bypassPermissions`, and `--output-format
stream-json --verbose`, or `--output-format json` under `--quiet`. In print
mode nothing can answer a permission prompt, so any unanticipated permission
would be a silent denial mid-run; bypassing them is a deliberate grant, and
the reason `work` is a command an operator types. The prompt goes in on
stdin. Attached (`-i`) drops `-p`, the permission mode and the output format,
and passes the prompt as an argument, because stdin is the terminal.

The session's `PATH` gets the runner's own directory prepended, when the
running executable is named `hatch`, so `hatch` inside the session is the
binary that spawned it. Cancellation kills the whole process tree, then waits
ten seconds for the output to drain. There is no other timeout: an increment
has no wall-clock cap.

### What is printed

From the stream: `hatch: session <id>` and `hatch:   join it with  claude
--resume <id>` first; `  ✻ thinking… Nk tokens` at most once per 3,000
tokens; `  ⏺ <tool>  <one summarised field>` per tool call, the field being
the first present of `command`, `file_path`, `pattern`, `description`,
`url`, `path`, `key`, clipped to 96 characters; prose as-is; failed tool
results only, as `  ✗ <text>`; and at the end `hatch: done|ended with an
error in <m>m<ss>s, N turns[, $x.xx]` with the resume line again. A pulse,
`  · still working - <last tool call> (<elapsed>)`, prints after
`HATCH_HEARTBEAT` seconds of silence. Streamed lines also feed the claim's
chatter, which the board draws on the card.

Under `--quiet` the CLI's single JSON summary is parsed for the same facts and
the closing prose is printed whole.

### The work-log row

The last fenced block tagged `work-log` in the session's closing text is
lifted out. Its first non-empty line is the title (a leading `title:`
stripped, clipped to 200 characters); the rest is the summary (clipped to
2,000). A missing block still posts the row, marked undescribed. The row
carries the session id, start and end instants from the runner's own clock,
the turn count, the four token counts summed across models, the notional USD,
and whether the run errored. It is posted with `POST
/api/hatch/issues/<key>/work-log` **before** the board is re-read, so it
exists even if later reads fail. If the session produced no result event at
all, nothing is posted: `hatch: no work log entry for <key> - the run ended
before it said what it had spent`. A failed post never fails the increment.

### After the session

1. `GET /api/hatch/work/<key>`: same column means **stalled**, a different
   column means **moved**. A read that fails is neither; the outcome is
   `where it ended up is not known - the board did not answer`.
2. `GET …/questions?open=true`: questions whose ids were not open before the
   run are printed under `--- <KEY> asked N question(s) ---` with the answer
   command and the issue URL.
3. **The stall guard**, when stalled and the lease was not lost. If the
   question count could not be read, nothing is written and the terminal says
   the ticket could not be flagged. Otherwise a comment is posted beginning
   `An unattended increment ran here and left this issue where it found it:
   still in "<From>", under a playbook moving <From> -> <To>.`, with the
   resume command and the exit code when non-zero. If a question was already
   open, that is the flag. If none was, a question goes up: `An unattended
   increment left <KEY> in "<From>" without moving it - what should happen
   to it now?` with exactly two options, `leave it` and `try again`, neither
   recommended, because the whole content of a stall is that nothing here
   knows why it happened.

### Prompt assembly

Joined with newlines, in this order:

1. The playbook's prompt.
2. `---`
3. `## The ticket`: `<KEY>  [<type>]  <title>`, `moving:   <From> -> <To>`,
   then `parent:`, `ready:`, `due:` when set, and the description, or `_No
   description. That is itself worth noting on the ticket._`
4. `## Its children`, one bullet per child, only when there are any.
5. `## Decisions already made`, only for questions with at least one answer:
   *These were asked on this ticket and answered. They are settled: build on
   them, and do not ask again.* Then each as `**Asked (<author>):**` and
   `**Answered (<author>):**`. Unanswered questions are absent, because a
   ticket with one is never dispatched.
6. A fixed tail: `## Reaching Hatch` (the `hatch show`, `start`, `move`,
   `comment`, `ask` and `api` calls, noting the key is already in the
   environment); `## When you cannot decide` (the `ask --recommend --option`
   shape, and *then stop*); `## Where this increment ends` (`<KEY> should be
   in "<To>" when you stop, and no further`, only the operator moves work
   into a terminal column, do not edit playbooks); and the instruction to end
   with a `work-log` block of a title line and a summary under 100 words.

## `hatch go-to-work`

```
usage: hatch go-to-work [--under <epic key>] [--once] [--quiet]
                        [--interval <seconds>] [--max-runs <n>]
                        [--max-spend <dollars>] [--until <HH:MM>]
                        [--stop-file <path>]
                        [--restart-after <minutes> | --no-restart]
```

| Flag | Takes | Default | Meaning |
|---|---|---|---|
| `--under <epic key>` | value | none | Stay inside one epic's subtree |
| `--once` | | off | One pass and out. Also heartbeats as kind `once` and disarms restarts. |
| `--quiet` | | off | No per-increment stream, only what each one ended as |
| `--interval <seconds>` | integer ≥ 1 | `60` | The wait when there was nothing to do. `0` is refused, not clamped. |
| `--max-runs <n>` | integer ≥ 0 | unset | Stop after that many increments |
| `--max-spend <dollars>` | decimal ≥ 0 | unset | Stop once the night's notional cost reaches it |
| `--until <HH:MM>` | `HH:mm` | unset | Stop at that wall-clock time, today or tomorrow if it has gone by |
| `--stop-file <path>` | path | unset | Stop once the path exists |
| `--restart-after <minutes>` | integer ≥ 0 | `30` | Ask to be restarted as a newer build at least that often; `0` only when the source actually changed |
| `--no-restart` | | off | Never ask to be restarted |

Refusals: a ticket key (`hatch: go-to-work does not take a ticket - it asks
the board what is next, until there is nothing.`); a key with `--under`; an
`--interval` under one; a negative `--restart-after`; a `--stop-file` that
already exists (`hatch: <path> already exists - that is the stop signal, so
nothing would run. Remove it, or name another path.`). `--until` is resolved
before anything is spawned.

### The per-checkout lock

A directory `<temp>/hatch-go-to-work-<fingerprint>.lock` holding a `pid` file
created exclusively. A live owner:

```
hatch: a go-to-work is already running in <root>, pid <n>.
hatch: one loop per checkout is the premise - join that one, stop it, or work in another checkout.
```

A lock whose owner is no longer running is cleared (`hatch: clearing a stale
lock left by pid <n>`) and retaken. The lock lives outside the repository so
it cannot be committed, and is keyed on the canonical path so two spellings
of one checkout take one lock.

### Each iteration, in order

1. **Check for the `claude` CLI** once, before the loop; missing, the run
   ends with `there is no claude CLI to spawn`.
2. **Heartbeat** `POST /api/hatch/runners/<name>` with kind `loop`, the last
   line said, and the current `--under`, `--max-runs`, `--max-spend` and
   `--until`. Sent at the top, the one moment no claim is held. With `--once`
   the answer is discarded.
3. The board answering **`stopping`** ends the run cleanly, exit 0: `the
   board asked this runner to stop`.
4. **The board's bounds replace the flags**, absences included, so a cap
   cleared on the Runners page is cleared here. Differences are announced:
   `hatch: the board set --max-runs 5, --until 06:00`. The flags seed the
   row once; after that the row is where the bounds live.
5. **Stop conditions**, below.
6. **The age backstop**: an incarnation older than `--restart-after` minutes
   restarts, with the night's totals carried forward. Checked here because an
   idle loop never resets the tree and so would never see a source change.
7. **`paused`**: keep heartbeating, say so once (`hatch: the board has this
   runner paused - nothing will be picked up until it is set running`), then
   `hatch: still paused, <duration> now` every ten minutes, nap an interval,
   and continue.
8. **One pass**, below.
9. `--once` ends the run after the pass.
10. An increment that ran is followed immediately by the next iteration.
    Anything else naps for the interval, in five-second slices, re-checking
    the stop conditions after each.

While idle the reasons the board is folded are printed once, grouped by
reason and counted, then again only when the digest changes; otherwise
`hatch: still nothing an agent may move, <duration> now` (or `every issue an
agent could take is still being worked elsewhere`) every ten minutes, and
`hatch: waiting, and asking again every <interval>s` once after the first
report.

### One pass

1. **Pick**, exactly as `work` does. Idle, busy or an unreadable board: wait
   an interval (`hatch: the board did not answer - asking again in
   <interval>s`).
2. With the claim held, print what was folded past: `hatch:   folded past N
   issue(s) on the way here:` with one line per reason, and `hatch:   another
   runner had  <key>  <sentence>` per busy ticket.
3. **Prepare the workspace**, below. It is done after the board read, so an
   idle loop does not fetch every interval all night. A workspace that can
   never be reset ends the run (`the workspace could not be reset`); one that
   is not ready yet, an unreachable origin, waits an interval.
4. **Check the loop's own source.** If it changed, print which files and
   return asking to restart, inside the `try` so the claim is released and
   the next incarnation finds the ticket free.
5. **Run the increment** with the playbook's own model and effort. No flag
   reaches here.
6. Record it and print `hatch: <KEY> moved, <outcome>  (N increment(s),
   $x.xx)` or `hatch: <KEY> did not move - <outcome>  …`.
7. Release the claim.

### Preparing the workspace

All through `git` in the checkout, in this order:

1. `git rev-parse --git-dir`. Not a repository: never.
2. `git fetch --quiet --prune origin`, before naming the trunk, so an
   unreachable origin is always the fetch's answer. Failure: `hatch: could not
   fetch from origin`, later.
3. Name the trunk: `HATCH_BASE_BRANCH`; else `git symbolic-ref
   refs/remotes/origin/HEAD`; else `git ls-remote --symref origin HEAD`.
   None: `hatch: cannot tell which branch is the trunk here - name it in
   HATCH_BASE_BRANCH`, never.
4. `git rev-parse --verify refs/remotes/origin/<trunk>`. Missing: `hatch:
   origin has no <trunk> - HATCH_BASE_BRANCH names the trunk if it is called
   something else`, never.
5. **Stash** if `git status --porcelain` is non-empty: `git stash push
   --include-untracked --message "hatch: workspace at <date> <hour>"`, then
   `hatch:   stashed N change(s) the tree was carrying - git stash pop takes
   them back`. Ignored files are never included. A stash that fails, such as a
   merge in progress: `hatch: the tree has changes in it that would not stash
   - clear them by hand`, never.
6. If the local trunk is ahead of origin, warn: `hatch:   <trunk> was N
   commit(s) ahead of origin and is not now - git reflog has them`.
7. `git checkout --quiet -B <trunk> refs/remotes/origin/<trunk>`, then
   `hatch:   workspace on <trunk> at <sha>, was <previous head>`.
8. **Prune**: every local branch whose upstream is `[gone]` and was under
   `origin` is force-deleted, with `hatch:   deleted <name>, was <sha> -
   origin dropped it when it merged`, so `git branch <name> <sha>` puts it
   back. Force, because a squash merge leaves the commits nowhere in the
   trunk's history. Never fails the reset.

### Restarts

Exit code 75 asks whatever started the loop to rebuild it and run it again.
It is **armed only when** `--no-restart` is absent, `--once` is absent, and
`HATCH_NIGHT_STATE` is set, which only the supervisor in `scripts/hatch.sh`
does. Without a supervisor, including the container runner and any
`hatch go-to-work` typed by hand, restarts are disarmed and the loop is the
build it started as.

The loop's own source is `scripts/hatch.sh`, `src/Hatch.Cli` and
`src/Hatch.Contracts` in the checkout, every file recursively excluding `bin`
and `obj` segments, hashed together. A baseline is taken at start-up and is
deliberately not carried across a restart, so a rebuild that failed does not
ask again for the same change. Two triggers: the digest changed after a reset
pulled new source, or the incarnation is older than `--restart-after`
minutes. A restart never holds a claim.

On the way out the night's totals (runs, spend, start, failure streak,
restarts, the resolved `--until`, the moved and stalled keys) are written to
`HATCH_NIGHT_STATE`. If that write fails the restart is cancelled rather than
starting the budget over. `--until` is carried as an instant, not recomputed,
so a restart across midnight does not add a day. On any other ending the
state file is deleted and the tally printed.

The supervisor, `scripts/hatch.sh go-to-work`, creates the state file with
`mktemp`, runs the loop, and on exit 75 runs `make build-hatch` (falling back
to `dotnet build`, then to a message) and starts it again with `hatch:
restarting`. Any other exit code is passed through. A failed build is never
fatal: `carrying on with the binary that is there`. Its own traps exit 130 on
interrupt and 143 on terminate.

### Stop conditions

Checked before every increment and after every five-second nap slice, in
this order:

1. **Three failed increments in a row**: `three increments in a row failed:
   <keys with exit codes>`. First, because it is the one that says something
   is wrong. A zero exit clears the streak; a lost lease neither counts as a
   failure nor resets it.
2. The stop file exists.
3. `--max-runs N reached`.
4. `--max-spend X reached at $Y`.
5. `--until HH:MM has come`.

And outside that list: the board answering `stopping`; a workspace that can
never be reset; `--once`; no `claude` CLI; an interrupt.

### The tally

Printed once, on the way out, whatever the reason:

```
hatch: <why it stopped>
hatch: N increment(s) in <duration>, $<x.xx>[, N restart(s)]
hatch:   moved    <KEY>  <outcome>
hatch:   stalled  <KEY>  <outcome>
```

## The Runners page, from the runner's side

Every heartbeat is `POST /api/hatch/runners/<url-encoded name>` carrying the
kind (`loop` or `once`), the last line printed, and the four bounds. The
answer carries the state (`running`, `paused`, `stopping`) and the bounds the
page holds, which replace the loop's own. A heartbeat that does not answer is
weather: the loop keeps running on what it had. A row is idle while heard
from, gone after ninety seconds of silence by default, and dropped from the
read after ten times that. Nothing on the server starts or stops a process.
The page's controls are person-only; a key can read and heartbeat, never
write, because an agent that could raise its own `--max-spend` could raise
its own budget.

## HTTP surface

| Method | Path | Used by |
|---|---|---|
| GET | `/api/hatch/board` | `board`, `next`, `move`, the `config` probe |
| GET | `/api/hatch/statuses` | `show` |
| GET | `/api/hatch/issues/{key}` | `show`, `pr` read, `depends` read |
| GET | `/api/hatch/issues?ancestorKey=…` | the idle report under an epic |
| GET | `/api/hatch/issues/{key}/comments` | `show` |
| POST | `/api/hatch/issues/{key}/comments` | `comment`, `ask`, `answer`, the stall comment |
| POST | `/api/hatch/issues/{key}/move` | `move`, `start` |
| PATCH | `/api/hatch/issues/{key}` | `pr` set and clear |
| POST, DELETE | `/api/hatch/issues/{key}/dependencies[/{other}]` | `depends` |
| GET | `/api/hatch/questions?open=…`, `/api/hatch/issues/{key}/questions?open=…` | `questions`, `answer`, before and after an increment |
| GET | `/api/hatch/work/queue?offsetMinutes=…[&ancestorKey=…]` | `queue`, the picker, the idle report |
| GET | `/api/hatch/work/next?offsetMinutes=…[&ancestorKey=…]` | `work --dry-run` only |
| GET | `/api/hatch/work/{key}[?heldToken=…]` | `work`, the picker's second read, the post-increment read |
| POST | `/api/hatch/issues/{key}/claim` | taking a claim |
| POST | `/api/hatch/issues/{key}/claim/heartbeat` | keeping it |
| DELETE | `/api/hatch/issues/{key}/claim?token=…` | releasing it |
| POST | `/api/hatch/issues/{key}/work-log` | the work-log row |
| POST | `/api/hatch/runners/{name}` | the runner heartbeat |
| GET | `/api/hatch/settings/claude-token` | `runner-claude-token` |

`offsetMinutes` is the machine's UTC offset, so ready dates fold against the
caller's calendar day. Serialization is source-generated because the
published binary is trimmed; a wire type not registered is a build-time
mistake and says so.

## Host prerequisites

| Tool | Needed by | How |
|---|---|---|
| `git` | `go-to-work`'s workspace reset, and whatever the session does | Spawned directly for `rev-parse`, `fetch`, `symbolic-ref`, `ls-remote`, `status`, `stash push`, `rev-list`, `checkout -B`, `for-each-ref`, `branch --delete`. Missing git reads as a workspace that can never be reset. |
| the `claude` CLI, logged in | `work`, `go-to-work` | Found on `PATH` or in `HATCH_CLAUDE_BIN`. |
| .NET SDK 10 | only `scripts/hatch.sh` and `make build-hatch` inside a Hatch checkout | A downloaded binary needs no .NET at all. |
| `make` | the supervisor's rebuild | Falls back to `dotnet build`, then to a message. |
| bash 3.2 or newer | `scripts/hatch.sh` only | |
| Docker | the board, and the container runner | |

Not used anywhere: the GitHub CLI, Node, `jq`, `curl`, `python`, `openssl`.
