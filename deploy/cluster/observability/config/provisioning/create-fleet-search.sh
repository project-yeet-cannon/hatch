#!/bin/sh
# One-shot: creates the "Aerie fleet revisions" saved search in OpenSearch
# Dashboards - every log line that names the build that produced it, newest
# first, with `service` and `aerie_revision` as its columns.
#
# This is docs/plans/version.md's SRE story, and it is a saved object rather
# than a query written down in a runbook for one reason: a query nobody saved
# is a query nobody runs. "How up to date is my system" should be something an
# operator opens, not something they reconstruct from a document while
# something is wrong.
#
# What it answers, read straight off the result: which distinct revisions are
# in the fleet right now, which service each belongs to, and - because
# UiLogsController attributes a browser's line to the browser rather than to
# the API that relayed it - which physical devices are still running an old
# bundle. Sorting by time descending rather than aggregating keeps it useful
# in Discover, where an operator can add `deviceId` or filter to one service
# without leaving the saved object behind.
#
# Third-party workloads are excluded by `_exists_: aerie_revision`, which is
# exactly the property that field's absence was designed to give: "ours" is a
# query rather than a list of names to maintain.
#
# Same shape as ./create-index-pattern.sh - retry budget, overwrite=true for
# idempotency, `-d @file` rather than `-d "$BODY"` so argv stays small - and
# the same reasoning applies to each; see that file's comments, which explain
# the E2BIG cliff the inline form walked into.
set -eu

# No `${DASHBOARDS_URL:-...}` fallback: Flux's postBuild envsubst rewrites that
# form inside a configMapGenerator body. ./apply-ism-policy.sh has the full
# account.
SEARCH_ID="aerie-fleet-revisions"
INDEX_PATTERN_ID="aerie-logs"
MAX_ATTEMPTS=30
RETRY_DELAY_SECONDS=5

# searchSourceJSON is a JSON *string* inside the saved object, so the query
# below is escaped once here rather than nested. index refers to the
# index-pattern saved object ./create-index-pattern.sh creates, by id - the
# reason this script runs after it in ../opensearch-provision.yaml's chain.
BODY_FILE=/tmp/fleet-search-body.json
cat > "$BODY_FILE" <<'JSON'
{
  "attributes": {
    "title": "Aerie fleet revisions",
    "description": "Every line that names the build that produced it. Group by aerie_revision to see what is running; add deviceId to find stale devices.",
    "columns": ["service", "aerie_revision", "aerie_sequence", "Message"],
    "sort": [["time", "desc"]],
    "kibanaSavedObjectMeta": {
      "searchSourceJSON": "{\"query\":{\"query\":\"_exists_: aerie_revision\",\"language\":\"lucene\"},\"filter\":[],\"indexRefName\":\"kibanaSavedObjectMeta.searchSourceJSON.index\"}"
    }
  },
  "references": [
    {
      "name": "kibanaSavedObjectMeta.searchSourceJSON.index",
      "type": "index-pattern",
      "id": "aerie-logs"
    }
  ]
}
JSON

attempt=1
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  status=$(curl -s -o /tmp/fleet-search-response.json -w '%{http_code}' -X POST \
    "$DASHBOARDS_URL/api/saved_objects/search/$SEARCH_ID?overwrite=true" \
    -H 'Content-Type: application/json' -H 'osd-xsrf: true' -d @"$BODY_FILE" || echo 000)

  if [ "$status" = "200" ]; then
    echo "created/updated saved search '$SEARCH_ID' against index pattern '$INDEX_PATTERN_ID'"
    exit 0
  fi

  # As in ./create-index-pattern.sh: "HTTP 000" means curl never produced a
  # status line at all, which is a local failure and not Dashboards answering.
  echo "[$attempt/$MAX_ATTEMPTS] saved-search POST failed (HTTP $status; 000 = request never completed): $(cat /tmp/fleet-search-response.json 2>/dev/null || true)"
  attempt=$((attempt + 1))
  sleep "$RETRY_DELAY_SECONDS"
done

echo "giving up after $MAX_ATTEMPTS attempts"
exit 1
