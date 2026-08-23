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

BODY=$(cat <<JSON
{
  "attributes": {
    "title": "$INDEX_PATTERN_TITLE",
    "timeFieldName": "time",
    "fields": "$FIELDS_ESCAPED"
  }
}
JSON
)

attempt=1
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  status=$(curl -s -o /tmp/index-pattern-response.json -w '%{http_code}' -X POST \
    "$DASHBOARDS_URL/api/saved_objects/index-pattern/$INDEX_PATTERN_ID?overwrite=true" \
    -H 'Content-Type: application/json' -H 'osd-xsrf: true' -d "$BODY" || echo 000)

  if [ "$status" = "200" ]; then
    echo "created/updated index pattern '$INDEX_PATTERN_ID' (aerie-logs-*, time field: time)"
    exit 0
  fi

  echo "[$attempt/$MAX_ATTEMPTS] Dashboards not ready yet or request failed (HTTP $status): $(cat /tmp/index-pattern-response.json 2>/dev/null || true)"
  attempt=$((attempt + 1))
  sleep "$RETRY_DELAY_SECONDS"
done

echo "giving up after $MAX_ATTEMPTS attempts"
exit 1
