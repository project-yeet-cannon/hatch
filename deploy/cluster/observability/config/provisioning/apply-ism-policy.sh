#!/bin/sh
# One-shot: applies the aerie-log-retention ISM policy so log indices
# autodelete instead of growing unbounded, and converges the replica count on
# the plugin's own config index. Copied from the old compose stack's
# containers/opensearch-provision/apply-ism-policy.sh, deleted with the rest
# of that path in 7b.9 and reachable in git history -
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
# before creating it, and the settings update at the end assigns a value the
# index usually already holds, so re-runs are no-ops either way.
set -eu

# No `${OPENSEARCH_URL:-http://opensearch:9200}` fallback here, and this is
# the one edit that separates this copy from the compose original. That
# fallback is not shell syntax by the time this script runs: ../../../..
# /observability.yaml gives this Kustomization a postBuild.substituteFrom, and
# Flux runs envsubst over everything it renders - including the body of a
# configMapGenerator file. `${VAR:-default}` is envsubst's *own* default-value
# syntax, so with no OPENSEARCH_URL key in aerie-cluster-config it collapsed
# the whole expression to the literal `http://opensearch:9200` before this
# ever reached the cluster. That is compose's Service name; here the Service
# is opensearch-cluster-master, so every request went to a host that does not
# resolve, the retry loop reported `HTTP 000` on every line as though
# OpenSearch were down, and ../opensearch-provision.yaml's carefully-set
# OPENSEARCH_URL env var was never consulted at all - the shell default had
# already won at build time.
#
# Bare `$OPENSEARCH_URL` below is untouched by the same pass (Flux leaves
# names it has no value for alone; only the `:-`, `:=`, `:?` and `:+` forms
# are rewritten), so reading the env var directly is both correct and the
# safest thing to write in a file that gets envsubst'd. `set -u` above turns a
# missing env var into an immediate, obvious failure instead of a silent
# wrong-host default - which is what should have happened here.
POLICY_ID="aerie-log-retention"
# The index the ISM plugin creates for itself, lazily, the first time a
# policy is written to it - so it exists as a direct consequence of the PUT
# below and of nothing else, which is why converging it belongs in this
# script rather than in ./apply-index-template.sh next door.
CONFIG_INDEX=".opendistro-ism-config"
# 30 attempts at 5s is 150s, unchanged from the compose original and ample.
# A brand-new pod does get one refused connection before
# ../../controllers/opensearch.yaml's opensearch-restrict-ingress NetworkPolicy
# starts admitting it by label - measured at under 20s on this cluster, so the
# first attempt or two can fail on a cold pod and the loop absorbs it.
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
policy_ready=0
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  status=$(curl -s -o /dev/null -w '%{http_code}' "$OPENSEARCH_URL/_plugins/_ism/policies/$POLICY_ID" || echo 000)

  if [ "$status" = "200" ]; then
    echo "ISM policy '$POLICY_ID' already present, nothing to do"
    policy_ready=1
    break
  fi

  if [ "$status" = "404" ]; then
    create_status=$(curl -s -o /tmp/ism-response.json -w '%{http_code}' -X PUT \
      "$OPENSEARCH_URL/_plugins/_ism/policies/$POLICY_ID" \
      -H 'Content-Type: application/json' -d "$POLICY_BODY")
    if [ "$create_status" = "200" ] || [ "$create_status" = "201" ]; then
      echo "created ISM policy '$POLICY_ID'"
      policy_ready=1
      break
    fi
    echo "[$attempt/$MAX_ATTEMPTS] failed to create policy (HTTP $create_status): $(cat /tmp/ism-response.json)"
  else
    echo "[$attempt/$MAX_ATTEMPTS] OpenSearch not ready yet (HTTP $status)"
  fi

  attempt=$((attempt + 1))
  sleep "$RETRY_DELAY_SECONDS"
done

if [ "$policy_ready" -ne 1 ]; then
  echo "giving up after $MAX_ATTEMPTS attempts"
  exit 1
fi

# The `break`s above replaced two `exit 0`s so that this runs on every path,
# including the overwhelmingly common one where the policy already exists.
#
# ISM creates $CONFIG_INDEX with a hardcoded `number_of_replicas: 1` and no
# `auto_expand_replicas` to walk it back - confirmed by reading the created
# index's own settings, not assumed. ../../controllers/opensearch.yaml runs
# `singleNode: true`, so that replica can never be allocated and the index
# sits yellow forever, taking the whole cluster's health with it. 6b.15 found
# it doing exactly that, alongside the aerie-logs-* half that
# ./apply-index-template.sh answers: two unassigned shards, two different
# causes, one shared symptom.
#
# This is a plugin's own index rather than one this tree designed, and
# reaching into it deserves the justification: the plugin has no setting to
# ask for a different replica count, the index only exists because of the PUT
# above, and the alternative is a cluster that can never report green - a
# permanently-yellow signal nobody can act on, which costs more than owning
# one setting on one index does.
#
# ignore_unavailable so the very first run on a fresh installation - where
# the PUT above has only just created the policy and the plugin may not have
# materialised its index yet - is a 200 rather than a 404 that would fail the
# `&&` chain in ../opensearch-provision.yaml. Convergence then happens on the
# next hourly tick, which is the whole reason that CronJob is a CronJob.
attempt=1
while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
  settings_status=$(curl -s -o /tmp/ism-config-settings-response.json -w '%{http_code}' -X PUT \
    "$OPENSEARCH_URL/$CONFIG_INDEX/_settings?ignore_unavailable=true&expand_wildcards=open,closed" \
    -H 'Content-Type: application/json' \
    -d '{"index":{"number_of_replicas":0}}' || echo 000)

  if [ "$settings_status" = "200" ]; then
    echo "applied number_of_replicas: 0 to $CONFIG_INDEX"
    exit 0
  fi

  echo "[$attempt/$MAX_ATTEMPTS] failed to set replicas on $CONFIG_INDEX (HTTP $settings_status): $(cat /tmp/ism-config-settings-response.json 2>/dev/null || true)"
  attempt=$((attempt + 1))
  sleep "$RETRY_DELAY_SECONDS"
done

echo "giving up on $CONFIG_INDEX after $MAX_ATTEMPTS attempts"
exit 1
