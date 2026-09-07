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
# Two settings, both required, neither in this repo:
#
#   AERIE_BASE       https://hatch.<your domain>   - or a local dev origin
#   AERIE_HATCH_KEY  aerie_ak_...                  - minted on the admin app's
#                                                    API keys page, shown once
#
# And one optional, for `work`:
#
#   HATCH_CLAUDE_BIN path to the claude CLI, if it is not on PATH
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
#   ./hatch.sh api GET /api/hatch/issues?statusId=2
#   ./hatch.sh api PATCH /api/hatch/issues/AER-12 '{"dueAt":"2026-10-01"}'
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
ENV_NAMES="AERIE_BASE AERIE_HATCH_KEY HATCH_CLAUDE_BIN"

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
require_env() {
  if [ -z "${AERIE_BASE:-}" ] || [ -z "${AERIE_HATCH_KEY:-}" ]; then
    cat >&2 <<MISSING
hatch: not configured.

  ./scripts/hatch.sh config

  asks for the Hatch origin and an aerie_ak_ key and writes them to
  $(env_file), which git ignores. Exporting AERIE_BASE and
  AERIE_HATCH_KEY yourself works too, and wins over the file.
MISSING
    exit 1
  fi
  base="${AERIE_BASE%/}"
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
    echo "AERIE_HATCH_KEY:  $([ -n "${AERIE_HATCH_KEY:-}" ] && mask_key "$AERIE_HATCH_KEY" || echo '<unset>')"
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
  [ -n "$cur_key" ] || { echo "hatch: a key is required - mint one on the admin app's API keys page" >&2; exit 1; }
  case "$cur_key" in
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
  args=(-sS -X "$method" -H "Authorization: Bearer ${AERIE_HATCH_KEY}" -w '\n%{http_code}')
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
    401) echo "hatch: 401 - the key was not accepted. Minted, not revoked, copied whole?" >&2 ;;
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


# ---- Watching a run ----

# The mark on a line that is a fact for the caller rather than a line for the
# terminal - see render_stream, which puts it on and takes it off again.
FACT_MARK=$'\001'

# How long the terminal may stay silent before it says it is still alive.
# Overridable because the right number depends on what the ticket has the agent
# doing: twenty seconds is right for editing, and irritating while a test suite
# runs. Zero switches the heartbeat off.
HATCH_HEARTBEAT="${HATCH_HEARTBEAT:-20}"

