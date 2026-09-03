#!/bin/sh
# Back up a directory to both restic repositories.
#
# The generic sibling of ./cluster-backup.sh. That script knows what a Postgres
# dump is and where the parameter tree comes from; this one knows nothing about
# its input beyond the path, which is the point: it is the platform's answer to
# "I have a PVC holding something that cannot be regenerated, put it in the
# backup path". Its first caller is the trading silo's option-chain lake
# (docs/plans/trading.md Phase 3, deploy/cluster/trading/lake/), and nothing in
# here names it.
#
# Three environment variables beyond the restic credentials:
#
#   BACKUP_PATH  the directory to back up. Must exist.
#   BACKUP_HOST  the restic snapshot host. See the long note below - this is
#                the single most load-bearing value in the file.
#   BACKUP_TAG   the tag every snapshot carries, so a caller can find its own.
#
# **This script does not run `forget`.** Retention across both repositories is
# owned by ./cluster-backup.sh and by no other job, which is that script's own
# rule and is not weakened here: `forget --prune` takes an exclusive lock that
# a concurrent `backup` will not wait behind, and two jobs holding retention
# policies for one repository is how a snapshot disappears with both of them
# reporting success. What that means in practice is written out in
# deploy/cluster/trading/lake/chains-backup-cronjob.yaml, because the
# consequence belongs where the schedule is.
set -eu

: "${BACKUP_PATH:?is empty - refusing to back up an unnamed path}"
: "${BACKUP_HOST:?is empty - see the snapshot-host note in this script}"
: "${BACKUP_TAG:?is empty - refusing to write untagged snapshots}"
: "${RESTIC_PASSWORD:?is empty - refusing to open a repository}"
: "${RESTIC_REPOSITORY_LOCAL:?is empty}"
: "${RESTIC_REPOSITORY_S3:?is empty}"

# A missing path is a failure, not an empty backup. The failure being prevented
# is a volume that did not mount: restic would happily record a snapshot of
# nothing, every night, and the repository would look healthy right up until
# somebody restored from it.
if [ ! -d "$BACKUP_PATH" ]; then
  echo "[path-backup] FATAL: $BACKUP_PATH does not exist or is not a directory." >&2
  echo "[path-backup] Refusing to record an empty snapshot - that is what a" >&2
  echo "[path-backup] volume that failed to mount looks like." >&2
  exit 1
fi

# Idempotent and cheap when both repositories already exist. Run first so a
# misconfigured repository fails before anything is read.
/app/scripts/init-repos.sh

# A fixed snapshot host, and it is not cosmetic - ./cluster-backup.sh found
# this the hard way and the finding applies to every restic job in this
# cluster. restic stamps each snapshot with the machine's hostname, which in
# Kubernetes is the *pod* name, unique to every run of a CronJob. `restic
# forget` groups snapshots by host and paths before applying its policy, so a
# per-run hostname puts every night's snapshot in a group of its own where
# `--keep-daily 7` keeps all seven of the one snapshot it can see: the
# repository grows without bound, every `forget` reports success, and nothing
# anywhere says so. A fixed host also restores restic's parent-snapshot
# lookup, which is keyed on the same host+paths pair.
for REPO in "$RESTIC_REPOSITORY_LOCAL" "$RESTIC_REPOSITORY_S3"; do
  echo "[path-backup] backing up $BACKUP_PATH to $REPO"
  restic -r "$REPO" backup "$BACKUP_PATH" \
    --host "$BACKUP_HOST" \
    --tag "$BACKUP_TAG"
done

echo "[path-backup] done"
