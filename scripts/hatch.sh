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
render_stream() {
  # Only the events. The CLI is entitled to say something on stdout that is not
  # one - a warning, an update notice - and jq exits on the first thing it
  # cannot parse, which would take the whole log down with it for a line nobody
  # needed. Line-buffered so the filter does not become the stall it exists to
  # prevent.
  grep --line-buffered '^{' | jq -n -r --unbuffered --arg root "${1:-}" '
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
        .out = "hatch: session \($e.session_id)\n" + resume($e.session_id) + "\n"

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
        .out = "\nhatch: "
          + (if $e.is_error then "ended with an error" else "done" end)
          + " in \(($e.duration_ms / 1000 | floor) | clock), \($e.num_turns) turns"
          + (if $e.total_cost_usd then ", $\(($e.total_cost_usd * 100 | round) / 100)" else "" end)
          + "\n" + resume($e.session_id)

      else .out = null end;

      select(.out != null and .out != "") | .out)
  '
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
    # "Nothing to do" and "everything is waiting on you" look identical from
    # here and are not the same situation, so the second one is named. Without
    # this an operator with a board full of open questions would be told the
    # work had run out.
    if [ -z "$work" ]; then
      local waiting
      if [ -n "$under" ]; then
        echo "hatch: nothing under ${under} is an agent's to move"
        # Scoped to match the sentence above it: an evening pointed at one epic
        # is not helped by a count of every question in the house.
        waiting=$(jq '[.[] | .openQuestions] | add // 0' \
          <<<"$(_get "/api/hatch/issues?ancestorKey=${under}")")
        [ "$waiting" -eq 0 ] || echo "hatch: ${waiting} question(s) under ${under} are waiting on you - ./scripts/hatch.sh answer"
      else
        echo "hatch: nothing on the board is an agent's to move"
        waiting=$(jq 'length' <<<"$(questions_json '' true)")
        [ "$waiting" -eq 0 ] || echo "hatch: ${waiting} question(s) are waiting on you - ./scripts/hatch.sh answer"
      fi
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

  if [ "$attach" = 1 ]; then
    # Attached: the same ticket, the same playbook, the same budget, in a session
    # somebody is sitting in front of. Two things are deliberately different.
    #
    # No bypassPermissions - there is somebody here to answer a prompt, and the
    # grant below exists only because in print mode there is not.
    #
    # And the prompt is an argument rather than stdin, because stdin is the
    # terminal: it is what the operator is about to type into. An argument list
    # has a length limit that the piped form was chosen to avoid, which is a
    # real difference and not one a playbook prompt gets anywhere near.
    (cd "$root" && exec "$bin" \
      --model "$model" \
      --effort "$effort" \
      --add-dir "$root" \
      "$prompt")
    return
  fi

  # What was open before the run, so that what the run asked can be told apart
  # from what was already sitting there. Ids rather than a count: an operator
  # who answered one question in another terminal while this ran would otherwise
  # see the arithmetic come out to zero.
  local before after asked
  before=$(jq -c '[.questions[] | select((.answers | length) == 0) | .id]' <<<"$work")

  # bypassPermissions because in print mode nothing can answer a prompt: any
  # permission this did not anticipate becomes a silent denial in the middle of
  # a run nobody is watching. That is a deliberate grant, and the reason `work`
  # is a command an operator types rather than something a cron job does.
  #
  # The prompt goes in on stdin rather than as an argument - it is long, and an
  # argument list is the one place where "long" has a limit worth avoiding.
  #
  # The exit code is let go of deliberately. A run that ends badly has already
  # said so through the stream, in a sentence, and the two things worth knowing
  # afterwards - what it asked, and where the ticket ended up - are worth
  # printing either way. Losing them to `set -e` on a non-zero exit would throw
  # away the part of the output somebody has to act on.
  if [ "$quiet" = 1 ]; then
    printf '%s' "$prompt" | (cd "$root" && "$bin" -p \
      --model "$model" \
      --effort "$effort" \
      --permission-mode bypassPermissions \
      --add-dir "$root") || true
  else
    printf '%s' "$prompt" | (cd "$root" && "$bin" -p \
      --model "$model" \
      --effort "$effort" \
      --permission-mode bypassPermissions \
      --add-dir "$root" \
      --output-format stream-json \
      --verbose) | render_stream "$root" | watch_stream || true
  fi

  # Whatever the session asked for on its way out. This is the half of the loop
  # that makes asking worth doing: an unattended run's questions are the one
  # thing in its output that somebody has to act on, and they would otherwise be
  # a paragraph in the middle of a transcript nobody scrolls back through.
  after=$(questions_json "$key" true)
  asked=$(jq -c --argjson before "$before" \
    '[.[] | select(($before | index(.id)) == null)]' <<<"$after")

  if [ "$(jq 'length' <<<"$asked")" -gt 0 ]; then
    echo
    echo "--- ${key} asked $(jq 'length' <<<"$asked") question(s) ---"
    echo
    print_questions "$asked"
    echo "  ./scripts/hatch.sh answer ${key}"
    echo "  $(issue_url "$key")"
  fi
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
  board|next|show|start|move|comment|ask|questions|answer|work|api) require_env ;;
esac

case "${1:-}" in
  config)  shift; cmd_config "$@" ;;
  board)   shift; cmd_board "$@" ;;
  next)    shift; cmd_next "$@" ;;
  show)    shift; cmd_show "$@" ;;
  start)   shift; cmd_move "${1:?usage: hatch.sh start AER-12}" "in progress" ;;
  move)    shift; cmd_move "$@" ;;
  comment) shift; cmd_comment "$@" ;;
  ask)       shift; cmd_ask "$@" ;;
  questions) shift; cmd_questions "$@" ;;
  answer)    shift; cmd_answer "$@" ;;
  work)    shift; cmd_work "$@" ;;
  api)     shift; api "${1:?method}" "/${2#/}" "${3:-}" ;;
  ''|-h|--help|help) usage 0 ;;
  *) echo "hatch: no such command \"$1\"" >&2; usage 1 ;;
esac
