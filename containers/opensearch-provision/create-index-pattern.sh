#!/bin/sh
# One-shot: creates the aerie-logs-* index pattern in OpenSearch Dashboards
# (time field: time), so it doesn't need to be clicked through in the UI.
#
# Idempotent via the saved objects API's overwrite=true: every run re-applies
# the same definition instead of 409ing on an existing one, so re-runs are
# safe no-ops.
set -eu

DASHBOARDS_URL="${DASHBOARDS_URL:-http://opensearch-dashboards:5601}"
INDEX_PATTERN_ID="aerie-logs"
MAX_ATTEMPTS=30
RETRY_DELAY_SECONDS=5

BODY=$(cat <<'JSON'
{
  "attributes": {
    "title": "aerie-logs-*",
    "timeFieldName": "time"
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
