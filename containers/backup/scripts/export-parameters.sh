#!/bin/sh
# The cluster plan Phase 8b.7 - the /aerie parameter tree, exported into the
# same restic snapshot as the database dumps. Called by ./cluster-backup.sh
# with the output path as its one argument; the environment it needs is set by
# deploy/cluster/data/backup/backup-cronjob.yaml.
#
# Why this exists, from docs/secrets-architecture.md, which says Phase 8 is
# where this loop closes. Moving every value into Parameter Store is what let
# the cluster read its own secrets without a human, and it introduced a
# failure mode nothing before it had: the store is off-site, so "the AWS
# account is gone" now takes every secret in the house with it. This export is
# the answer, and it is only an answer because RESTIC_PASSWORD is printed
# offline (8a.4 proved the copy still opens a repo) - the tree lands somewhere
# readable from outside the account it describes, rather than in a second copy
# of the same dependency.
#
# TWO THINGS THIS FILE MUST NEVER DO.
#
#   1. Write. It is `get-parameters-by-path` and nothing else. The identity it
#      runs as should not be able to write either, and does not:
#      ../../../scripts/secrets/iam/aerie-restic-ssm.policy.json grants three
#      read actions plus kms:Decrypt through SSM, and no ssm:PutParameter.
#      Read-only here is the belt; that policy is the braces.
#   2. Log. `set -x` anywhere in this script - or an `echo` of the output, or
#      a `cat` of the file while debugging - puts every secret in the house
#      into a pod log that Fluent Bit ships to OpenSearch, which the cluster
#      plan Phase 6b.9 writes down as a searchable, unauthenticated index.
#      Nothing below prints anything derived from a value. The only number it
#      reports is a count, and the only name it reports is the prefix.
set -eu

# Before the file is created, not after: the export is written by the
# redirection below, so the mode it is born with is the only mode it ever has.
# 0600 also travels - restic records permissions, so a `restic restore` of
# this snapshot lays the tree back down unreadable to anyone but its owner.
umask 077

OUT="${1:?usage: export-parameters.sh <output-path>}"

# The prefix is a real variable in this design, not decoration.
# scripts/secrets/parameters.json carries a `prefix` field, Sync-AerieSecrets.ps1
# takes a -ParameterPrefix override for the case its help text names ("two
# installations sharing one AWS account give each its own prefix"), and the
# IAM policy above is written against a <PARAMETER_PREFIX> placeholder for the
# same reason. Hardcoding /aerie here would be the one place in the chain that
# could not follow. The default matches parameters.json's own so a stock
# installation needs no environment at all.
PREFIX="${PARAMETER_PREFIX:-/aerie}"

# A bare "/" is the failure this guard exists for: it is a legal path that
# exports every parameter in the account, including any belonging to something
# that is not Aerie, into a backup repository scoped to Aerie. An empty value
# is rejected by the same test. `/?*` needs a slash and at least one character
# after it, which "/" and "" both fail.
case "$PREFIX" in
  /?*) ;;
  *)
    echo "[export-parameters] PARAMETER_PREFIX='$PREFIX' is not an absolute path below the root - refusing to export the whole account" >&2
    exit 1
    ;;
esac
PREFIX="${PREFIX%/}"

# --recursive because the tree is two levels deep (cert-manager/..., backup/...).
# --with-decryption because a SecureString exported as ciphertext is a copy of
# something only the account that is gone can read, which is the whole failure
# being insured against.
#
# --query sort_by(...) does two things worth the flag. It drops the
# `{"Parameters": [...]}` envelope so the file is the array itself and its
# `length` is the parameter count - which is what 8b.16's gate compares against
# parameters.json - and it makes the byte content deterministic. SSM returns
# pages in no promised order, so an unsorted export differs from yesterday's
# even when nothing changed, and restic stores a new blob for it every night
# forever. Sorted, an unchanged tree deduplicates to nothing.
#
# Pagination is the AWS CLI's own and is deliberately not disabled:
# get-parameters-by-path caps MaxResults at 10, so a --no-paginate here would
# silently export the first ten parameters of twenty-two. The CLI merges the
# pages before --query is applied, which is why the sort covers the whole tree
# rather than each page.
echo "[export-parameters] exporting $PREFIX"
aws ssm get-parameters-by-path \
  --path "$PREFIX" \
  --recursive \
  --with-decryption \
  --query 'sort_by(Parameters, &Name)' \
  --output json > "$OUT"

# An export that succeeded and captured nothing is the failure here that looks
# like success: the snapshot is taken, the job is green, and the file is `[]`
# until the day someone needs it. A wrong prefix and a credential whose policy
# lost its Resource ARNs both land exactly there, and neither is an error the
# CLI reports. `jq length` rather than counting matches with grep, because the
# file's own values are attacker-adjacent text and a value containing the
# string this would grep for would inflate the count.
COUNT=$(jq 'length' "$OUT")
if [ "$COUNT" -eq 0 ]; then
  echo "[export-parameters] $PREFIX returned no parameters - refusing to snapshot an empty export. Check PARAMETER_PREFIX and the identity's ssm:GetParametersByPath Resource ARNs (the bare-path ARN is the one usually missing)." >&2
  exit 1
fi

# The count, and nothing else. 8b.16's gate is what checks it against the
# entry count in parameters.json; this line is what makes a shrinking tree
# visible in the pod log on the day it shrinks.
echo "[export-parameters] $COUNT parameter(s) exported"
