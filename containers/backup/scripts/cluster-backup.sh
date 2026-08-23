#!/bin/sh
# The daily backup, as run by deploy/cluster/data/backup/backup-cronjob.yaml.
# Dumps both databases by their own correct method (logical, custom-format,
# never a copy of a live volume directory), exports the parameter tree beside
# them, pushes the result to both restic repos and prunes each to the standard
# retention.
#
# What this deliberately no longer does, both from the cluster plan Phase 8's
# findings:
#   - `pg_dumpall`. CNPG disables the superuser role, so the cluster-wide dump
#     has no credential to run under, and it was already the wrong artifact:
#     its CREATE ROLE / CREATE DATABASE collide with what CNPG's own bootstrap
#     and the Database CRD created. Both databases are owned by the `aerie`
#     role in the CNPG-generated aerie-pg-app Secret, which is what PGUSER
#     below is, so the two per-database dumps carry everything a restore needs
#     and the globals carry nothing it can use.
#   - The Uptime Kuma SQLite snapshot. No pod can reach that volume any more
#     (its PVC is ReadWriteOnce and nothing co-schedules this Job with it);
#     it is Longhorn's backup target's job now, through a frozen snapshot.
set -eu

# Idempotent, and cheap when both repos already exist. Run before the dumps so
# a misconfigured repo fails fast, and so the daily job stands on its own
# rather than depending on a deploy having ever run the same check.
/app/scripts/init-repos.sh

# A fixed staging path rather than `mktemp -d`, because restic records the
# absolute path of every file it backs up: a random directory per run means
# the same dump lands at a different path in every snapshot, and
# `restic dump latest /...` - the recovery path Phase 8b.7 depends on for
# reading a parameter value back with nothing but the offline password - has
# no name to ask for. Cleared rather than assumed empty, and torn down on the
# way out; the pod's filesystem is ephemeral either way.
STAGING=/tmp/aerie-backup
rm -rf "$STAGING"
mkdir -p "$STAGING"
trap 'rm -rf "$STAGING"' EXIT

# Custom format (-Fc), not plain SQL, so the restore side can
# `pg_restore --no-owner --no-privileges` these onto whatever role CNPG
# generated rather than requiring the dump's own owner to exist - which is
# exactly what deploy/cluster/data/schema/restore.sh and ./cluster-verify.sh
# both do. PGHOST/PGPORT/PGUSER/PGPASSWORD come from the aerie-pg-app Secret,
# not from a hostname baked in here: CNPG owns that name and rotates that
# password.
echo "[backup] dumping aerie (custom format)"
pg_dump -Fc -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" aerie > "$STAGING/aerie.dump"
echo "[backup] dumping quartz (custom format)"
pg_dump -Fc -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" quartz > "$STAGING/quartz.dump"

# Into the same staging directory, so the parameter tree and the dumps go up
# in one `restic backup` below and are therefore one snapshot rather than two
# things that can be a different age. See ./export-parameters.sh for why the
# export exists at all and for the two things it must never do.
echo "[backup] exporting the /aerie parameter tree"
/app/scripts/export-parameters.sh "$STAGING/parameters.json"

for REPO in "$RESTIC_REPOSITORY_LOCAL" "$RESTIC_REPOSITORY_S3"; do
  echo "[backup] backing up to $REPO"
  restic -r "$REPO" backup \
    "$STAGING/aerie.dump" \
    "$STAGING/quartz.dump" \
    "$STAGING/parameters.json" \
    --tag daily

  # Retention is owned by this job and no other. restic's `forget --prune`
  # takes an exclusive lock that a concurrent `backup` will not wait behind,
  # so the verify CronJob and the export never carry a retention flag of
  # their own - and this CronJob is `concurrencyPolicy: Forbid` so it cannot
  # race itself.
  #
  # --keep-tag cutover-final protects one snapshot from the policy above: the
  # last complete copy of the pre-cluster world, taken at the Phase 7 cutover.
  # Uptime Kuma's SQLite and the pre-cutover `pg_dumpall` exist nowhere else,
  # and after the old host's disk was reformatted the only copies left are the
  # local repo this loop writes to and the S3 one - both of which this
  # `forget` now prunes. The flag is here in the same commit that first wrote
  # the `forget`, rather than added once the retention has had a year to reach
  # that snapshot, because by then the snapshot is gone and nothing reports it.
  echo "[backup] pruning $REPO"
  restic -r "$REPO" forget \
    --keep-daily 7 --keep-weekly 4 --keep-monthly 12 \
    --keep-tag cutover-final \
    --prune
done

echo "[backup] done"
