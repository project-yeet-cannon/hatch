#!/bin/sh
# One-shot: applies the aerie-logs index template so the `service` field
# (see containers/fluent-bit/service_tag.lua) is mapped as `keyword` on new
# daily indices, instead of relying on OpenSearch's dynamic text+keyword
# guess. Only affects indices created after this runs -- any aerie-logs-*
# index that already exists keeps whatever mapping it dynamically picked up.
#
# Safe to run on every deploy: PUT on a composable index template is itself
# idempotent (re-applying the same body is a no-op), so no existence check
# is needed before creating/updating it.
set -eu

OPENSEARCH_URL="${OPENSEARCH_URL:-http://opensearch:9200}"
TEMPLATE_NAME="aerie-logs"
MAX_ATTEMPTS=30
RETRY_DELAY_SECONDS=5

TEMPLATE_BODY=$(cat <<'JSON'
{
  "index_patterns": ["aerie-logs-*"],
  "template": {
    "mappings": {
      "properties": {
        "service": { "type": "keyword" }
      }
    }
  }
}
JSON
)

attempt=1
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  status=$(curl -s -o /tmp/index-template-response.json -w '%{http_code}' -X PUT \
    "$OPENSEARCH_URL/_index_template/$TEMPLATE_NAME" \
    -H 'Content-Type: application/json' -d "$TEMPLATE_BODY" || echo 000)

  if [ "$status" = "200" ]; then
    echo "applied index template '$TEMPLATE_NAME'"
    exit 0
  fi

  echo "[$attempt/$MAX_ATTEMPTS] failed to apply index template (HTTP $status): $(cat /tmp/index-template-response.json 2>/dev/null || true)"
  attempt=$((attempt + 1))
  sleep "$RETRY_DELAY_SECONDS"
done

echo "giving up after $MAX_ATTEMPTS attempts"
exit 1
