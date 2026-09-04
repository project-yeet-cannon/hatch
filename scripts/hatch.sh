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
#   ./hatch.sh show AER-12            # the brief, plus its comments
#   ./hatch.sh start AER-12           # move it to "in progress"
#   ./hatch.sh move AER-12 todo       # ...or to any non-terminal column
#   ./hatch.sh comment AER-12 "sha abc123 on branch aer-12-thing"
#   ./hatch.sh work                   # one increment on the next thing due
#   ./hatch.sh work AER-12            # ...or on this one
#   ./hatch.sh work --model opus --effort xhigh AER-12
#   ./hatch.sh work --dry-run         # print the prompt, spawn nothing
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
    "## Reaching Hatch",
    "",
    "Run these from the repository root. The key is already in the environment.",
    "",
    "```",
    "./scripts/hatch.sh show \(.issue.key)              the ticket and its comments",
    "./scripts/hatch.sh start \(.issue.key)             move it to in progress",
    "./scripts/hatch.sh move \(.issue.key) <column>     move it anywhere non-terminal",
    "./scripts/hatch.sh comment \(.issue.key) \"...\"    write on the ticket",
    "./scripts/hatch.sh api GET /api/hatch/issues?parentKey=\(.issue.key)",
    "```",
    "",
    "Filing new issues, editing descriptions and setting dates all go through",
    "`api` - CLAUDE.md documents the shapes.",
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

cmd_work() {
  local key="" model="" effort="" dry=0

  while [ $# -gt 0 ]; do
    case "$1" in
      --model)   model="${2:?--model needs a value}"; shift 2 ;;
      --effort)  effort="${2:?--effort needs a value}"; shift 2 ;;
      --dry-run) dry=1; shift ;;
      -*)        echo "hatch: work does not take $1" >&2; exit 1 ;;
      *)         key="$1"; shift ;;
    esac
  done

  local work
  if [ -n "$key" ]; then
    work=$(_get "/api/hatch/work/${key}")
  else
    work=$(_get "/api/hatch/work/next?offsetMinutes=$(offset_minutes)")
    # 204: the board holds nothing an agent may advance. Not a failure - it is
    # the answer a finished board gives, and a loop should be able to see it.
    [ -n "$work" ] || { echo "hatch: nothing on the board is an agent's to move"; exit 2; }
  fi

  local blocked
  blocked=$(jq -r '.blocked // empty' <<<"$work")
  [ -z "$blocked" ] || {
    echo "hatch: $(jq -r '.issue.key' <<<"$work") - ${blocked}" >&2
    exit 2
  }

  # The playbook chooses; the flags override. Nothing here writes back, so an
  # override is one run's opinion and not a change to the matrix.
  [ -n "$model" ]  || model=$(jq -r '.playbook.model' <<<"$work")
  [ -n "$effort" ] || effort=$(jq -r '.playbook.effort' <<<"$work")

  local prompt
  prompt=$(compose "$work")

  if [ "$dry" = 1 ]; then
    jq -r '"# \(.issue.key) \(.fromStatus.name) -> \(.toStatus.name)"' <<<"$work"
    echo "# model ${model}, effort ${effort}"
    echo
    printf '%s\n' "$prompt"
    return
  fi

  local bin root
  bin=$(claude_bin)
  root=$(repo_root)

  jq -r '"hatch: \(.issue.key) [\(.issue.type)] \(.issue.title)"' <<<"$work"
  echo "hatch: ${model}, effort ${effort}, $(jq -r '"\(.fromStatus.name) -> \(.toStatus.name)"' <<<"$work")"
  echo

  # bypassPermissions because in print mode nothing can answer a prompt: any
  # permission this did not anticipate becomes a silent denial in the middle of
  # a run nobody is watching. That is a deliberate grant, and the reason `work`
  # is a command an operator types rather than something a cron job does.
  #
  # The prompt goes in on stdin rather than as an argument - it is long, and an
  # argument list is the one place where "long" has a limit worth avoiding.
  printf '%s' "$prompt" | (cd "$root" && "$bin" -p \
    --model "$model" \
    --effort "$effort" \
    --permission-mode bypassPermissions \
    --add-dir "$root")
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
  board|next|show|start|move|comment|work|api) require_env ;;
esac

case "${1:-}" in
  config)  shift; cmd_config "$@" ;;
  board)   shift; cmd_board "$@" ;;
  next)    shift; cmd_next "$@" ;;
  show)    shift; cmd_show "$@" ;;
  start)   shift; cmd_move "${1:?usage: hatch.sh start AER-12}" "in progress" ;;
  move)    shift; cmd_move "$@" ;;
  comment) shift; cmd_comment "$@" ;;
  work)    shift; cmd_work "$@" ;;
  api)     shift; api "${1:?method}" "/${2#/}" "${3:-}" ;;
  ''|-h|--help|help) usage 0 ;;
  *) echo "hatch: no such command \"$1\"" >&2; usage 1 ;;
esac
