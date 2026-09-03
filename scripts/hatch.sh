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
# Two environment variables, both required, neither in this repo:
#
#   AERIE_BASE       https://hatch.<your domain>   - or a local dev origin
#   AERIE_HATCH_KEY  aerie_ak_...                  - minted on the admin app's
#                                                    API keys page, shown once
#
# Put them in your shell profile or a secrets file you source. Never here:
# Aerie ships to other operators, and a key in the artifact is one operator's
# key inherited by everybody who clones it.
#
# Usage:
#   ./hatch.sh board                  # the columns, and how many cards in each
#   ./hatch.sh next                   # top workable card of "todo"
#   ./hatch.sh next "in progress"     # ...or of any column
#   ./hatch.sh show AER-12            # the brief, plus its comments
#   ./hatch.sh start AER-12           # move it to "in progress"
#   ./hatch.sh move AER-12 todo       # ...or to any non-terminal column
#   ./hatch.sh comment AER-12 "sha abc123 on branch aer-12-thing"
#   ./hatch.sh api GET /api/hatch/issues?statusId=2
#   ./hatch.sh api PATCH /api/hatch/issues/AER-12 '{"dueAt":"2026-10-01"}'
#
# Bash 3.2 compatible on purpose - that is what macOS still ships as /bin/bash.

set -euo pipefail

command -v jq >/dev/null || { echo "hatch: needs jq" >&2; exit 1; }

: "${AERIE_BASE:?set AERIE_BASE to your Hatch origin - see the header of this file}"
: "${AERIE_HATCH_KEY:?set AERIE_HATCH_KEY to an aerie_ak_ key - see the header of this file}"

base="${AERIE_BASE%/}"

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

usage() {
  sed -n '/^# Usage:/,/^#$/p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

case "${1:-}" in
  board)   shift; cmd_board "$@" ;;
  next)    shift; cmd_next "$@" ;;
  show)    shift; cmd_show "$@" ;;
  start)   shift; cmd_move "${1:?usage: hatch.sh start AER-12}" "in progress" ;;
  move)    shift; cmd_move "$@" ;;
  comment) shift; cmd_comment "$@" ;;
  api)     shift; api "${1:?method}" "/${2#/}" "${3:-}" ;;
  ''|-h|--help|help) usage 0 ;;
  *) echo "hatch: no such command \"$1\"" >&2; usage 1 ;;
esac
