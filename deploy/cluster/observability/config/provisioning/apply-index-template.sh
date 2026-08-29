#!/bin/sh
# One-shot: applies the aerie-logs index template so the `service` and
# `aerie_revision` fields (see ../../controllers/fluent-bit/service_tag.lua)
# are mapped as `keyword` on new daily indices, instead of relying on
# OpenSearch's dynamic text+keyword guess, and then converges
# `number_of_replicas: 0` onto the aerie-logs-* indices that already exist.
# Copied from the old compose stack's
# containers/opensearch-provision/apply-index-template.sh, deleted with the
# rest of that path in 7b.9 and reachable in git history - see
# ./kustomization.yaml for why this is a real file rather than a string
# embedded in ./opensearch-provision.yaml.
#
# keyword and not text for the revision fields, for the same reason as
# `service`: a git sha is an identifier to group and filter on exactly, never
# a string to tokenise. The default guess would make `aerie_revision` a text
# field with a `.keyword` subfield, which works for a term query and then
# quietly does not for an aggregation written against the bare name - and the
# whole point of this field is one path that behaves the same everywhere.
# aerie_sequence is a long because it is ordered and gets range queries.
#
# settings.number_of_replicas: 0 is the one edit this copy carries over the
# original: ../../controllers/opensearch.yaml runs `singleNode: true`, and a
# one-node cluster can never allocate a replica shard - every aerie-logs-*
# index would otherwise sit permanently yellow, which is not a warning about
# anything and is the first thing anyone chasing it would find. The compose
# original has no such setting because compose's OpenSearch was never asked
# whether it was single-node either way.
#
# A composable index template is consulted at index-creation time only, so
# the template alone converges nothing that already exists - and on this
# cluster that gap was not hypothetical. 6b.15 found health yellow with the
# template correctly in place: ../../controllers/opensearch.yaml's
# opensearch-restrict-ingress NetworkPolicy blocked these provisioning pods
# for the first stretch of the phase (that file's own comment tells the rest
# of that story), so fluent-bit created a run of daily indices before any
# template existed, each with the cluster default of one replica that a
# one-node cluster can never allocate. Those indices hold the cluster yellow
# until they are updated in place, which is what the second PUT below does.
# It is a settings update, not a reindex, and the distinction is the reason
# the mapping half above cannot be fixed the same way: per-index settings are
# mutable, field mappings are not, so an older index that already guessed
# text+keyword for `service` keeps that guess until ISM ages it out
# (./apply-ism-policy.sh). Only the replica count - the half that costs
# cluster health - is recoverable after the fact.
#
# Safe to run on every deploy: PUT on a composable index template is itself
# idempotent (re-applying the same body is a no-op), and so is a settings
# update that assigns the value an index already holds, so no existence check
# is needed before either call.
set -eu

# No `${OPENSEARCH_URL:-...}` fallback - Flux's postBuild envsubst rewrites
# that form inside a configMapGenerator body and would pin this to compose's
# `http://opensearch:9200` instead of the Service that exists here. The full
# account is in ./apply-ism-policy.sh, which hit it first.
TEMPLATE_NAME="aerie-logs"
INDEX_PATTERN="aerie-logs-*"
MAX_ATTEMPTS=30
RETRY_DELAY_SECONDS=5

# kubernetes.labels is pinned to flat_object - the OpenSearch half of the
# 2026-08-29 fluent-bit crashloop fix; the other half is Replace_Dots On in
# ../../controllers/fluent-bit.yaml, and that file's own comment carries the
# full account. Short version: pod labels are arbitrary operator-chosen keys,
# and OpenSearch's dynamic mapper reads a dot inside a field *name* as object
# nesting, so a pod labelled both `app: x` and `app.kubernetes.io/name: y`
# asks one index to map kubernetes.labels.app as a string and as an object at
# once. Whichever arrived first won, and every document with the other shape
# was rejected with a mapper_parsing_exception for the life of that index -
# 21 of this cluster's 97 pods carried such a pair.
#
# flat_object indexes the whole subtree as one field instead of mapping each
# label key on its own, so no key under it can collide with another, and the
# subtree cannot contribute to the 1000-field mapping limit either - which
# matters here for the same reason: the key space is arbitrary and grows with
# every chart this cluster installs. Replace_Dots already removes the dots
# that caused *this* collision; this is what makes the class of it
# unreachable, including for keys that arrive from somewhere other than the
# kubernetes filter.
#
# Both halves are prevention, not repair. As the header above says, a
# composable index template is consulted only when an index is created, and
# field mappings are immutable once guessed - so an aerie-logs-* index that
# already mapped kubernetes.labels.app as an object keeps rejecting those
# documents until ISM (./apply-ism-policy.sh) ages it out. The daily index
# pattern is what bounds that: the fix takes effect on the next UTC day's
# index without anything being deleted or reindexed.
TEMPLATE_BODY=$(cat <<'JSON'
{
  "index_patterns": ["aerie-logs-*"],
  "template": {
    "settings": {
      "number_of_replicas": 0
    },
    "mappings": {
      "properties": {
        "service": { "type": "keyword" },
        "aerie_revision": { "type": "keyword" },
        "aerie_relay_revision": { "type": "keyword" },
        "aerie_sequence": { "type": "long" },
        "kubernetes": {
          "properties": {
            "labels": { "type": "flat_object" },
            "annotations": { "type": "flat_object" }
          }
        }
      }
    }
  }
}
JSON
)

REPLICA_BODY='{"index":{"number_of_replicas":0}}'

# $1 url, $2 request body, $3 what to call it in the log. Both callers want
# the same thing - PUT this until it takes or the budget runs out - and the
# budget is what absorbs a cold pod: a brand-new one gets a refused
# connection or two before the NetworkPolicy starts admitting it by label,
# the same 150s window ./apply-ism-policy.sh sizes and explains.
put_with_retry() {
  url=$1
  body=$2
  label=$3
  attempt=1

  while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
    status=$(curl -s -o /tmp/index-template-response.json -w '%{http_code}' -X PUT \
      "$url" -H 'Content-Type: application/json' -d "$body" || echo 000)

    if [ "$status" = "200" ]; then
      echo "applied $label"
      return 0
    fi

    echo "[$attempt/$MAX_ATTEMPTS] failed to apply $label (HTTP $status): $(cat /tmp/index-template-response.json 2>/dev/null || true)"
    attempt=$((attempt + 1))
    sleep "$RETRY_DELAY_SECONDS"
  done

  echo "giving up on $label after $MAX_ATTEMPTS attempts"
  return 1
}

put_with_retry \
  "$OPENSEARCH_URL/_index_template/$TEMPLATE_NAME" \
  "$TEMPLATE_BODY" \
  "index template '$TEMPLATE_NAME'"

# allow_no_indices and ignore_unavailable so the first run on a fresh
# installation - template applied, fluent-bit not yet through its first
# flush, so nothing matches the wildcard - is a 200 and not a 404 that fails
# the `&&` chain in ../opensearch-provision.yaml before
# ./create-index-pattern.sh ever runs. expand_wildcards covers closed indices
# too: nothing closes one today (./apply-ism-policy.sh only deletes), but a
# closed index still reports its replica count into cluster health, so
# skipping it would leave exactly the yellow this call exists to clear.
put_with_retry \
  "$OPENSEARCH_URL/$INDEX_PATTERN/_settings?allow_no_indices=true&ignore_unavailable=true&expand_wildcards=open,closed" \
  "$REPLICA_BODY" \
  "number_of_replicas: 0 to existing $INDEX_PATTERN indices"
