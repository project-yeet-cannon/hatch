#!/usr/bin/env bash
#
# Hatch from a terminal - the few calls a working session actually makes,
# behind one command, so a prompt can say "take the next ticket" instead of
# spelling out curl.
#
# The API is documented in CLAUDE.md and every endpoint here is one of those.
# What this file adds is the two lookups nobody should be repeating: which
# column is "todo" this week (statuses are rows, the operator can rename them)
# and which card is at the top of it (rank order, minus the ones whose ready
# date has not arrived - the board folds those away and so does this).
#
# Two settings, neither in this repo:
#
#   AERIE_BASE       https://hatch.<your domain>   - or a local dev origin.
#                                                    Required.
#   AERIE_HATCH_KEY  aerie_ak_...                  - minted on the admin app's
#                                                    API keys page, shown once.
#                                                    Required against a Hatch
#                                                    with its wall up, which is
#                                                    every cluster install;
#                                                    optional against one
#                                                    started with Auth:Enabled
#                                                    false, where calls name
#                                                    themselves with the runner
#                                                    header instead
#
# And a few optional ones, all of them for the two commands that spawn a
# session - which are the runner's rather than this file's, and read them
# themselves (src/Aerie.Hatch, and `work --help` there):
#
#   HATCH_CLAUDE_BIN   path to the claude CLI, if it is not on PATH
#   HATCH_BASE_BRANCH  the trunk `go-to-work` resets to between increments,
#                      when it is not the one origin calls its default
#   HATCH_RUNNER       what the board calls this runner while it holds a claim.
#                      Default `host:/path/to/checkout`, which is what somebody
#                      reading "who has this ticket" needs to know
#   HATCH_RUNNER_BIN   a built runner, for a machine with no dotnet SDK on it
#   HATCH_HEARTBEAT    seconds of silence before a running session says what it
#                      is still waiting on. 0 turns the pulse off
#
# `./hatch.sh config` asks for them and writes scripts/.env, mode 600 and
# ignored by git. Every command reads that file, and anything already exported
# wins over it - so a one-off origin is a prefix on the command line and not an
# edit. A shell profile still works; the file exists so that a key does not
# have to live in one, readable by everything you run all day.
#
# Never in this repo, either way: Aerie ships to other operators, and a key in
# the artifact is one operator's key inherited by everybody who clones it.
#
# Usage:
#   ./hatch.sh config                 # ask for the settings, write scripts/.env
#   ./hatch.sh config --show          # what is set, and where it came from
#   ./hatch.sh board                  # the columns, and how many cards in each
#   ./hatch.sh next                   # top workable card of "todo"
#   ./hatch.sh next "in progress"     # ...or of any column
#   ./hatch.sh queue                  # every card a pass would look at, and why
#   ./hatch.sh queue AER-1            # ...under one epic
#   ./hatch.sh show AER-12            # the brief, plus its comments
#   ./hatch.sh start AER-12           # move it to "in progress"
#   ./hatch.sh move AER-12 todo       # ...or to any non-terminal column
#   ./hatch.sh comment AER-12 "sha abc123 on branch aer-12-thing"
#   ./hatch.sh pr AER-12              # where it is being reviewed
#   ./hatch.sh pr AER-12 https://...  # ...or say where, having opened one
#   ./hatch.sh pr AER-12 --clear      # ...or take it off the one it has
#   ./hatch.sh depends AER-12         # what it waits on, and what waits on it
#   ./hatch.sh depends AER-12 AER-11  # AER-12 waits on AER-11
#   ./hatch.sh depends AER-12 --remove AER-11   # ...no longer
#   ./hatch.sh ask AER-12 "how should retries be scoped?" \
#       --recommend "Per-node: one budget per node, so a slow node cannot starve" \
#       --option "Global: one budget for the drain, simpler to reason about"
#   ./hatch.sh questions              # everything waiting on an answer
#   ./hatch.sh questions AER-12       # ...or just this ticket's
#   ./hatch.sh answer                 # answer them, one at a time, here
#   ./hatch.sh answer AER-12
#   ./hatch.sh work                   # one increment on the next thing due
#   ./hatch.sh work AER-12            # ...or on this one
#   ./hatch.sh work --under AER-1     # ...or on the next thing under one epic
#   ./hatch.sh work -i AER-12         # ...in a session you sit in
#   ./hatch.sh work --quiet           # ...saying nothing until it is finished
#   ./hatch.sh work --model opus --effort xhigh AER-12
#   ./hatch.sh work --dry-run         # print the prompt, spawn nothing
#   ./hatch.sh go-to-work             # increments, back to back, until told to stop
#   ./hatch.sh go-to-work --once      # ...one pass, and out
#   ./hatch.sh go-to-work --under AER-1     # ...inside one epic, all night
#   ./hatch.sh go-to-work --quiet --interval 300
#   ./hatch.sh go-to-work --max-runs 5 --max-spend 20 --until 08:00
#   ./hatch.sh go-to-work --stop-file /tmp/stop   # touch it to end the loop
#   ./hatch.sh go-to-work --restart-after 60      # ...coming back as a newer build that often
#   ./hatch.sh go-to-work --no-restart            # ...never coming back as a newer one
#   ./hatch.sh api GET /api/hatch/issues?statusId=2
#   ./hatch.sh api PATCH /api/hatch/issues/AER-12 '{"dueAt":"2026-10-01"}'
#
# `work` and `go-to-work` are the two that spawn an agent, and they are not in
# this file: they hold a claim on the ticket they spawn at, which is a lease
# with a clock on it, a background heartbeat and three signals - none of which
# can be tested in a shell script. They live in src/Aerie.Hatch and are reached
# through here unchanged; see docs/hatch.md, "Where the loop lives".
#
# Bash 3.2 compatible on purpose - that is what macOS still ships as /bin/bash.

set -euo pipefail

command -v jq >/dev/null || { echo "hatch: needs jq" >&2; exit 1; }

# ---- Settings ----

script_dir() { CDPATH= cd -- "$(dirname -- "$0")" && pwd; }