# The event stream, as lines a person can read.
#
# `claude -p` with the default text output prints nothing at all until it is
# finished, which for a ticket-sized increment is minutes of a blank terminal
# that looks exactly like a hang. The JSON stream carries what the chat window
# shows - every tool call, every result, and a running count of thinking tokens
# - so this turns that into a log and the run stops being a black box.
#
# One jq process rather than a call per line: a long run emits thousands of
# events, most of them thinking-token ticks, and a process each would cost more
# than the agent. `foreach` carries the little state that needs carrying, which
# is how the thinking counter reports every few thousand tokens instead of every
# hundred.
#
# --include-partial-messages is deliberately not asked for. It streams assistant
# prose token by token, which is the one thing here that reads fine arriving
# whole, and it multiplies the event count by an order of magnitude to do it.
#
# The second argument is a file, and it is how the two things a caller needs
# back out of a run - the session id and what it cost - escape a pipeline whose
# every stage is a subshell. jq cannot write a second file and stdout is the
# terminal's, so those lines come down the same pipe wearing FACT_MARK and are
# set aside at the end. A control character, because everything else on this
# pipe is prose somebody wrote.
render_stream() {
  local root="${1:-}" facts="${2:-}" line

  # Only the events. The CLI is entitled to say something on stdout that is not
  # one - a warning, an update notice - and jq exits on the first thing it
  # cannot parse, which would take the whole log down with it for a line nobody
  # needed. Line-buffered so the filter does not become the stall it exists to
  # prevent.
  grep --line-buffered '^{' | jq -n -r --unbuffered --arg root "$root" --arg mark "$FACT_MARK" '
    def clip($n): if length > $n then .[0:$n - 1] + "…" else . end;

    # Paths as the repository says them. An absolute path to a file in this
    # tree is most of a terminal line spent on the part that never changes.
    def here: if $root != "" then sub("^" + ($root | sub("/$"; "")) + "/"; "") else . end;
    def flat: gsub("\\s+"; " ") | sub("^ +"; "") | sub(" +$"; "");
    def pad2: tostring | if length == 1 then "0" + . else . end;
    def clock: (. / 60 | floor) as $m | "\($m)m\((. % 60) | pad2)s";

    # The one field of a tool call worth a line of terminal. Ordered by how much
    # it says about what is happening: a command, then a path, then whatever the
    # call was actually about.
    def summarise:
      .input as $i
      | ($i.command // $i.file_path // $i.pattern // $i.description
         // $i.url // $i.path // $i.key // ($i | tostring))
      | tostring | here | flat | clip(96);

    def resume($id): "hatch:   join it with  claude --resume \($id)";

    foreach inputs as $e ({ think: 0, mark: 0, out: null };
      if $e.type == "system" and $e.subtype == "init" then
        .out = $mark + "session=\($e.session_id)\n"
          + "hatch: session \($e.session_id)\n" + resume($e.session_id) + "\n"

      # Thinking is the longest silence a run produces and the one most often
      # mistaken for a hang. Reported every few thousand tokens: often enough to
      # be a pulse, rarely enough not to become the transcript.
      elif $e.type == "system" and $e.subtype == "thinking_tokens" then
        (.think = ($e.estimated_tokens // .think))
        | if .think >= .mark + 3000
          then (.mark = .think) | (.out = "  ✻ thinking… \(.think / 1000 | floor)k tokens")
          else .out = null end

      elif $e.type == "assistant" then
        .out = ([ $e.message.content[]?
                  | if .type == "tool_use" then "  ⏺ \(.name)  \(summarise)"
                    elif .type == "text" and ((.text // "") | flat) != "" then "\n" + .text
                    else empty end ] | join("\n"))

      # Only failures. A tool that worked is told by the next line happening at
      # all, and echoing every result would bury the calls under their own output.
      elif $e.type == "user" then
        .out = ([ $e.message.content[]?
                  | select(.type == "tool_result" and .is_error == true)
                  | "  ✗ " + ((.content
                      | if type == "array" then (map(.text // "") | join(" ")) else tostring end)
                      | flat | clip(96)) ] | join("\n"))

      elif $e.type == "result" then
        .out = $mark + "session=\($e.session_id)\n"
          + (if $e.total_cost_usd then $mark + "cost=\($e.total_cost_usd)\n" else "" end)
          + "\nhatch: "
          + (if $e.is_error then "ended with an error" else "done" end)
          + " in \(($e.duration_ms / 1000 | floor) | clock), \($e.num_turns) turns"
          + (if $e.total_cost_usd then ", $\(($e.total_cost_usd * 100 | round) / 100)" else "" end)
          + "\n" + resume($e.session_id)

      else .out = null end;

      select(.out != null and .out != "") | .out)
  ' | while IFS= read -r line; do
      case "$line" in
        "$FACT_MARK"*) [ -z "$facts" ] || printf '%s\n' "${line#$FACT_MARK}" >>"$facts" ;;
        *) printf '%s\n' "$line" ;;
      esac
    done
}

# The rendered lines, plus a pulse when there are none.
#
# The renderer covers everything the agent says; this covers the gaps between,
# which is where the doubt actually lives - a three-minute `make test-api`
# produces no events at all, and silence is indistinguishable from a crash. So
# the last thing seen is held onto and said back: "still working - Bash make
# test-api (2m14s)" is the difference between waiting and wondering.
watch_stream() {
  local line last="starting up" waited

  [ "$HATCH_HEARTBEAT" -gt 0 ] 2>/dev/null || { cat; return; }

  # SECONDS rather than date(1), in both places it is read below. This loop runs
  # once per line of a stream that can be thousands long, and a fork per line to
  # ask the time would cost more than the rendering does.
  SECONDS=0

  while :; do
    line=""
    waited=$SECONDS

    if IFS= read -r -t "$HATCH_HEARTBEAT" line; then
      printf '%s\n' "$line"

      # Tool calls, and nothing else - the thinking counter is already a pulse,
      # and prose is not a thing the run can be stuck inside of.
      case "$line" in
        *"⏺ "*) last=$(printf '%s' "$line" | sed 's/^ *⏺ *//') ;;
      esac
      continue
    fi

    # Which failure that was, decided by the clock rather than by the exit code.
    #
    # bash documents a status over 128 for an expired -t, and macOS ships 3.2,
    # which predates that and answers 1 for a timeout and 1 for end of stream
    # alike. Reading the code would therefore end the log at the first quiet
    # moment on the one platform this file promises to run on. How long the read
    # actually blocked says the same thing and says it the same way everywhere:
    # it sat out the whole timeout, or it came back early because there is
    # nothing more coming.
    if [ $(( SECONDS - waited )) -lt "$HATCH_HEARTBEAT" ]; then
      # A last line with no newline after it is still a line somebody wants.
      [ -z "$line" ] || printf '%s\n' "$line"
      break
    fi

    printf '  · still working - %s (%dm%02ds)\n' "$last" "$((SECONDS / 60))" "$((SECONDS % 60))"
  done
}

# ---- Working ----

# Where the repository is, whatever directory this was invoked from. The agent
# is spawned here, because a ticket is about this codebase and a session that
# started somewhere else would have to be told so.
repo_root() { CDPATH= cd -- "$(dirname -- "$0")/.." && pwd; }

# This machine's offset from UTC in minutes, which is what the API wants in
# order to fold ready dates against the caller's calendar day rather than the
# server's.
offset_minutes() { echo $(( $(offset_secs) / 60 )); }

# The CLI that runs the increment.
#
# No search of the VS Code extension's bundle, though a binary does live in
# there: it is an implementation detail of a program that updates itself
# weekly, and a loop built on that path breaks on somebody else's release
# schedule. Install the CLI, or name it once in HATCH_CLAUDE_BIN.
claude_bin() {
  if [ -n "${HATCH_CLAUDE_BIN:-}" ]; then
    [ -x "$HATCH_CLAUDE_BIN" ] || { echo "hatch: HATCH_CLAUDE_BIN is not executable: $HATCH_CLAUDE_BIN" >&2; exit 1; }
    echo "$HATCH_CLAUDE_BIN"
    return
  fi

  command -v claude 2>/dev/null && return

  cat >&2 <<'MISSING'
hatch: no claude CLI on PATH.

  Install it, or point at one you have:
      export HATCH_CLAUDE_BIN=/path/to/claude

  The binary inside a VS Code extension directory will work, but it moves with
  every extension update - name it here and expect to rename it, or install the
  standalone CLI and forget about it.
MISSING
  exit 1
}

# Which of the model and the effort the ticket chose rather than the playbook,
# as half a sentence - or nothing at all, which is every issue on a stock board.
#
# The server has already folded an issue's overrides into the playbook it hands
# back, so `.playbook.model` is the value that won and there is nothing here to
# decide. What is left is saying where it came from, and this decides that by
# comparing the value being spawned on against the override the issue carries -
# not by tracking whether a flag was given. The comparison is derivable from
# what the caller already holds, it is exactly right on every unattended path
# (where there are no flags at all), and the one case it softens - an operator
# typing `--model opus` at a ticket already set to `opus` - prints a sentence
# that is still true.
#
# `// empty` rather than a bare read: `jq -r` prints an absent field as the four
# characters `null`, which would compare equal to nothing and read as a model.
override_line() {
  local work="$1" model="$2" effort="$3" key over which=""

  over=$(jq -r '.issue.modelOverride // empty' <<<"$work")
  if [ -n "$over" ] && [ "$over" = "$model" ]; then which="model"; fi

  over=$(jq -r '.issue.effortOverride // empty' <<<"$work")
  if [ -n "$over" ] && [ "$over" = "$effort" ]; then which="${which:+${which} and }effort"; fi

  if [ -n "$which" ]; then
    key=$(jq -r '.issue.key' <<<"$work")
    echo "${which} from ${key}, not the playbook"
  fi
}

# The whole instruction: the playbook first, because it says what kind of job
# this is, then the ticket it is a job about. Composed here rather than stored
# whole in the database so that a playbook stays a method - one row that reads
# sensibly for every ticket it will ever be applied to.
compose() {
  local work="$1"
  jq -r '
    .playbook.prompt,
    "",
    "---",
    "",
    "## The ticket",
    "",
    "\(.issue.key)  [\(.issue.type)]  \(.issue.title)",
    "moving:   \(.fromStatus.name) -> \(.toStatus.name)",
    (if .issue.parentKey then "parent:   \(.issue.parentKey)" else empty end),
    (if .issue.readyAt then "ready:    \(.issue.readyAt)" else empty end),
    (if .issue.dueAt then "due:      \(.issue.dueAt)" else empty end),
    "",
    (if (.issue.description | length) > 0 then .issue.description else "_No description. That is itself worth noting on the ticket._" end),
    "",
    (if (.children | length) > 0 then
      "## Its children\n\n" + ([.children[] | "- \(.key)  [\(.type)]  \(.title)"] | join("\n")) + "\n"
     else empty end),
    # Decisions that were asked for and given, on this ticket, before now.
    # Carried into the prompt rather than left for the agent to find in the
    # comment thread, because the one thing a resumed run must not do is reopen
    # a question somebody has already answered.
    (if ([.questions[] | select((.answers | length) > 0)] | length) > 0 then
      "## Decisions already made\n\n" +
      "These were asked on this ticket and answered. They are settled: build on\nthem, and do not ask again.\n\n" +
      ([.questions[] | select((.answers | length) > 0)
        | "**Asked (\(.askedBy)):** \(.body)\n\n"
          + ([.answers[] | "**Answered (\(.author)):** \(.body)"] | join("\n\n"))]
       | join("\n\n---\n\n")) + "\n"
     else empty end),
    "## Reaching Hatch",
    "",
    "Run these from the repository root. The key is already in the environment.",
    "",
    "```",
    "./scripts/hatch.sh show \(.issue.key)              the ticket and its comments",
    "./scripts/hatch.sh start \(.issue.key)             move it to in progress",
    "./scripts/hatch.sh move \(.issue.key) <column>     move it anywhere non-terminal",
    "./scripts/hatch.sh comment \(.issue.key) \"...\"     write on the ticket",
    "./scripts/hatch.sh ask \(.issue.key) \"...\"         ask for a decision, and stop",
    "./scripts/hatch.sh api GET /api/hatch/issues?parentKey=\(.issue.key)",
    "```",
    "",
    "Filing new issues, editing descriptions and setting dates all go through",
    "`api` - CLAUDE.md documents the shapes.",
    "",
    "## When you cannot decide",
    "",
    "Some things are not yours to choose: a product call, a name that will be",
    "lived with for years, a tradeoff with no technically correct side. When you",
    "reach one, do not guess, and do not quietly pick whichever option is easiest",
    "to build.",
    "",
    "```",
    "./scripts/hatch.sh ask \(.issue.key) \"the question, in one sentence\" \\",
    "    --recommend \"The one you would take: what it means, and what it costs\" \\",
    "    --option \"The alternative: what it means, and what it costs\"",
    "```",
    "",
    "**Name the choices.** Nearly every decision worth asking about is a choice",
    "between two or three things you can already name, and each `--option` becomes",
    "something the operator presses - in the browser and at a terminal - rather",
    "than a paragraph they have to read twice and then compose a reply to. So the",
    "body is the question alone, in a sentence; the tradeoffs go inside the options",
    "they belong to; and `--recommend` is the one you would take, of which there",
    "may be one. Ask in prose only when the answer is genuinely open-ended.",
    "",
    "A label is short enough to press and reads as a decision on its own -",
    "\"child-weighted\", not \"we should weight each direct child equally\". It",
    "becomes the answer text itself, and that is what somebody reads six months",
    "later.",
    "",
    "One call per question, so each can be answered on its own. Then stop. An open",
    "question blocks this ticket from being dispatched at all, so nothing further",
    "will be spawned at it until somebody answers - and anything built past an",
    "unanswered question is built on a guess.",
    "",
    "What the repository can answer, answer by reading the repository. A question",
    "the code already settles is a round trip through a person for nothing.",
    "",
    "## Where this increment ends",
    "",
    "\(.issue.key) should be in \"\(.toStatus.name)\" when you stop, and no further.",
    "Only the operator moves work into a terminal column.",
    "",
    "Do not edit playbooks. The API refuses it, and the refusal is deliberate:",
    "an agent that could widen its own instructions and its own budget is a loop",
    "with no end. If a playbook is wrong, say so on the ticket and stop."
  ' <<<"$work"
}

# "There is nothing an agent may move", said properly - and the one place that
# tells a finished board from a jammed one.
#
# Three situations look identical from here and are not the same situation, so
# each is named. Nothing is left on the dispatcher's path at all: the work has
# run out. Everything on it was folded: the board is jammed, and the reasons
# and their counts say on what. And questions are waiting on a person, which is
# true of a subtree rather than of the dispatcher's path and so is said last
# and separately.
#
# The scan is the caller's if it has one - work_pass already made it - and read
# here if not, so `hatch.sh work` on its own says as much as the loop does. A
# read that fails prints the first sentence and no digest: a count that did not
# arrive is not a count, and this is said on the way past something else.
nothing_to_do() {
  local under="${1:-}" queue="${2:-}" waiting=0 scope="" total digest

  if [ -n "$under" ]; then
    echo "hatch: nothing under ${under} is an agent's to move"
  else
    echo "hatch: nothing on the board is an agent's to move"
  fi

  if [ -z "$queue" ]; then
    [ -z "$under" ] || scope="&ancestorKey=${under}"
    queue=$(_get "/api/hatch/work/queue?offsetMinutes=$(offset_minutes)${scope}") || queue=""
  fi

  if [ -n "$queue" ]; then
    total=$(jq 'length' <<<"$queue") || total=""
    case "$total" in ''|*[!0-9]*) total="" ;; esac

    if [ "$total" = 0 ]; then
      # A finished board. Nothing the dispatcher looks at at all, as opposed to
      # a column of cards it looked at and could not take.
      echo "hatch:   nothing is on the dispatcher's path - what is left is in a terminal column"
    elif [ -n "$total" ]; then
      digest=$(queue_digest "$queue")
      if [ -n "$digest" ]; then
        echo "hatch:   ${total} issue(s) were on the dispatcher's path, and every one was folded:"
        printf '%s\n' "$digest"
        echo "hatch:   ./scripts/hatch.sh queue names them one by one"
      fi
    fi
  fi

  # Scoped to match the sentence above it: an evening pointed at one epic is
  # not helped by a count of every question in the house. Counted over the
  # whole subtree rather than over the dispatcher's path, which is a different
  # and still useful number - a question on a card in a terminal column is
  # still a question somebody owes an answer to.
  if [ -n "$under" ]; then
    waiting=$(jq '[.[] | .openQuestions] | add // 0' \
      <<<"$(_get "/api/hatch/issues?ancestorKey=${under}")") || waiting=0
  else
    waiting=$(jq 'length' <<<"$(questions_json '' true)") || waiting=0
  fi

  # A count that did not arrive is not a count. This is said on the way past
  # something else, and a loop must not end on it.
  case "$waiting" in ''|*[!0-9]*) waiting=0 ;; esac
  [ "$waiting" -eq 0 ] || echo "hatch: ${waiting} question(s)${under:+ under ${under}} are waiting on you - ./scripts/hatch.sh answer"
}

# ---- One increment ----

# What one increment answers with.
#
# Bash 3.2 has no way to return a record, and the run happens inside a pipeline
# whose every stage is a subshell, so nothing learned in there survives being
# returned the ordinary way. These are set by run_increment and read by its
# caller before the next one starts, which is how a loop can say what happened
# without asking the board a second time.
INC_KEY=""      # the issue the increment was spent on
INC_FROM=""     # the column it started in
INC_TO=""       # the column the playbook was moving it to
INC_ENDED=""    # the column it is in now, which is the only report that counts
INC_MOVED=0     # whether those last two differ
INC_SESSION=""  # what `claude --resume` takes
INC_COST=""     # dollars, out of the result event
INC_EXIT=0      # what the CLI exited with
INC_ASKED=0     # questions the session opened on its way out
INC_PENDING=0   # set once the increment is under way, cleared once it is counted
INC_FACTS=""    # the file the renderer leaves the session id and the cost in
INC_STALLED=0   # the board says it ended where it started: an increment that did nothing
INC_FLAG=""     # what was done about that, in the words the tally says it in

# The session id and what the run cost, out of the file the renderer wrote them
# to. Also called from the exit trap, for the increment an interrupt landed on:
# a session that was cut short still had a session id and still sent a bill.
read_facts() {
  [ -n "$INC_FACTS" ] || return 0

  if [ -f "$INC_FACTS" ]; then
    INC_SESSION=$(sed -n 's/^session=//p' "$INC_FACTS" | tail -1)
    INC_COST=$(sed -n 's/^cost=//p' "$INC_FACTS" | tail -1)
    rm -f "$INC_FACTS"
  fi
  INC_FACTS=""
}

# ---- The stall guard ----

# An increment that ended with the ticket in the column it found it in, said
# where a person will see it.
#
# This is the one failure mode of an unattended loop that is dangerous rather
# than merely disappointing. Nothing about the board changed, so the next pass
# picks the same issue, spends the same money and fails the same way - and does
# it all night. Every other way an increment can go badly costs one increment.
#
# The flag is an open question, because a question already does all three things
# a flag field would have to be taught: it blocks the issue from being
# dispatched again, it badges the card on the board, and it is the list
# `hatch.sh answer` walks. Answering it clears the flag, which is the right
# gesture - the flag means "nobody has looked at this", and answering is
# somebody having looked.
#
# Written through `comment` and `ask` rather than through a POST spelled out
# here, so a stall reads like every other question: on the issue page, in
# `hatch.sh questions`, and in the next session's "Decisions already made".
#
# The comment and the question are two jobs and only one of them is conditional.
# The comment says which session ran and what it was trying to do, and that is
# worth having whether or not the ticket is already flagged; the question is the
# flag, and a ticket that has one does not need a second.
#
# Neither option is recommended, and that is not modesty. `--recommend` is for a
# choice something knows the answer to, and the whole content of a stall is that
# nothing here knows why it happened.
flag_stall() {
  local open="$1" body

  # Nothing is written on a guess. If the questions could not be read, whether
  # this issue is already flagged is not known, and a second question under the
  # first is one more thing for somebody to answer saying no more than it did.
  if [ -z "$open" ]; then
    INC_FLAG="not flagged - its questions could not be read"
    echo "hatch: ${INC_KEY} moved nothing and could not be flagged - a later pass may offer it again" >&2
    return
  fi

  body="An unattended increment ran here and left this issue where it found it:
still in \"${INC_FROM}\", under a playbook moving ${INC_FROM} -> ${INC_TO}. The
board is the report that counts, and it says nothing happened."

  if [ -n "$INC_SESSION" ]; then
    body="${body}

The session it ran in is still there, with everything it did in context:

    claude --resume ${INC_SESSION}"
  else
    body="${body}

There is no session to resume: the run ended before it said what its id was."
  fi

  [ "$INC_EXIT" = 0 ] || body="${body}

It exited ${INC_EXIT}."

  echo
  if [ "$open" -gt 0 ]; then
    # A question already open on the ticket is this flag, raised - usually the
    # session's own, asked on the way out by something that knew why it was
    # stopping. It blocks the next dispatch and badges the same card, so all
    # that is left to do is write down which session it was.
    body="${body}

There is already a question open here, and that is the flag: nothing further
will be dispatched at this issue until somebody answers it."
    echo "hatch: ${INC_KEY} moved nothing, and is already waiting on a question"
  else
    body="${body}

A question goes up with this comment, so nothing further will be dispatched at
this issue until somebody answers it."
    echo "hatch: ${INC_KEY} moved nothing - flagging it, and going on to the next"
  fi
  echo

  # In a subshell, because every call through `api` ends the process on a
  # refusal and this is the one place where that must not be what takes a
  # night's run down with it: a stall is not a reason to stop, and neither is
  # failing to write one down.
  #
  # The comment goes on either way - it is where the session id lives, and
  # resuming the conversation is most of why a stall is worth recording rather
  # than merely counting. The question goes up only when there is not one there.
  if (
    cmd_comment "$INC_KEY" "$body" &&
    { [ "$open" -gt 0 ] ||
      cmd_ask "$INC_KEY" "An unattended increment left ${INC_KEY} in \"${INC_FROM}\" without moving it - what should happen to it now?" \
        --option "leave it: It waits for you. Nothing is dispatched at it while this question is open, so answer once you have looked - or once you have moved it somewhere the loop does not reach." \
        --option "try again: Spend another increment on the same ticket. The next session is handed this stall, and your answer, among the decisions already made."
    }
  ); then
    if [ "$open" -gt 0 ]; then
      INC_FLAG="waiting on a question"
    else
      INC_FLAG="flagged"
    fi
  else
    INC_FLAG="not flagged - the write was refused"
    echo "hatch: ${INC_KEY} could not be flagged - a later pass may offer it again" >&2
  fi
}

# One increment: the header, the session, and what became of the ticket.
#
# Everything between "here is the dispatch" and "here is what happened", which
# is the part `work` and `go-to-work` have in common. Choosing the ticket stays
# with the callers, because they choose it differently, and so does the last
# word, because one of them is talking to somebody sitting there and the other
# is writing a log nobody will read until morning.
run_increment() {
  local work="$1" model="$2" effort="$3" quiet="${4:-0}"
  local bin root prompt facts before after asked out later open chose
  local -a codes

  INC_KEY=$(jq -r '.issue.key' <<<"$work")
  INC_FROM=$(jq -r '.fromStatus.name' <<<"$work")
  INC_TO=$(jq -r '.toStatus.name // "?"' <<<"$work")
  INC_ENDED="$INC_FROM"
  INC_MOVED=0
  INC_SESSION=""
  INC_COST=""
  INC_EXIT=0
  INC_ASKED=0
  INC_PENDING=0
  INC_FACTS=""
  INC_STALLED=0
  INC_FLAG=""

  bin=$(claude_bin)
  root=$(repo_root)
  prompt=$(compose "$work")

  jq -r '"hatch: \(.issue.key) [\(.issue.type)] \(.issue.title)"' <<<"$work"
  echo "hatch: ${model}, effort ${effort}, ${INC_FROM} -> ${INC_TO}"
  chose=$(override_line "$work" "$model" "$effort")
  [ -z "$chose" ] || echo "hatch:   ${chose}"
  echo

  # What was open before the run, so that what the run asked can be told apart
  # from what was already sitting there. Ids rather than a count: an operator
  # who answered one question in another terminal while this ran would otherwise
  # see the arithmetic come out to zero.
  before=$(jq -c '[.questions[] | select((.answers | length) == 0) | .id]' <<<"$work")

  # Where the renderer leaves the session id and the cost. Under TMPDIR and
  # removed on the way out: it is one run's scratch, and it belongs neither in
  # the repository nor in the next increment.
  facts=$(mktemp "${TMPDIR:-/tmp}/hatch-run.XXXXXX")
  INC_FACTS="$facts"

  # From here an interrupt must not lose the run. Bash holds a pending handler
  # until the spawn it is waiting on returns and then runs it immediately - a
  # statement before anything below could have counted this - so the counting
  # has to be something the trap can finish on its own.
  INC_PENDING=1

  # bypassPermissions because in print mode nothing can answer a prompt: any
  # permission this did not anticipate becomes a silent denial in the middle of
  # a run nobody is watching. That is a deliberate grant, and the reason `work`
  # is a command an operator types rather than something a cron job does.
  #
  # The prompt goes in on stdin rather than as an argument - it is long, and an
  # argument list is the one place where "long" has a limit worth avoiding.
  #
  # `set -e` is off across the spawn, and the exit code is kept rather than let
  # go of. A run that ends badly has already said so through the stream, and the
  # two things worth knowing afterwards - what it asked, and where the ticket
  # ended up - are worth printing either way; but a loop counting three failures
  # in a row needs the number, and this is the only place it exists.
  set +e
  if [ "$quiet" = 1 ]; then
    # No renderer, so the facts come from the CLI's own summary instead: one
    # object at the end carrying the session id, the cost, and the last thing
    # the session said.
    out=$(printf '%s' "$prompt" | (cd "$root" && "$bin" -p \
      --model "$model" \
      --effort "$effort" \
      --permission-mode bypassPermissions \
      --add-dir "$root" \
      --output-format json))
    INC_EXIT=$?
    set -e

    if [ -n "$out" ] && jq -e . >/dev/null 2>&1 <<<"$out"; then
      jq -r '.result // empty' <<<"$out"
      # That object is the event the stream ends with, so the closing lines and
      # the facts are written once rather than twice.
      jq -c . <<<"$out" | render_stream "$root" "$facts"
    elif [ -n "$out" ]; then
      # Not JSON, so it is the CLI complaining. Whatever it said is the only
      # account of the run there is.
      printf '%s\n' "$out"
    fi
  else
    printf '%s' "$prompt" | (cd "$root" && "$bin" -p \
      --model "$model" \
      --effort "$effort" \
      --permission-mode bypassPermissions \
      --add-dir "$root" \
      --output-format stream-json \
      --verbose) | render_stream "$root" "$facts" | watch_stream
    codes=("${PIPESTATUS[@]}")
    set -e
    INC_EXIT=${codes[1]}
  fi

  read_facts

  # Where the ticket actually ended up, asked of the board rather than of the
  # session. An increment that says it did the work and leaves the ticket in the
  # column it found it in did not do the work, and this is the read that can
  # tell the difference. Wrapped in an `if` because a blip here is not worth
  # taking a whole loop down for: the increment already happened.
  if later=$(_get "/api/hatch/work/${INC_KEY}"); then
    INC_ENDED=$(jq -r '.fromStatus.name' <<<"$later")
    if [ "$INC_ENDED" = "$INC_FROM" ]; then INC_STALLED=1; else INC_MOVED=1; fi
  else
    # Not knowing where it ended up is not the same as knowing it went nowhere.
    # A stall is written on the ticket in front of a person, so a read that did
    # not happen must not become one.
    INC_FLAG="where it ended up is not known - the board did not answer"
  fi

  # Whatever the session asked for on its way out. This is the half of the loop
  # that makes asking worth doing: an unattended run's questions are the one
  # thing in its output that somebody has to act on, and they would otherwise be
  # a paragraph in the middle of a transcript nobody scrolls back through.
  open=""
  if after=$(questions_json "$INC_KEY" true); then
    open=$(jq 'length' <<<"$after")
    # The id is lifted out before `index` is asked about it: inside index(),
    # `.` is the array being searched, so `index(.id)` looks for a field on the
    # list rather than the question's own id - and answers with an error, which
    # under `set -e` would end the run at the exact moment a session had asked
    # something worth reading.
    asked=$(jq -c --argjson before "$before" \
      '[.[] | . as $q | select(($before | index($q.id)) == null)]' <<<"$after")
    INC_ASKED=$(jq 'length' <<<"$asked")

    if [ "$INC_ASKED" -gt 0 ]; then
      echo
      echo "--- ${INC_KEY} asked ${INC_ASKED} question(s) ---"
      echo
      print_questions "$asked"
      echo "  ./scripts/hatch.sh answer ${INC_KEY}"
      echo "  $(issue_url "$INC_KEY")"
    fi
  fi

  # And if the board says nothing happened, say so on the ticket. Only for an
  # increment that actually ran against an issue that was actually offered,
  # which is the only thing that reaches this function.
  if [ "$INC_STALLED" = 1 ]; then
    flag_stall "$open"
  fi
}

cmd_work() {
  local key="" under="" model="" effort="" dry=0 attach=0 quiet=0

  while [ $# -gt 0 ]; do
    case "$1" in
      --model)   model="${2:?--model needs a value}"; shift 2 ;;
      --effort)  effort="${2:?--effort needs a value}"; shift 2 ;;
      --under)   under="${2:?--under needs a key}"; shift 2 ;;
      --dry-run) dry=1; shift ;;
      --quiet)   quiet=1; shift ;;
      -i|--interactive) attach=1; shift ;;
      -*)        echo "hatch: work does not take $1" >&2; exit 1 ;;
      *)         key="$1"; shift ;;
    esac
  done

  # Two different asks: a bare key is "this ticket", --under is "whatever is
  # next below this one". Silently preferring either would be a run spent on a
  # ticket nobody named, so both together is a refusal.
  if [ -n "$key" ] && [ -n "$under" ]; then
    echo "hatch: work takes a key or --under, not both - one names the ticket, the other names where to look for it" >&2
    exit 1
  fi

  local work
  if [ -n "$key" ]; then
    work=$(_get "/api/hatch/work/${key}")
  else
    # The same question either way, and the same rule answering it - right to
    # left, top of the column down. --under only narrows the candidates to one
    # subtree, on the server, so an evening can be pointed at one project
    # without this script learning a second notion of "next".
    local scope=""
    [ -z "$under" ] || scope="&ancestorKey=${under}"
    work=$(_get "/api/hatch/work/next?offsetMinutes=$(offset_minutes)${scope}")

    # 204: nothing there is an agent's to advance. Not a failure - it is the
    # answer a finished board gives, and a loop should be able to see it.
    #
    # A finished board, a jammed one and a board full of open questions look
    # identical from here and are three different situations, so each is named.
    # No scan in hand - `work` makes one read, not two - so nothing_to_do takes
    # its own, and this command alone can say which of the three this is.
    if [ -z "$work" ]; then
      nothing_to_do "$under" ""
      exit 2
    fi
  fi

  key=$(jq -r '.issue.key' <<<"$work")

  local blocked
  blocked=$(jq -r '.blocked // empty' <<<"$work")
  if [ -n "$blocked" ]; then
    echo "hatch: ${key} - ${blocked}" >&2

    # A refusal that names a question prints the question. Being told a ticket
    # is blocked and then having to go and ask what by is two round trips for
    # something already in hand.
    local open
    open=$(jq -c '[.questions[] | select((.answers | length) == 0)]' <<<"$work")
    if [ "$(jq 'length' <<<"$open")" -gt 0 ]; then
      echo >&2
      print_questions "$open" >&2
      echo "  ./scripts/hatch.sh answer ${key}" >&2
      echo "  $(issue_url "$key")" >&2
    fi
    exit 2
  fi

  # What the server decided, unless a flag says otherwise. `.playbook.model` is
  # already the effective value - an issue carrying its own model has had it
  # folded in there - so a flag beats an override for free, and typing one is
  # naming a value for this run.
  [ -n "$model" ]  || model=$(jq -r '.playbook.model' <<<"$work")
  [ -n "$effort" ] || effort=$(jq -r '.playbook.effort' <<<"$work")

  local chose
  chose=$(override_line "$work" "$model" "$effort")

  if [ "$dry" = 1 ]; then
    jq -r '"# \(.issue.key) \(.fromStatus.name) -> \(.toStatus.name)"' <<<"$work"
    echo "# model ${model}, effort ${effort}"
    [ -z "$chose" ] || echo "# ${chose}"
    echo
    compose "$work"
    return
  fi

  if [ "$attach" = 1 ]; then
    # Attached: the same ticket, the same playbook, the same budget, in a session
    # somebody is sitting in front of. Two things are deliberately different.
    #
    # No bypassPermissions - there is somebody here to answer a prompt, and the
    # grant run_increment makes exists only because in print mode there is not.
    #
    # And the prompt is an argument rather than stdin, because stdin is the
    # terminal: it is what the operator is about to type into. An argument list
    # has a length limit that the piped form was chosen to avoid, which is a
    # real difference and not one a playbook prompt gets anywhere near.
    local bin root prompt
    bin=$(claude_bin)
    root=$(repo_root)
    prompt=$(compose "$work")

    jq -r '"hatch: \(.issue.key) [\(.issue.type)] \(.issue.title)"' <<<"$work"
    echo "hatch: ${model}, effort ${effort}, $(jq -r '"\(.fromStatus.name) -> \(.toStatus.name)"' <<<"$work")"
    [ -z "$chose" ] || echo "hatch:   ${chose}"
    echo

    (cd "$root" && exec "$bin" \
      --model "$model" \
      --effort "$effort" \
      --add-dir "$root" \
      "$prompt")
    return
  fi

  run_increment "$work" "$model" "$effort" "$quiet"
}

