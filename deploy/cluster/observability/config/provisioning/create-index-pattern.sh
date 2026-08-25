#!/bin/sh
# One-shot: creates the aerie-logs-* index pattern in OpenSearch Dashboards
# (time field: time), so it doesn't need to be clicked through in the UI.
# Copied unedited from the old compose stack's
# containers/opensearch-provision/create-index-pattern.sh, deleted with the
# rest of that path in 7b.9 and reachable in git history - see
# ./kustomization.yaml for why this is a real file rather than a string
# embedded in ./opensearch-provision.yaml.
#
# Idempotent via the saved objects API's overwrite=true: every run re-applies
# the same definition instead of 409ing on an existing one, so re-runs are
# safe no-ops.
#
# Posting straight to the saved objects API (as this used to do) creates the
# index-pattern *without* its "fields" cache, which is normally populated by
# the Dashboards index-pattern wizard. Without that cache the in-browser
# IndexPattern has no fields until someone manually clicks "Refresh field
# list" in Stack Management, and Discover fails with "Could not locate that
# index-pattern-field (id: time)" when it tries to build the histogram
# aggregation. So fetch the live field list first and embed it ourselves,
# matching what the wizard does. This also means we must wait until at least
# one aerie-logs-* index exists (fluent-bit has shipped a log) before the
# "time" field is actually discoverable.
set -eu

# No `${DASHBOARDS_URL:-...}` fallback - Flux's postBuild envsubst rewrites
# that form inside a configMapGenerator body, as ./apply-ism-policy.sh explains
# at length. This one was only ever harmless by luck: the default it collapsed
# to, `http://opensearch-dashboards:5601`, happens to be the right Service
# here, so this script alone would have kept working while its two siblings
# silently talked to a host that does not resolve. Removed anyway - the next
# person to copy this line should not inherit a landmine that is currently
# defused.
INDEX_PATTERN_ID="aerie-logs"
INDEX_PATTERN_TITLE="aerie-logs-*"
MAX_ATTEMPTS=30
RETRY_DELAY_SECONDS=5

attempt=1
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  fields_status=$(curl -s -o /tmp/fields-response.json -w '%{http_code}' \
    "$DASHBOARDS_URL/api/index_patterns/_fields_for_wildcard?pattern=$INDEX_PATTERN_TITLE&meta_fields=%5B%22_source%22%2C%22_id%22%2C%22_type%22%2C%22_index%22%2C%22_score%22%5D" || echo 000)

  if [ "$fields_status" = "200" ] && grep -q '"name":"time"' /tmp/fields-response.json; then
    break
  fi

  echo "[$attempt/$MAX_ATTEMPTS] 'time' field not indexed yet or Dashboards not ready (HTTP $fields_status)"
  attempt=$((attempt + 1))
  sleep "$RETRY_DELAY_SECONDS"
done

if [ "$attempt" -gt "$MAX_ATTEMPTS" ]; then
  echo "giving up waiting for the 'time' field after $MAX_ATTEMPTS attempts"
  exit 1
fi

# The saved object's "fields" attribute is a JSON string, not a nested
# object, so re-embed the fetched array as an escaped string.
FIELDS_JSON=$(sed 's/^{"fields"://; s/}$//' /tmp/fields-response.json)
FIELDS_ESCAPED=$(printf '%s' "$FIELDS_JSON" | sed 's/\\/\\\\/g; s/"/\\"/g')

# Written to a file rather than held in a variable for `-d "$BODY"`, and
# that is the whole reason this file exists rather than the obvious inline
# form. Linux caps a *single* argv entry at MAX_ARG_STRLEN - 32 pages,
# 131072 bytes - independently of the far larger total ARG_MAX that `getconf
# ARG_MAX` reports, and the escaped field cache blows through it: 138043
# bytes across 764 fields on 2026-08-25. Past that line execve() returns
# E2BIG, curl never starts, and because the `|| echo 000` below catches a
# command substitution that produced nothing, the loop reports `HTTP 000` and
# blames Dashboards for what is a local exec failure - the shell's own
# `curl: Argument list too long` being the only true word in the output.
#
# This grew into the failure rather than arriving with it. The cache is one
# entry per field fluent-bit has ever shipped, so it only crosses 131072
# bytes after enough distinct fields accumulate; the script worked for
# months and then began failing every run without anything about it
# changing. `-d @file` keeps argv a constant few hundred bytes no matter how
# many fields the cluster reaches, which is the property worth having here -
# a larger buffer would just move the same cliff further out.
BODY_FILE=/tmp/index-pattern-body.json
cat > "$BODY_FILE" <<JSON
{
  "attributes": {
    "title": "$INDEX_PATTERN_TITLE",
    "timeFieldName": "time",
    "fields": "$FIELDS_ESCAPED"
  }
}
JSON

attempt=1
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  status=$(curl -s -o /tmp/index-pattern-response.json -w '%{http_code}' -X POST \
    "$DASHBOARDS_URL/api/saved_objects/index-pattern/$INDEX_PATTERN_ID?overwrite=true" \
    -H 'Content-Type: application/json' -H 'osd-xsrf: true' -d @"$BODY_FILE" || echo 000)

  if [ "$status" = "200" ]; then
    echo "created/updated index pattern '$INDEX_PATTERN_ID' (aerie-logs-*, time field: time)"
    exit 0
  fi

  # "HTTP 000" means curl produced no status line at all - it could not run,
  # could not connect, or died mid-request - which is a different thing from
  # Dashboards answering with an error, and reads as a readiness problem if
  # the message does not say so.
  echo "[$attempt/$MAX_ATTEMPTS] index-pattern POST failed (HTTP $status; 000 = request never completed): $(cat /tmp/index-pattern-response.json 2>/dev/null || true)"
  attempt=$((attempt + 1))
  sleep "$RETRY_DELAY_SECONDS"
done

echo "giving up after $MAX_ATTEMPTS attempts"
exit 1
