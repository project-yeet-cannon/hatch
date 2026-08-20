#!/bin/sh
# One-shot: applies the aerie-log-retention ISM policy so log indices
# autodelete instead of growing unbounded. Copied from
# ../../../../../containers/opensearch-provision/apply-ism-policy.sh (which
# stays in place, unedited, for compose.observability.yml until Phase 7) -
# see ./kustomization.yaml for why this is a real file rather than a string
# embedded in ./opensearch-provision.yaml.
#
# min_index_age is still 30d, unedited from the compose original, and that is
# a placeholder rather than a decision: the cluster plan's 6b.10 asks for a
# week of `_cat/indices` growth against fluent-bit's cluster-wide tail
# (Longhorn, CNPG, k3s and Flux logs now sharing this index, not just one
# compose project) before this number is set deliberately. Revisit once that
# measurement exists and write the measured number here, replacing this
# comment with the number and the date it was taken.
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