# ---- The loop ----

# One loop at a time on one machine, and this is what says so.
#
# A directory, because mkdir is atomic on every filesystem this could land on
# and a lock file written with `>` is not. Under TMPDIR rather than in the
# repository: a lock in a tracked tree is a lock somebody commits, and a lock
# that outlives a reboot is one somebody has to come and clear by hand.
lock_dir() { echo "${TMPDIR:-/tmp}/hatch-go-to-work.lock"; }

# Whether this process is the one that took it, so that an exit which never got
# the lock cannot remove the lock belonging to the run that did.
LOCK_HELD=0

take_lock() {
  local dir pid
  dir=$(lock_dir)

  if mkdir "$dir" 2>/dev/null; then
    echo $$ >"${dir}/pid"
    LOCK_HELD=1
    return 0
  fi

  # kill -0 asks whether the pid is still there and signals nothing. A lock
  # whose owner is gone - killed outright, or a machine that rebooted out from
  # under it - is not a lock, it is litter, and clearing it is the difference
  # between a loop that survives a crash and one that has to be let back in.
  pid=$(cat "${dir}/pid" 2>/dev/null || true)
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    echo "hatch: a go-to-work is already running here, pid ${pid}." >&2
    echo "hatch: one loop at a time is the whole premise - join that one, or stop it." >&2
    return 1
  fi

  echo "hatch: clearing a stale lock left by pid ${pid:-?}" >&2
  rm -rf "$dir"
  if mkdir "$dir" 2>/dev/null; then
    echo $$ >"${dir}/pid"
    LOCK_HELD=1
    return 0
  fi

  # Two loops that both found the lock stale, and this is the one that lost the
  # race for it. The other is running; that is the right outcome either way.
  echo "hatch: could not take the lock at ${dir}" >&2
  return 1
}