# The file `config` writes and every other command reads. Beside the script
# rather than in the repository root: it belongs to this tool, not to the
# build. Already ignored by .gitignore's `.env`, and written 600 regardless -
# the point of it is that a credential need not be exported into every shell
# and every process the day starts with.
env_file() { echo "$(script_dir)/.env"; }

# What the file may carry. An allowlist and a parser rather than a `source`,
# because a credential file that is also a shell script is a larger promise
# than "two values and a path", and a stray line in it should be ignored
# instead of run.
ENV_NAMES="AERIE_BASE AERIE_HATCH_KEY HATCH_CLAUDE_BIN HATCH_BASE_BRANCH HATCH_RUNNER"

# KEY=value a line, blanks and # comments skipped, one layer of surrounding
# quotes stripped. An exported value wins over the file, which is what makes
# `AERIE_BASE=http://localhost:5227 ./hatch.sh board` work without an edit.
load_env() {
  local file line name value current
  file=$(env_file)
  [ -f "$file" ] || return 0

  while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in ''|'#'*) continue ;; esac
    name="${line%%=*}"
    [ "$name" != "$line" ] || continue
    value="${line#*=}"
    name=$(printf '%s' "$name" | tr -d '[:space:]')

    case " $ENV_NAMES " in
      *" $name "*) ;;
      *) echo "hatch: ignoring \"$name\" in ${file} - not one of: ${ENV_NAMES}" >&2; continue ;;
    esac

    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    case "$value" in
      \"*\") value="${value#\"}"; value="${value%\"}" ;;
      \'*\') value="${value#\'}"; value="${value%\'}" ;;
    esac

    eval "current=\${${name}:-}"
    if [ -z "$current" ]; then
      # Exported, not just set: `work` spawns a session that will make these
      # same calls, and it should not have to find the file a second time.
      eval "export ${name}=\$value"
    fi
  done < "$file"
}

# Called by every command that talks to Hatch, and by none of the ones that do
# not - `config` has to be able to run before there is anything to require.
# The origin alone. The key is not required, because a Hatch started with its
# wall off has no credential to present and names its callers by the runner
# header instead - see docs/auth-architecture.md, "Local mode". A key against
# that Hatch still works and still wins; a missing one against a Hatch with a
# wall is a 401, and `api` says which of the two happened.
require_env() {
  if [ -z "${AERIE_BASE:-}" ]; then
    cat >&2 <<MISSING
hatch: not configured.

  ./scripts/hatch.sh config

  asks for the Hatch origin and an aerie_ak_ key and writes them to
  $(env_file), which git ignores. Exporting AERIE_BASE and
  AERIE_HATCH_KEY yourself works too, and wins over the file. The key
  may be left empty for a Hatch running with its wall off.
MISSING
    exit 1
  fi
  base="${AERIE_BASE%/}"
}

# What a keyless call calls itself: the same host:/path sentence
# Checkout.Runner builds, so one runner reads one way whichever half of the
# tooling is speaking. Capped from the head like that one does, because the end
# of a path is the part that names a checkout.
RUNNER_MAX=240

runner_name() {
  local name root
  if [ -n "${HATCH_RUNNER:-}" ]; then
    name="$HATCH_RUNNER"
  else
    root=$(git rev-parse --show-toplevel 2>/dev/null || pwd)
    name="$(hostname -s 2>/dev/null || hostname):${root}"
  fi

  if [ "${#name}" -gt "$RUNNER_MAX" ]; then
    printf '...%s' "${name: -$((RUNNER_MAX - 3))}"
  else
    printf '%s' "$name"
  fi
}

# Enough of a key to recognise which one it is, and not enough to use.
mask_key() {
  local k="$1"
  if [ "${#k}" -le 16 ]; then echo "(set)"; else echo "${k:0:12}...${k: -4}"; fi
}

