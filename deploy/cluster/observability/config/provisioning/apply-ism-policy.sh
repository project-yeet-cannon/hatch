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
# 120 rather than the compose original's 30, and the extra 7.5 minutes are not
# about OpenSearch being slow to start. ../../controllers/opensearch.yaml's
# opensearch-restrict-ingress NetworkPolicy admits this pod by label, but that
# admission is eventually consistent: k3s runs kube-router's firewall
# controller without overriding --iptables-sync-period, so its 5m default
# stands, and a pod that has only just been created can be REJECTed for
# anything up to a full sync period before its address lands in the ipset the
# policy compiles to. Measured on this cluster: one pod was admitted after
# 30s, another was still refused at 100s. 30 attempts is 150s, which loses
# that race often enough to look like OpenSearch is down - `HTTP 000` on every
# line, which is a refused connection, not an unhealthy cluster. 120 attempts
# is 10 minutes, comfortably past the 5m worst case.
#
# The alternative fix is to keep ephemeral pods out from behind the policy
# entirely - reaching :9200 through the API server's service proxy the way
# scripts/k3s/Test-Observability.ps1 does, which is unaffected by it. That
# buys RBAC and a kubectl in this image; waiting is cheaper for a CronJob
# whose whole job is to converge eventually.
MAX_ATTEMPTS=120
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