drop_lock() {
  [ "$LOCK_HELD" = 1 ] || return 0
  rm -rf "$(lock_dir)"
  LOCK_HELD=0
}

# Seconds, as somebody reads them off a terminal at midnight.
duration() {
  local s="$1"
  if [ "$s" -ge 3600 ]; then
    printf '%dh%02dm%02ds' $((s / 3600)) $(((s % 3600) / 60)) $((s % 60))
  else
    printf '%dm%02ds' $((s / 60)) $((s % 60))
  fi
}

# The scan the last pass made, for whoever reports what it found.
#
# work_pass runs in the caller's shell, so this survives it the way the INC_
# globals do. A pass with an increment to run prints its own folds - that is
# the moment they are context for something - and a pass with nothing to do
# prints nothing and leaves the scan here, so that the idle path reports it
# exactly once through nothing_to_do rather than twice in two vocabularies.
PASS_QUEUE=""

# One pass of the board: everything it folds past and why, and then the one
# thing it does about the rest.
#
# Two reads where `work` makes one, and the second is the read `work` makes.
# The scan says what was skipped, which is the only thing an unattended run
# leaves behind that somebody has to act on; `work/next` says what to do, which
# is what keeps an increment here the same increment as an increment there.
# They are the same walk on the server, so the second cannot land on something
# the first called blocked.
#
#   0  an increment ran, and the INC_ globals say how it went
#   1  nothing on the board is an agent's to move
#   2  the board could not be read - a reason to wait, not a reason to stop. A
#      loop that ends on one bad minute of network is a loop somebody has to sit
#      with, which is the thing being built away from here.
work_pass() {
  local under="$1" quiet="$2" scope="" queue clear work model effort digest folded

  [ -z "$under" ] || scope="&ancestorKey=${under}"

  queue=$(_get "/api/hatch/work/queue?offsetMinutes=$(offset_minutes)${scope}") || return 2
  clear=$(jq -r 'map(select(.blocked == null)) | first | .issue.key // empty' <<<"$queue")

  PASS_QUEUE="$queue"

  # Nothing to run: say nothing here, and let the idle path say the whole thing
  # once. A digest printed here and a digest printed there is the same board
  # reported twice a minute until morning.
  [ -n "$clear" ] || return 1

  work=$(_get "/api/hatch/work/next?offsetMinutes=$(offset_minutes)${scope}") || return 2

  # The board moved between the two reads - somebody answered a question, or
  # something landed. Not an error, and not worth a sentence: the next pass
  # asks again. The scan goes with it, because it describes a board that no
  # longer exists: an idle report built from it would say every candidate was
  # folded while naming one that was not.
  if [ -z "$work" ]; then
    PASS_QUEUE=""
    return 1
  fi

  # An increment is about to run, so what the pass walked past on the way to it
  # is context rather than noise - counted rather than listed, because a column
  # of two hundred cards folded for four reasons is four facts.
  digest=$(queue_digest "$queue")
  if [ -n "$digest" ]; then
    folded=$(jq '[ .[] | select(.blocked) ] | length' <<<"$queue")
    echo "hatch:   folded past ${folded} issue(s) on the way here:"
    printf '%s\n' "$digest"
  fi

  # No flag reaches this, and that is still deliberate: `work --model` is one
  # operator's opinion about one increment, and a loop that carried it across a
  # night would be applying it to tickets nobody looked at.
  #
  # What the loop does carry is the ticket's own model and effort, because those
  # are a fact recorded on an issue somebody read - which is precisely the thing
  # the flag was a poor substitute for. They arrive folded into `.playbook`
  # already, so there is nothing to ask for here.
  model=$(jq -r '.playbook.model' <<<"$work")
  effort=$(jq -r '.playbook.effort' <<<"$work")

  echo
  run_increment "$work" "$model" "$effort" "$quiet"
}