cmd_config() {
  local file cur_base cur_key cur_bin ans tmp board
  file=$(env_file)

  if [ "${1:-}" = "--show" ]; then
    echo "file:             ${file}$([ -f "$file" ] || echo ' (does not exist yet)')"
    echo "AERIE_BASE:       ${AERIE_BASE:-<unset>}"
    echo "AERIE_HATCH_KEY:  $([ -n "${AERIE_HATCH_KEY:-}" ] && mask_key "$AERIE_HATCH_KEY" || echo "<unset - calls go out as \"$(runner_name)\", which only a wall-off Hatch reads>")"
    echo "HATCH_CLAUDE_BIN: ${HATCH_CLAUDE_BIN:-<unset, using PATH>}"
    return
  fi
  [ $# -eq 0 ] || { echo "hatch: config takes nothing, or --show" >&2; exit 1; }

  [ -t 0 ] || { echo "hatch: config asks questions and needs a terminal" >&2; exit 1; }

  # Whatever is loaded already is the default, so changing one setting is not
  # an excuse to retype the other.
  cur_base="${AERIE_BASE:-}"
  cur_key="${AERIE_HATCH_KEY:-}"
  cur_bin="${HATCH_CLAUDE_BIN:-}"

  echo "Writing ${file}. Enter keeps what is shown in brackets."
  echo

  printf 'Hatch origin [%s]: ' "${cur_base:-https://hatch.<your domain>}"
  IFS= read -r ans || ans=""
  if [ -n "$ans" ]; then cur_base="$ans"; fi
  case "$cur_base" in
    http://*|https://*) ;;
    '') echo "hatch: an origin is required" >&2; exit 1 ;;
    *)  echo "hatch: \"$cur_base\" has no scheme - every call will fail. Write it as https://..." >&2; exit 1 ;;
  esac

  # Read without echo: this is the one value on the screen that a screenshot,
  # a shoulder or a scrollback should not be able to keep.
  printf 'API key [%s]: ' "$([ -n "$cur_key" ] && mask_key "$cur_key" || echo 'aerie_ak_...')"
  IFS= read -rs ans || ans=""
  echo
  if [ -n "$ans" ]; then cur_key="$ans"; fi
  case "$cur_key" in
    '') echo "hatch: no key - calls will name themselves \"$(runner_name)\", which only a Hatch with its wall off reads." >&2 ;;
    aerie_ak_*) ;;
    *) echo "hatch: warning - that does not start with aerie_ak_. Carrying on; the call below will say." >&2 ;;
  esac

  printf 'claude CLI path, for `work` [%s]: ' "${cur_bin:-on PATH}"
  IFS= read -r ans || ans=""
  if [ -n "$ans" ]; then cur_bin="$ans"; fi

  # 077 covers the window between creating the temp file and chmod'ing it; the
  # rename is what makes a half-written file impossible to read as a whole one.
  tmp="${file}.$$"
  ( umask 077; : > "$tmp" )
  chmod 600 "$tmp"
  {
    echo "# Hatch's settings, written by \`scripts/hatch.sh config\`."
    echo "#"
    echo "# Ignored by git and mode 600. The key does not belong in a commit, a"
    echo "# plan, an issue or a paste - Aerie ships to other operators, and the"
    echo "# aerie_ak_ prefix exists so that one which slips into a diff is"
    echo "# recognisable on sight. Re-run \`config\` to change any of this."
    echo
    printf 'AERIE_BASE=%s\n' "$cur_base"
    # Written even when empty, so the file records that the omission was
    # deliberate rather than looking like a half-finished config.
    printf 'AERIE_HATCH_KEY=%s\n' "$cur_key"
    if [ -n "$cur_bin" ]; then printf 'HATCH_CLAUDE_BIN=%s\n' "$cur_bin"; fi
  } >> "$tmp"
  mv "$tmp" "$file"

  export AERIE_BASE="$cur_base" AERIE_HATCH_KEY="$cur_key"
  [ -z "$cur_bin" ] || export HATCH_CLAUDE_BIN="$cur_bin"
  require_env

  echo
  echo "wrote ${file}"

  # Written before it is proven, on purpose: a key that is refused is worth
  # keeping on disk to fix, and the message below says what to fix.
  if board=$(api GET /api/hatch/board); then
    echo "reached ${base} - columns: $(columns_named "$board")"
  else
    echo "hatch: the file is written but that call did not go through - fix it and run config again." >&2
    exit 1
  fi
}

# ---- The wire ----

# Every call goes through here so one place decides what a failure looks like.
# The body and the status code come back together and the body is printed on a
# refusal, because Hatch's errors are sentences worth reading - "AER-12 is in
# another project", not "400".
api() {
  local method="$1" path="$2" body="${3:-}"
  local response code
  local -a args
  args=(-sS -X "$method" -w '\n%{http_code}')

  # One or the other, never both. A key is a credential and outranks a name;
  # the header is only a name, and only a Hatch with its wall off reads one.
  if [ -n "${AERIE_HATCH_KEY:-}" ]; then
    args+=(-H "Authorization: Bearer ${AERIE_HATCH_KEY}")
  else
    args+=(-H "X-Hatch-Runner: $(runner_name)")
  fi
  [ -n "$body" ] && args+=(-H 'Content-Type: application/json' -d "$body")

  if ! response=$(curl "${args[@]}" "${base}/${path#/}" 2>&1); then
    # curl wrote its complaint and the -w code into the same capture; the code
    # is meaningless when the connection never happened, so it is dropped.
    echo "hatch: could not reach ${base} - $(printf '%s' "$response" | grep -v '^[0-9]\{3\}$' | tr '\n' ' ')" >&2
    exit 1
  fi

  code="${response##*$'\n'}"
  response="${response%$'\n'*}"

  case "$code" in
    2*) [ -n "$response" ] && printf '%s\n' "$response"; return 0 ;;
    401)
      if [ -n "${AERIE_HATCH_KEY:-}" ]; then
        echo "hatch: 401 - the key was not accepted. Minted, not revoked, copied whole?" >&2
      else
        # No credential was sent, so nothing was rejected: this Hatch has its
        # wall up and wants one. Said plainly, because "the key was not
        # accepted" would send somebody looking at a key they never set.
        echo "hatch: 401 - no key was sent and this Hatch has its wall on. Run \`./scripts/hatch.sh config\`." >&2
      fi
      ;;
    403) echo "hatch: 403 - the key is good and this route is not one it may take (CLAUDE.md)." >&2 ;;
    404) echo "hatch: 404 - no such issue or route: ${path}" >&2 ;;
    *)   echo "hatch: ${code} - ${response}" >&2 ;;
  esac
  exit 1
}

# The path is written with a leading slash everywhere it is called; strip it
# once here so ${base}${path} never doubles up.
_get() { api GET "/${1#/}"; }

# ---- Columns ----

