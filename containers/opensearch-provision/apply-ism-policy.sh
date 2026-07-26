#!/bin/sh
# One-shot: applies the aerie-log-retention ISM policy so log indices
# autodelete after 30 days instead of growing unbounded on a home server.
#
# Safe to run on every deploy: checks whether the policy already exists
# before creating it, so re-runs are no-ops.
set -eu

OPENSEARCH_URL="${OPENSEARCH_URL:-http://opensearch:9200}"
POLICY_ID="aerie-log-retention"
MAX_ATTEMPTS=30
RETRY_DELAY_SECONDS=5

POLICY_BODY=$(cat <<'JSON'
{
  "policy": {
    "description": "Delete Aerie log indices after 30 days",
    "default_state": "hot",
    "states": [
      { "name": "hot", "transitions": [{ "state_name": "delete", "conditions": { "min_index_age": "30d" } }] },
      { "name": "delete", "actions": [{ "delete": {} }] }
    ],
    "ism_template": { "index_patterns": ["aerie-logs-*"], "priority": 100 }
  }
}
JSON
)

attempt=1
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  status=$(curl -s -o /dev/null -w '%{http_code}' "$OPENSEARCH_URL/_plugins/_ism/policies/$POLICY_ID" || echo 000)

  if [ "$status" = "200" ]; then
    echo "ISM policy '$POLICY_ID' already present, nothing to do"
    exit 0
  fi

  if [ "$status" = "404" ]; then
    create_status=$(curl -s -o /tmp/ism-response.json -w '%{http_code}' -X PUT \
      "$OPENSEARCH_URL/_plugins/_ism/policies/$POLICY_ID" \
      -H 'Content-Type: application/json' -d "$POLICY_BODY")
    if [ "$create_status" = "200" ] || [ "$create_status" = "201" ]; then
      echo "created ISM policy '$POLICY_ID'"
      exit 0
    fi
    echo "[$attempt/$MAX_ATTEMPTS] failed to create policy (HTTP $create_status): $(cat /tmp/ism-response.json)"
  else
    echo "[$attempt/$MAX_ATTEMPTS] OpenSearch not ready yet (HTTP $status)"
  fi

  attempt=$((attempt + 1))
  sleep "$RETRY_DELAY_SECONDS"
done

echo "giving up after $MAX_ATTEMPTS attempts"
exit 1