# ---- Ending, and saying what happened ----

# Everything the loop has to stop for, and none of them set by default. An
# unattended run that stopped for a reason nobody asked for would be a run
# somebody has to check on, which is the thing being built away from.
MAX_RUNS=""    # --max-runs N
MAX_SPEND=""   # --max-spend USD
UNTIL=""       # --until HH:MM, as typed
UNTIL_AT=""    # ...and as an instant
STOP_FILE=""   # --stop-file PATH

# The tally, accumulated as the loop goes rather than reconstructed at the end -
# because an interrupted run has to be able to print it too, and an interrupted
# run is exactly the one with nothing left to reconstruct from.
STARTED=0
RUNS=0
SPENT=0
MOVED_LINES=""
STALLED_LINES=""
FAILS=0        # increments that exited non-zero, in a row
FAIL_KEYS=""   # ...and which
STOP_WHY=""    # the sentence naming what ended the run

# Dollars, the way a bill is written.
money() { awk -v v="$1" 'BEGIN { printf "%.2f", v }'; }

# HH:MM today, or tomorrow if that hour has already gone by - somebody who says
# `--until 06:00` at eleven at night means the morning, and a loop that read it
# as "seventeen hours ago" would stop before it started. BSD date first because
# this file is Bash 3.2 for macOS's sake; GNU date behind it, for everywhere
# else.
at_clock() {
  local hhmm="$1" today ts
  today=$(date +%Y-%m-%d)
  ts=$(date -j -f '%Y-%m-%d %H:%M' "${today} ${hhmm}" +%s 2>/dev/null) ||
    ts=$(date -d "${today} ${hhmm}" +%s 2>/dev/null) ||
    return 1
  [ "$ts" -gt "$(date +%s)" ] || ts=$((ts + 86400))
  echo "$ts"
}