# Statuses are rows and the operator may rename them, so a column is found by
# a loose match on its name - case, spaces and punctuation all ignored - and
# never by a hardcoded id.
column_id() {
  local want="$1" board="$2"
  jq -r --arg want "$want" '
    def norm: ascii_downcase | gsub("[^a-z0-9]"; "");
    (.statuses // .) | map(select((.name | norm) == ($want | norm))) | first | .id // empty
  ' <<<"$board"
}

columns_named() {
  jq -r '(.statuses // .) | map(.name) | join(", ")' <<<"$1"
}

# This machine's offset from UTC, in seconds. Both of the comparisons below
# need it, because "ready" is a question about calendar days in the reader's
# zone and not about elapsed hours - see schedule.ts, which holds the same rule
# for the board.
offset_secs() {
  local tz
  tz=$(date +%z)
  if [ "${tz:0:1}" = "-" ]; then
    echo $(( 0 - (10#${tz:1:2} * 3600 + 10#${tz:3:2} * 60) ))
  else
    echo $(( 10#${tz:1:2} * 3600 + 10#${tz:3:2} * 60 ))
  fi
}

# Today, as a day number. An issue is workable from the start of the day it
# names, whatever hour it was set to.
today_day() { echo $(( ( $(date +%s) + $(offset_secs) ) / 86400 )); }

# The same thing in minutes, which is what the API wants in order to fold ready
# dates against the caller's calendar day rather than the server's.
offset_minutes() { echo $(( $(offset_secs) / 60 )); }

# ---- Commands ----

cmd_board() {
  local board
  board=$(_get /api/hatch/board)
  jq -r '
    .statuses[] as $s
    | "\($s.name)\(if $s.isTerminal then " (terminal)" else "" end): " +
      ([.issues[] | select(.statusId == $s.id)] | length | tostring)
  ' <<<"$board"
}

cmd_next() {
  local want="${1:-todo}" board id
  board=$(_get /api/hatch/board)
  id=$(column_id "$want" "$board")
  [ -n "$id" ] || { echo "hatch: no column called \"$want\" - there is $(columns_named "$board")" >&2; exit 1; }

  # The board arrives ordered by (status, rank, id), so "the top card" is the
  # first survivor of the filter and no sorting happens here.
  local card
  card=$(jq -r --argjson id "$id" --argjson today "$(today_day)" --argjson off "$(offset_secs)" '
    def dayof($off): if length == 10
      then (. + "T00:00:00Z" | fromdateiso8601 / 86400 | floor)
      else ((fromdateiso8601 + $off) / 86400 | floor) end;
    [ .issues[]
      | select(.statusId == $id)
      | select(.readyAt == null or (.readyAt | dayof($off)) <= $today)
    ] | first // empty
  ' <<<"$board")

  [ -n "$card" ] || { echo "hatch: nothing workable in \"$want\"" >&2; exit 2; }
  jq -r '"\(.key)  [\(.type)]  \(.title)" + (if .dueAt then "  (due \(.dueAt))" else "" end)' <<<"$card"
}

# Everything a pass would look at, and what it would decide about each: the
# same walk `work` takes, printed instead of acted on. It spawns nothing and
# writes nothing.
#
# `next` and `work` fold past what they cannot do in silence, which is right
# when somebody is watching - "nothing to do" is the useful answer. Nobody is
# watching an unattended loop, and then the reasons are the whole point: a
# column and type nobody has written a playbook for reads as a finished board
# from the outside, and this is where it stops reading that way.
#
# The order is the dispatcher's - rightmost column first, and within a column
# the board's own, which is the order `GET /api/hatch/board` serves that column
# in. A card's line here is its place on the board. It arrives that way and
# nothing here re-sorts it, because a script with its own opinion about which
# ticket is next is the drift this endpoint exists to rule out.
#
# This is the long form. A pass that finds nothing prints the same reasons with
# a count each (queue_digest), which is what makes a jammed board legible
# without anybody thinking to run this by hand; this is where the counts are
# turned back into tickets.
cmd_queue() {
  local under="${1:-}" scope=""
  [ -z "$under" ] || scope="&ancestorKey=${under}"

  local queue
  queue=$(_get "/api/hatch/work/queue?offsetMinutes=$(offset_minutes)${scope}")

  # An empty board is a sentence and not a blank line: "there is nothing" and
  # "something went wrong and printed nothing" look identical otherwise, which
  # is the one thing a run nobody watched cannot afford to be unsure about.
  if [ "$(jq 'length' <<<"$queue")" -eq 0 ]; then
    if [ -n "$under" ]; then
      echo "hatch: nothing under ${under} is on the dispatcher's path"
    else
      echo "hatch: nothing on the board is on the dispatcher's path"
    fi
    return 0
  fi

  # Columns are padded to the widest value in the answer rather than to a
  # guessed width - status names are rows the operator renames.
  jq -r '
    def pad($n): . + ((" " * ($n - length)) // "");
    (map(.issue.key | length) | max) as $k
    | ((map(.issue.type | length) | max) + 2) as $t
    | (map(.fromStatus.name | length) | max) as $c
    | .[]
    | (.issue.key | pad($k)) + "  "
      + ("[" + .issue.type + "]" | pad($t)) + "  "
      + (.fromStatus.name | pad($c)) + "  "
      + (.blocked // ("-> " + (.toStatus.name // "?")))
  ' <<<"$queue"
}

# The same scan, as many lines as it has distinct reasons.
#
# The sentence is the group. Two issues held up by the same missing playbook
# say the same words and are one line; the ones naming an issue or a date stay
# apart because they are separate facts. Nothing here parses a reason - a
# script that read one would drift the first time the server reworded it - so
# the grouping is arithmetic on strings the server wrote, and a board's 227
# skip lines come out as five.
#
# Worst first. `sort_by([-.n, .why])` rather than `sort_by(-.n, .why)`: the
# array form sorts identically on every jq, including the one macOS ships.
queue_digest() {
  [ -n "${1:-}" ] || return 0

  jq -r '
    def pad($n): ((" " * ($n - (tostring | length))) // "") + tostring;
    [ .[] | select(.blocked) ] as $rows
    | if ($rows | length) == 0 then empty
      else ($rows | group_by(.blocked)
            | map({n: length, why: .[0].blocked})
            | sort_by([-.n, .why])) as $g
        | ($g | map(.n | tostring | length) | max) as $w
        | $g[] | "hatch:     " + (.n | pad($w)) + "  " + .why
      end' <<<"$1"
}

cmd_show() {
  local key="${1:?usage: hatch.sh show AER-12}"
  local issue statuses comments count
  issue=$(_get "/api/hatch/issues/${key}")
  statuses=$(_get /api/hatch/statuses)
  comments=$(_get "/api/hatch/issues/${key}/comments")

  # Rendered plainly rather than cleverly: the description is the brief, it is
  # markdown, and it goes to the terminal as it was written.
  jq -r --argjson statuses "$statuses" '
    . as $i
    | "\($i.key)  [\($i.type)]  \($i.title)",
      "status:   \((($statuses | map(select(.id == $i.statusId)) | first | .name) // ($i.statusId | tostring)))",
      (if $i.parentKey then "parent:   \($i.parentKey)" else empty end),
      (if ($i.childKeys | length) > 0 then "children: \($i.childKeys | join(", "))" else empty end),
      (if ($i.dependsOnKeys | length) > 0 then "depends:  \($i.dependsOnKeys | join(", "))" else empty end),
      (if ($i.dependentKeys | length) > 0 then "blocks:   \($i.dependentKeys | join(", "))" else empty end),
      (if $i.readyAt then "ready:    \($i.readyAt)" else empty end),
      (if $i.dueAt then "due:      \($i.dueAt)" else empty end),
      "",
      ($i.description // ""),
      ""
  ' <<<"$issue"

  count=$(jq 'length' <<<"$comments")
  if [ "$count" -gt 0 ]; then
    echo "--- $count comment(s) ---"
    jq -r '.[] | "[\(.createdAt)] \(.author):\n\(.body)\n"' <<<"$comments"
  fi
}

cmd_move() {
  local key="${1:?usage: hatch.sh move AER-12 <column>}" want="${2:?usage: hatch.sh move AER-12 <column>}"
  local board id terminal
  board=$(_get /api/hatch/board)
  id=$(column_id "$want" "$board")
  [ -n "$id" ] || { echo "hatch: no column called \"$want\" - there is $(columns_named "$board")" >&2; exit 1; }

  # Only the operator decides that something shipped (CLAUDE.md). A terminal
  # column is refused here rather than left to a careful prompt, because the
  # rule is about the tool and not about who is holding it.
  terminal=$(jq -r --argjson id "$id" '.statuses[] | select(.id == $id) | .isTerminal' <<<"$board")
  if [ "$terminal" = "true" ]; then
    echo "hatch: \"$want\" is a terminal column - only the operator moves a ticket there (CLAUDE.md)" >&2
    exit 1
  fi

  api POST "/api/hatch/issues/${key}/move" "$(jq -nc --argjson id "$id" '{statusId: $id}')" \
    | jq -r --arg want "$want" '"\(.key) -> \($want)"'
}

cmd_comment() {
  local key="${1:?usage: hatch.sh comment AER-12 \"body\"}" body="${2:?usage: hatch.sh comment AER-12 \"body\"}"
  api POST "/api/hatch/issues/${key}/comments" "$(jq -nc --arg body "$body" '{body: $body}')" \
    | jq -r '"commented on '"$key"' as \(.author)"'
}

# Where an issue is being reviewed: read it, set it, or take it off.
#
# The setting half is what a session that has just opened a pull request runs,
# so that the URL lands on the ticket instead of in a comment somebody has to
# find later. The reading half prints the URL alone, and nothing else, so that
# `open "$(./hatch.sh pr AER-12)"` is the whole of "show me the review".
cmd_pr() {
  local key="${1:?usage: hatch.sh pr AER-12 [<url>|--clear]}" url found

  # No second argument is a read. Distinguished by the count rather than by the
  # value, so that an empty one is the mistake below and not a silent clear.
  if [ $# -lt 2 ]; then
    found=$(_get "/api/hatch/issues/${key}" | jq -r '.pullRequestUrl // empty')
    [ -n "$found" ] || { echo "hatch: ${key} points at no pull request" >&2; exit 2; }
    echo "$found"
    return
  fi

  # "" is what the API reads as the clear (CLAUDE.md), and an empty argument at
  # a prompt is easy to pass by accident and impossible to see afterwards. So
  # the clear is spelled out loud and an empty string is refused.
  url="$2"
  if [ "$url" = "--clear" ]; then
    url=""
  elif [ -z "$url" ]; then
    echo "hatch: give a url, or --clear to take ${key} off the one it has" >&2
    exit 1
  fi

  api PATCH "/api/hatch/issues/${key}" "$(jq -nc --arg url "$url" '{pullRequestUrl: $url}')" \
    | jq -r '.pullRequestUrl // "\(.key) points at no pull request"'
}

# ---- Dependencies ----

# What an issue waits on, and what waits on it.
#
# An edge is what serialises work - not shared parentage, which the board used
# to guess from. A planning session that has just filed five stories that must
# land one after another chains them here, and the loop then walks the chain in
# order; five stories that are independent get nothing and go in whatever order
# the board puts them in.
#
# It gates one move: the one into the column where the code gets written. An
# issue waiting on another is still broken down, still lands in the backlog and
# is still analysed - and the edge clears only when the issue it names is in a
# terminal column, because the point is that story two is not written on story
# one's unmerged branch.
cmd_depends() {
  local key="${1:?usage: hatch.sh depends AER-12 [<key>|--remove <key>]}" issue blocker

  # No second argument is a read, distinguished by the count rather than by the
  # value - so an empty one is the mistake below and not a silent nothing.
  # cmd_pr splits on the same rule and for the same reason.
  if [ $# -lt 2 ]; then
    issue=$(_get "/api/hatch/issues/${key}")
  elif [ "$2" = "--remove" ]; then
    blocker="${3:-}"
    [ -n "$blocker" ] || { echo "hatch: say which issue ${key} should stop waiting on" >&2; exit 1; }
    issue=$(api DELETE "/api/hatch/issues/${key}/dependencies/${blocker}")
  elif [ -n "$2" ]; then
    issue=$(api POST "/api/hatch/issues/${key}/dependencies" \
      "$(jq -nc --arg k "$2" '{dependsOnKey: $k}')")
  else
    echo "hatch: say which issue ${key} waits on, or --remove one" >&2
    exit 1
  fi

  depends_lines "$issue"
}

# Both directions, printed even when empty - "there is nothing" and "something
# went wrong and printed nothing" look identical otherwise, which is the reason
# cmd_queue says so out loud. Keys alone, as `show` prints children: this is one
# read of the issue and no fan-out.
depends_lines() {
  jq -r '
    . as $i
    | (if ($i.dependsOnKeys | length) > 0
       then "\($i.key) waits on \($i.dependsOnKeys | join(", "))"
       else "\($i.key) waits on nothing" end),
      (if ($i.dependentKeys | length) > 0
       then "\($i.dependentKeys | join(", ")) \(if ($i.dependentKeys | length) == 1 then "waits" else "wait" end) on \($i.key)"
       else "nothing waits on \($i.key)" end)
  ' <<<"$1"
}

# ---- Questions ----

# Where a question is read and answered in a browser. Hatch's own origin, where
# "/" is the board - so a question printed here is a link somebody can follow,
# which is the whole point of putting it on the ticket instead of leaving it in
# this scrollback.
issue_url() { echo "${base}/issues/${1}"; }

# Questions for one issue, or for the whole house. The house-wide list is the
# one an operator sitting down to unblock the board wants; a key narrows it to
# the ticket in hand.
questions_json() {
  local key="${1:-}" open="${2:-true}"
  if [ -n "$key" ]; then
    _get "/api/hatch/issues/${key}/questions?open=${open}"
  else
    _get "/api/hatch/questions?open=${open}"
  fi
}

# A question the way it is read: where it came from, who asked, the body, and
# the answers it offers - numbered, because a number is what `answer` takes.
print_questions() {
  jq -r '.[]
    | "\(.issueKey)  #\(.id)  \(.issueTitle)",
      "  asked by \(.askedBy), \(.askedAt)",
      "",
      "  " + (.body | gsub("\n"; "\n  ")),
      "",
      (if (.options // []) | length > 0 then
        (.options | to_entries[]
          | "  [\(.key + 1)] \(.value.label)\(if .value.recommended then "  (recommended)" else "" end)",
            (if .value.detail then "      " + (.value.detail | gsub("\n"; "\n      ")) else empty end))
       else empty end),
      ""' <<<"$1"
}

# One `--option "Label: what taking it means"` folded onto a JSON array.
#
# Split on the first ": " so that a detail may contain colons, which prose does
# constantly. A spec with no separator at all is a bare label - fine for a
# choice that explains itself, and the only shape short enough to type twice.
add_option() {
  local options="$1" spec="$2" recommended="$3" label detail

  case "$spec" in
    *": "*) label="${spec%%: *}"; detail="${spec#*: }" ;;
    *)      label="$spec"; detail="" ;;
  esac

  jq -c --arg label "$label" --arg detail "$detail" --argjson recommended "$recommended" \
    '. + [{
      label: $label,
      detail: (if $detail == "" then null else $detail end),
      recommended: $recommended
    }]' <<<"$options"
}

# What an agent calls when it hits a decision that is not its to make.
#
# One question a call: a question is a row, and two of them in one body cannot
# be answered separately or counted apart. Where the decision is a choice
# between named things, name them - an option is something the operator presses,
# and a paragraph is something they have to read twice and then compose a reply
# to. Prose still works, for the decisions that are not a menu.
cmd_ask() {
  local key="" body="" options="[]" payload

  while [ $# -gt 0 ]; do
    case "$1" in
      --option)    options=$(add_option "$options" "${2:?--option needs \"Label: what it means\"}" false); shift 2 ;;
      --recommend) options=$(add_option "$options" "${2:?--recommend needs \"Label: what it means\"}" true); shift 2 ;;
      -*)          echo "hatch: ask does not take $1" >&2; exit 1 ;;
      *)
        if [ -z "$key" ]; then key="$1"
        elif [ -z "$body" ]; then body="$1"
        else echo "hatch: ask takes one issue and one question - put the choices in --option" >&2; exit 1
        fi
        shift ;;
    esac
  done

  [ -n "$key" ] && [ -n "$body" ] || {
    cat >&2 <<'USAGE'
usage: hatch.sh ask AER-12 "the question" [--option "Label: what it means"]...
                                          [--recommend "Label: what it means"]

  A question that is a choice between named things should name them: each
  --option becomes something the operator can press, in the web UI and here.
  --recommend is an option you would take, and there may be one of those.
USAGE
    exit 1
  }

  payload=$(jq -nc --arg body "$body" --argjson options "$options" \
    '{body: $body, kind: "question"} + (if ($options | length) > 0 then {options: $options} else {} end)')

  api POST "/api/hatch/issues/${key}/comments" "$payload" \
    | jq -r '"asked question #\(.id) on '"$key"' as \(.author)" +
             (if (.options // []) | length > 0 then " with \(.options | length) options" else "" end)'

  echo
  echo "${key} will not be dispatched again until it is answered:"
  echo "  ./scripts/hatch.sh answer ${key}"
  echo "  $(issue_url "$key")"
}

cmd_questions() {
  local key="${1:-}" questions count
  questions=$(questions_json "$key" true)
  count=$(jq 'length' <<<"$questions")

  if [ "$count" -eq 0 ]; then
    echo "hatch: nothing is waiting on an answer${key:+ on $key}"
    return
  fi

  echo "--- ${count} open question(s) ---"
  echo
  print_questions "$questions"
  echo "answer them: ./scripts/hatch.sh answer${key:+ ${key}}"
}

# What a typed reply actually means, given what was offered.
#
# A bare number in range is the option at that position, and what comes back is
# its label - the answer on the ticket should read as the decision it was, not
# as "2". Anything else comes back untouched, including a number when nothing
# was offered and a number out of range: those are somebody typing an answer
# that happens to be numeric, and second-guessing them would be worse than
# taking them literally.
chosen_option() {
  local options="$1" reply="$2" count

  count=$(jq 'length' <<<"$options")
  [ "$count" -gt 0 ] || { printf '%s' "$reply"; return; }

  case "$reply" in
    ''|*[!0-9]*) printf '%s' "$reply"; return ;;
  esac

  if [ "$reply" -ge 1 ] && [ "$reply" -le "$count" ]; then
    jq -r --argjson n "$reply" '.[$n - 1].label' <<<"$options"
  else
    printf '%s' "$reply"
  fi
}

# Every open question, one at a time, on this terminal.
#
# The serial loop is the point rather than a convenience. A question is a
# decision somebody owes, and the way to get a decision out of a person is to
# ask them one thing and wait for it - a list of six printed at once gets
# skimmed and answered in aggregate, which is how a wrong assumption gets in.
#
# An empty reply leaves that question open and moves on, so a session can be
# abandoned halfway without anybody having to answer something badly to get out
# of it. Every answer that is given goes to the ticket as it is typed, so a loop
# interrupted at question four keeps the first three.
cmd_answer() {
  local key="${1:-}" questions count i id issue title body options reply line answered

  [ -t 0 ] || { echo "hatch: answer reads your replies and needs a terminal" >&2; exit 1; }

  questions=$(questions_json "$key" true)
  count=$(jq 'length' <<<"$questions")
  [ "$count" -gt 0 ] || { echo "hatch: nothing is waiting on an answer${key:+ on $key}"; return; }

  echo "${count} open question(s). A number takes that option, Enter alone leaves one open,"
  echo "\\ at the end of a line keeps typing, ^D stops."
  echo

  answered=0
  i=0
  while [ "$i" -lt "$count" ]; do
    id=$(jq -r --argjson i "$i" '.[$i].id' <<<"$questions")
    issue=$(jq -r --argjson i "$i" '.[$i].issueKey' <<<"$questions")
    title=$(jq -r --argjson i "$i" '.[$i].issueTitle' <<<"$questions")
    body=$(jq -r --argjson i "$i" '.[$i].body' <<<"$questions")
    options=$(jq -c --argjson i "$i" '.[$i].options // []' <<<"$questions")

    echo "$((i + 1))/${count}  ${issue}  ${title}"
    echo "$(jq -r --argjson i "$i" '"        asked by \(.[$i].askedBy), \(.[$i].askedAt)"' <<<"$questions")"
    echo
    printf '%s\n' "$body" | sed 's/^/  /'
    echo

    # The offered answers, numbered from one - typing that number is the whole
    # interaction for a question that is a choice, which is most of them.
    if [ "$(jq 'length' <<<"$options")" -gt 0 ]; then
      jq -r 'to_entries[]
        | "  [\(.key + 1)] \(.value.label)\(if .value.recommended then "  (recommended)" else "" end)",
          (if .value.detail then "      " + (.value.detail | gsub("\n"; "\n      ")) else empty end)' <<<"$options"
      echo
    fi

    reply=""
    while :; do
      printf '> '
      # ^D at the prompt ends the whole session rather than this question: it is
      # the gesture for "I am done here", and treating it as an empty answer
      # would silently walk the rest of the list.
      IFS= read -r line || { echo; break 2; }
      case "$line" in
        *\\) reply="${reply}${line%\\}"$'\n' ;;
        *)   reply="${reply}${line}"; break ;;
      esac
    done

    # A bare number that names one of the offered answers is that answer, and
    # what gets written is its label - so the thread reads as a decision rather
    # than as an index into a list nobody kept.
    reply=$(chosen_option "$options" "$reply")

    if [ -z "$(printf '%s' "$reply" | tr -d '[:space:]')" ]; then
      echo "  left open"
    else
      api POST "/api/hatch/issues/${issue}/comments" \
        "$(jq -nc --arg body "$reply" --argjson answers "$id" \
          '{body: $body, kind: "answer", answersId: $answers}')" >/dev/null
      answered=$((answered + 1))
      echo "  answered: ${reply%%$'\n'*}"
    fi
    echo

    i=$((i + 1))
  done

  echo "answered ${answered} of ${count}"

  # Said rather than left to be remembered: answering is only half of it, and
  # the ticket does not move until somebody dispatches it again.
  if [ "$answered" -gt 0 ]; then
    echo "the tickets that are now clear can be worked: ./scripts/hatch.sh work${key:+ ${key}}"
  fi
}


# ---- The runner ----

# `work` and `go-to-work` are a program, not a function in this file.
#
# They stopped being shell the day the loop learned to claim a ticket. Every
# other command here is one request and a sentence about the answer; those two
# are a lease with a clock on it, a heartbeat on a background thread, a session
# that has to be killed the moment the lease goes, and three signals - and none
# of that could be tested on any machine in this house, because nothing in this
# repository tests a shell script and a race is precisely the thing a hand check
# cannot catch twice.
#
# So they live in src/Aerie.Hatch, they are compiled against the same wire
# records the server serves (src/Aerie.Hatch.Contracts), and their behaviour is
# asserted in src/Aerie.Hatch.Tests, which `make test` and CI already run. See
# docs/hatch.md, "Where the loop lives".
#
# This function is the whole of what is left here: find the runner, and hand it
# the arguments and the checkout it is standing in. Settings are read from the
# same scripts/.env by the same rules, so nothing an operator configured moves.

# Where the repository is, whatever directory this was invoked from.
repo_root() { CDPATH= cd -- "$(dirname -- "$0")/.." && pwd; }

# The runner as a built binary if there is one, and `dotnet run` if there is
# not.
#
# A published binary starts in milliseconds and a `dotnet run` spends a few
# seconds deciding whether to build first, which matters not at all for
# `go-to-work` - one launch, all night - and is the whole cost of `work`. So it
# prefers a binary and falls back rather than requiring anybody to publish one.
# HATCH_RUNNER_BIN names one directly, for a machine with no SDK on it.
runner_cmd() {
  local root bin
  root=$(repo_root)

  if [ -n "${HATCH_RUNNER_BIN:-}" ]; then
    [ -x "$HATCH_RUNNER_BIN" ] || {
      echo "hatch: HATCH_RUNNER_BIN is not executable: $HATCH_RUNNER_BIN" >&2; exit 1; }
    echo "$HATCH_RUNNER_BIN"
    return
  fi

  for bin in \
    "${root}/src/Aerie.Hatch/bin/Release/net10.0/hatch-runner" \
    "${root}/src/Aerie.Hatch/bin/Debug/net10.0/hatch-runner"
  do
    [ -x "$bin" ] || continue
    echo "$bin"
    return
  done

  command -v dotnet >/dev/null || {
    cat >&2 <<'MISSING'
hatch: the runner needs either a build of src/Aerie.Hatch or the dotnet SDK.

    make build-hatch          builds it once, and every command after is fast
    export HATCH_RUNNER_BIN=… names one you already have
MISSING
    exit 1
  }

  echo "dotnet|run|--project|${root}/src/Aerie.Hatch/Aerie.Hatch.csproj|--"
}

# The runner and how to launch it, in RUNNER_ARGV - a global because bash 3.2
# cannot return an array and this file is bash 3.2 on purpose. Resolved again
# before every launch, since a rebuild between two of them can put a binary
# where there was only the SDK.
runner_argv() {
  local cmd
  cmd=$(runner_cmd)

  case "$cmd" in
    *"|"*) IFS='|' read -r -a RUNNER_ARGV <<<"$cmd" ;;
    *)     RUNNER_ARGV=("$cmd") ;;
  esac
}

# `work`, through one door. HATCH_ROOT is the checkout: this script knows where
# it lives and the runner would otherwise have to guess, and "which checkout" is
# the question the whole of AERIE-794 is about.
#
# One increment has nothing to carry forward and nothing to come back as, so it
# replaces this process rather than being watched by it.
exec_runner() {
  runner_argv
  HATCH_ROOT="$(repo_root)" exec "${RUNNER_ARGV[@]}" "$@"
}

# ---- The supervisor ----

# `go-to-work` is run rather than exec'd, and this is the whole reason why: a
# loop that spends the night improving this repository is running the version it
# started with, and would be until somebody came and stopped it. Work that lands
# at one in the morning does not reach the run that wrote it.
#
# A process cannot exec itself into a newer build - relaunching the same binary
# relaunches the same code, and the new source has to be compiled by something
# that outlives the process being replaced. That something is this file, which
# was already sitting here as the parent. So the runner asks to come back by
# exiting 75, and everything interesting - deciding to restart, letting go of
# the claim, carrying the night's totals - stays in the runner, where
# `make test-hatch` can assert them.
#
# The totals travel in a file this names once per night: a restart that started
# the budget over would be a way of outspending `--max-spend` by restarting.
night_state=""

forget_night_state() {
  [ -n "$night_state" ] && rm -f "$night_state"
  return 0
}

supervise_go_to_work() {
  local status root
  root=$(repo_root)

  night_state=$(mktemp "${TMPDIR:-/tmp}/hatch-night.XXXXXX")

  # Nothing is ever backgrounded here, so there is nothing to orphan: a signal
  # reaches the runner directly, and bash runs the trap once the runner has
  # finished giving its ticket back.
  trap forget_night_state EXIT
  trap "forget_night_state; exit 130" INT
  trap "forget_night_state; exit 143" TERM

  while :; do
    runner_argv

    # Captured rather than read after the fact, because `set -e` would otherwise
    # end the night on the exit code that is asking for it to continue.
    status=0
    HATCH_ROOT="$root" HATCH_NIGHT_STATE="$night_state" \
      "${RUNNER_ARGV[@]}" go-to-work "$@" || status=$?

    [ "$status" -eq 75 ] || exit "$status"

    rebuild_runner
    echo "hatch: restarting" >&2
  done
}

# The new source, compiled - the one place in this file that builds anything.
#
# A build that fails is not the end of a night: the loop comes back on the
# binary that is there, and having taken its baseline at startup it will not ask
# again for the same change. So this says what went wrong and returns, always.
rebuild_runner() {
  local root cmd
  root=$(repo_root)

  if [ -n "${HATCH_RUNNER_BIN:-}" ]; then
    # A machine with no SDK on it. Restart anyway: hatch.sh itself may be what
    # changed, and this file is read fresh on the way back in.
    echo "hatch: HATCH_RUNNER_BIN names the runner, so there is nothing here to rebuild" >&2
    return 0
  fi

  cmd=$(runner_cmd)
  case "$cmd" in
    *"|"*)
      # The `dotnet run` fallback, which builds on its own. Building here would
      # leave a Release binary that the next launch prefers - a change nobody
      # asked for.
      return 0
      ;;
  esac

  echo "hatch: rebuilding the runner" >&2

  if command -v make >/dev/null; then
    make -C "$root" build-hatch >&2 \
      || echo "hatch: the rebuild failed - carrying on with the runner that is there" >&2
  elif command -v dotnet >/dev/null; then
    dotnet build "${root}/src/Aerie.Hatch/Aerie.Hatch.csproj" --configuration Release >&2 \
      || echo "hatch: the rebuild failed - carrying on with the runner that is there" >&2
  else
    echo "hatch: neither make nor dotnet is here - carrying on with the runner that is there" >&2
  fi

  return 0
}

usage() {
  sed -n '/^# Usage:/,/^#$/p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

load_env

# Every command that talks to Hatch wants an origin and a key before it starts
# doing anything; `config` and `usage` are the two that have to work on a
# machine which has neither yet. Named rather than defaulted, so that a typo
# still comes back as a typo below.
case "${1:-}" in
  board|next|queue|show|start|move|comment|pr|depends|ask|questions|answer|work|go-to-work|api) require_env ;;
esac

case "${1:-}" in
  config)  shift; cmd_config "$@" ;;
  board)   shift; cmd_board "$@" ;;
  next)    shift; cmd_next "$@" ;;
  queue)   shift; cmd_queue "$@" ;;
  show)    shift; cmd_show "$@" ;;
  start)   shift; cmd_move "${1:?usage: hatch.sh start AER-12}" "in progress" ;;
  move)    shift; cmd_move "$@" ;;
  comment) shift; cmd_comment "$@" ;;
  pr)      shift; cmd_pr "$@" ;;
  depends) shift; cmd_depends "$@" ;;
  ask)       shift; cmd_ask "$@" ;;
  questions) shift; cmd_questions "$@" ;;
  answer)    shift; cmd_answer "$@" ;;
  work)    shift; exec_runner work "$@" ;;
  go-to-work) shift; supervise_go_to_work "$@" ;;
  api)     shift; api "${1:?method}" "/${2#/}" "${3:-}" ;;
  ''|-h|--help|help) usage 0 ;;
  *) echo "hatch: no such command \"$1\"" >&2; usage 1 ;;
esac