# Whether this run is over, and why - asked before every increment and through
# every wait. One function, because "every exit path" is not something several
# call sites can promise between them.
should_stop() {
  STOP_WHY=""

  # First, because it is the one that says something is wrong rather than
  # something is finished.
  if [ "$FAILS" -ge 3 ]; then
    STOP_WHY="three increments in a row failed: ${FAIL_KEYS}"
    return 0
  fi

  # A file, so that stopping a loop needs nothing but a shell and a path - no
  # pid to find, no signal to send, and nothing that could land in the middle
  # of a push. The increment in flight finishes first; this is only ever read
  # between them.
  if [ -n "$STOP_FILE" ] && [ -e "$STOP_FILE" ]; then
    STOP_WHY="${STOP_FILE} exists"
    return 0
  fi

  if [ -n "$MAX_RUNS" ] && [ "$RUNS" -ge "$MAX_RUNS" ]; then
    STOP_WHY="--max-runs ${MAX_RUNS} reached"
    return 0
  fi

  if [ -n "$MAX_SPEND" ] && awk -v s="$SPENT" -v m="$MAX_SPEND" 'BEGIN { exit !(s + 0 >= m + 0) }'; then
    STOP_WHY="--max-spend ${MAX_SPEND} reached at \$$(money "$SPENT")"
    return 0
  fi

  if [ -n "$UNTIL_AT" ] && [ "$(date +%s)" -ge "$UNTIL_AT" ]; then
    STOP_WHY="--until ${UNTIL} has come"
    return 0
  fi

  return 1
}

# Waiting, in slices, so that a stop file dropped during a wait is noticed then
# rather than an interval later, and an --until at three in the morning lands at
# three in the morning however long the interval is.
nap() {
  local left="$1" slice
  while [ "$left" -gt 0 ]; do
    slice=5
    [ "$left" -ge 5 ] || slice="$left"
    sleep "$slice"
    left=$((left - slice))
    ! should_stop || return 1
  done
  return 0
}

# What became of the ticket, in the phrase both the running commentary and the
# tally say it in - so a line read at midnight and a line read in the morning
# cannot come to disagree about the same increment.
increment_outcome() {
  if [ "$INC_MOVED" = 1 ]; then
    echo "${INC_FROM} -> ${INC_ENDED}"
  elif [ "$INC_STALLED" = 1 ]; then
    echo "still in \"${INC_ENDED}\"${INC_FLAG:+, ${INC_FLAG}}"
  else
    echo "${INC_FLAG:-where it ended up is not known}"
  fi
}

# One increment, added to the run's account.
#
# Called by the loop when an increment finishes, and by the exit trap when an
# interrupt landed between the two - bash runs a pending handler the moment the
# spawn it was waiting on returns, which is one statement before the loop gets
# to count it.
record_increment() {
  [ "$INC_PENDING" = 1 ] || return 0
  INC_PENDING=0
  read_facts

  RUNS=$((RUNS + 1))
  [ -z "$INC_COST" ] || SPENT=$(awk -v a="$SPENT" -v b="$INC_COST" 'BEGIN { printf "%.6f", a + b }')

  # Two lists rather than one, because they are two different mornings: the
  # moved ones are what the night got done, and the stalled ones are what is
  # waiting on somebody - each of them flagged with a question that says so.
  if [ "$INC_MOVED" = 1 ]; then
    MOVED_LINES="${MOVED_LINES}hatch:   moved    ${INC_KEY}  $(increment_outcome)
"
  else
    STALLED_LINES="${STALLED_LINES}hatch:   stalled  ${INC_KEY}  $(increment_outcome)
"
  fi

  # A failed increment is not a reason to stop - a ticket can be wrong, or a
  # test can be flaky, and the next ticket is a different question. Three in a
  # row is something else: whatever is broken is broken for every ticket, and
  # the loop is now spending money to prove it.
  if [ "$INC_EXIT" -ne 0 ]; then
    FAILS=$((FAILS + 1))
    FAIL_KEYS="${FAIL_KEYS}${FAIL_KEYS:+, }${INC_KEY} (exit ${INC_EXIT})"
  else
    FAILS=0
    FAIL_KEYS=""
  fi
}

# What the run came to. Printed from the EXIT trap and nowhere else, because
# the reasons a loop ends include the ones nobody wrote code for - an interrupt,
# a failure, a terminal closing - and those are precisely the runs whose tally
# somebody needs.
print_tally() {
  local elapsed
  elapsed=$(( $(date +%s) - STARTED ))

  echo
  [ -z "$STOP_WHY" ] || echo "hatch: ${STOP_WHY}"
  echo "hatch: ${RUNS} increment(s) in $(duration "$elapsed"), \$$(money "$SPENT")"
  [ -z "$MOVED_LINES" ]   || printf '%s' "$MOVED_LINES"
  [ -z "$STALLED_LINES" ] || printf '%s' "$STALLED_LINES"
}

go_to_work_ends() {
  drop_lock
  record_increment
  print_tally
}

# The command this epic is named for: pick the next actionable issue, spend one
# increment on it, and do it again.
#
# The loop is the shell and not the model. A fresh context per ticket is
# cheaper, and a session that has been running for six hours is one whose
# earliest decisions nobody can audit.
cmd_go_to_work() {
  local key="" under="" interval=60 once=0 quiet=0 outcome idle_since=0 idle_said=0 now
  local idle_digest="" digest

  while [ $# -gt 0 ]; do
    case "$1" in
      --under)      under="${2:?--under needs a key}"; shift 2 ;;
      --interval)   interval="${2:?--interval needs a number of seconds}"; shift 2 ;;
      --once)       once=1; shift ;;
      --quiet)      quiet=1; shift ;;
      --max-runs)   MAX_RUNS="${2:?--max-runs needs a count}"; shift 2 ;;
      --max-spend)  MAX_SPEND="${2:?--max-spend needs an amount in dollars}"; shift 2 ;;
      --until)      UNTIL="${2:?--until needs HH:MM}"; shift 2 ;;
      --stop-file)  STOP_FILE="${2:?--stop-file needs a path}"; shift 2 ;;
      -*)           echo "hatch: go-to-work does not take $1" >&2; exit 1 ;;
      *)            key="$1"; shift ;;
    esac
  done

  # A key with --under is the refusal `work` makes, for the reason `work` makes
  # it. A key on its own is a refusal too, and a different one: this command's
  # question is "what is next", asked again and again, and one ticket cannot be
  # the answer to it twice.
  if [ -n "$key" ] && [ -n "$under" ]; then
    echo "hatch: go-to-work takes --under or a key, not both - one names where to look, the other names the ticket" >&2
    exit 1
  fi
  if [ -n "$key" ]; then
    echo "hatch: go-to-work does not take a ticket - it asks the board what is next, until there is nothing." >&2
    echo "hatch: one increment on ${key} is  ./scripts/hatch.sh work ${key}" >&2
    exit 1
  fi

  # Zero is refused rather than clamped: it reads as "as fast as possible" and
  # means a board asked the same question thousands of times a minute.
  case "$interval" in ''|*[!0-9]*|0) echo "hatch: --interval takes a number of seconds, at least one" >&2; exit 1 ;; esac
  [ -z "$MAX_RUNS" ] || case "$MAX_RUNS" in ''|*[!0-9]*) echo "hatch: --max-runs takes a count" >&2; exit 1 ;; esac
  [ -z "$MAX_SPEND" ] || case "$MAX_SPEND" in ''|*[!0-9.]*) echo "hatch: --max-spend takes an amount in dollars" >&2; exit 1 ;; esac

  # Read before anything is spawned. A wrong clock discovered at the end of the
  # night is a stop condition that never applied.
  if [ -n "$UNTIL" ]; then
    UNTIL_AT=$(at_clock "$UNTIL") || { echo "hatch: --until takes a wall-clock time, as HH:MM" >&2; exit 1; }
  fi

  # A stop file that is already there would end the loop before its first
  # increment, silently, and look exactly like a board with nothing on it.
  if [ -n "$STOP_FILE" ] && [ -e "$STOP_FILE" ]; then
    echo "hatch: ${STOP_FILE} already exists - that is the stop signal, so nothing would run. Remove it, or name another path." >&2
    exit 1
  fi

  take_lock || exit 1

  STARTED=$(date +%s)

  # Every way out through one door. INT and TERM are named rather than left to
  # bash, because a signal the shell does not trap kills it outright - and the
  # run that ends in an interrupt is the one whose tally is most worth having.
  trap 'go_to_work_ends' EXIT
  trap 'STOP_WHY="interrupted"; exit 130' INT
  trap 'STOP_WHY="terminated"; exit 143' TERM

  while :; do
    if should_stop; then break; fi

    outcome=0
    work_pass "$under" "$quiet" || outcome=$?

    case "$outcome" in
      0)
        idle_since=0
        record_increment

        echo
        if [ "$INC_MOVED" = 1 ]; then
          echo "hatch: ${INC_KEY} moved, $(increment_outcome)  (${RUNS} increment(s), \$$(money "$SPENT"))"
        else
          echo "hatch: ${INC_KEY} did not move - $(increment_outcome)  (${RUNS} increment(s), \$$(money "$SPENT"))"
        fi
        ;;

      1)
        # Said in full the first time, again whenever the answer changes, and
        # otherwise rarely. An idle loop is a thing somebody left running; it
        # should be able to say it is alive without filling a scrollback with
        # the same sentence six hundred times - and the one pass whose reasons
        # changed, because somebody answered a question at three in the
        # morning, is exactly the one nobody would find in that.
        now=$(date +%s)
        digest=$(queue_digest "$PASS_QUEUE" | cksum)
        if [ "$idle_since" = 0 ] || [ "$digest" != "$idle_digest" ]; then
          idle_said=$now
          idle_digest="$digest"
          nothing_to_do "$under" "$PASS_QUEUE"

          # What the loop is going to do about it, said once. A reprint later
          # is a report about the board having changed, not a fresh
          # explanation of the interval.
          if [ "$idle_since" = 0 ]; then
            idle_since=$now
            [ "$once" = 1 ] || echo "hatch: waiting, and asking again every ${interval}s"
          fi
        elif [ $((now - idle_said)) -ge 600 ]; then
          idle_said=$now
          echo "hatch: still nothing an agent may move, $(duration $((now - idle_since))) now"
        fi
        ;;

      *)
        # api() already said what went wrong, in a sentence. This says what is
        # going to happen about it.
        echo "hatch: the board did not answer - asking again in ${interval}s" >&2
        ;;
    esac

    # `--once` is the loop's own dry run against a board that is not a fixture:
    # one pass, whatever it found, and out.
    [ "$once" = 0 ] || { STOP_WHY="--once, and the pass is done"; break; }

    # An increment that ran is followed by the next one immediately. The
    # interval is what to do when there was nothing to do.
    [ "$outcome" = 0 ] || nap "$interval" || break
  done
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
  board|next|queue|show|start|move|comment|pr|ask|questions|answer|work|go-to-work|api) require_env ;;
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
  ask)       shift; cmd_ask "$@" ;;
  questions) shift; cmd_questions "$@" ;;
  answer)    shift; cmd_answer "$@" ;;
  work)    shift; cmd_work "$@" ;;
  go-to-work) shift; cmd_go_to_work "$@" ;;
  api)     shift; api "${1:?method}" "/${2#/}" "${3:-}" ;;
  ''|-h|--help|help) usage 0 ;;
  *) echo "hatch: no such command \"$1\"" >&2; usage 1 ;;
esac
