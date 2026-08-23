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
echo "[backup] exporting the parameter tree"
/app/scripts/export-parameters.sh "$STAGING/parameters.json"

# A fixed snapshot host, and it is not cosmetic - without it the retention
# below silently never removes anything. restic stamps each snapshot with the
# machine's hostname, and in Kubernetes that is the *pod* name, which is unique
# to every run of a CronJob. `restic forget` groups snapshots before applying
# its policy and groups by host and paths by default, so a per-run hostname
# puts every night's snapshot in a group of its own, where `--keep-daily 7`
# keeps all seven of the one snapshot it can see. The repository grows without
# bound, every `forget` reports success, and nothing anywhere says so.
#
# `aerie` rather than the pod name also restores restic's parent-snapshot
# lookup, which is keyed on the same host+paths pair: without it every run logs
# "no parent snapshot found, will read all files" and re-reads both dumps in
# full. Deduplication means that costs no storage, only work - but the
# retention half of the same finding costs both.
#
# Found by running this script twice against a throwaway repository and
# noticing that a deliberately broken `--keep-last 1` still removed nothing.
BACKUP_HOST=aerie

for REPO in "$RESTIC_REPOSITORY_LOCAL" "$RESTIC_REPOSITORY_S3"; do
  echo "[backup] backing up to $REPO"
  restic -r "$REPO" backup \
    "$STAGING/aerie.dump" \
    "$STAGING/quartz.dump" \
    "$STAGING/parameters.json" \
    --host "$BACKUP_HOST" \
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
  #
  # Phase 8b.6 asks for that protection to be asserted rather than read, and
  # this is where the assertion is cheapest and does the most good. `--prune`
  # deletes data, so a check that runs afterwards cannot undo anything - but
  # this loop does the local repository first and `set -e` aborts the script,
  # so a policy change that eats the tagged snapshot here stops the run before
  # the S3 `forget` reaches the last remaining copy. That is the difference
  # between finding out and losing it.
  #
  # The comparison is the projected snapshot list either side of the `forget`,
  # not a count: 8b.6's exit condition is "still exactly one, and still the
  # pre-cutover date", and short_id + time carries both. Projected rather than
  # compared raw because restic's snapshot JSON has grown fields between
  # versions (`summary` most recently) and an image bump should not read as a
  # lost snapshot. `.[]?` normalises the empty repository, which restic may
  # render as `[]` or as `null` - and the empty case has to pass, because a
  # fresh installation of this repository has no cutover-final snapshot at all
  # and its backups must not fail every night on the absence of one. The
  # property being defended is not "the tag exists"; it is "retention never
  # removes it", which is exactly what before-equals-after says.
  PROTECTED_BEFORE=$(restic -r "$REPO" snapshots --tag cutover-final --json |
    jq -Sc '[.[]? | {short_id, time}]')

  echo "[backup] pruning $REPO"
  # --group-by is restic's default written down rather than relied on, because
  # the BACKUP_HOST comment above is only true while this stays host+paths: a
  # future restic that changed the default, or a well-meaning `--group-by ''`,
  # would move the pre-cutover snapshot into the same group as the dailies and
  # make --keep-tag the only thing standing between it and the policy. It is
  # the only thing standing there in any case, which is what the assertion
  # after this call is for.
  restic -r "$REPO" forget \
    --group-by host,paths \
    --keep-daily 7 --keep-weekly 4 --keep-monthly 12 \
    --keep-tag cutover-final \
    --prune

  PROTECTED_AFTER=$(restic -r "$REPO" snapshots --tag cutover-final --json |
    jq -Sc '[.[]? | {short_id, time}]')

  if [ "$PROTECTED_BEFORE" != "$PROTECTED_AFTER" ]; then
    echo "[backup] FATAL: the forget above removed a cutover-final snapshot from $REPO." >&2
    echo "[backup] before: $PROTECTED_BEFORE" >&2
    echo "[backup] after:  $PROTECTED_AFTER" >&2
    echo "[backup] That snapshot is the last complete copy of the pre-cluster world and --keep-tag is the only thing protecting it. Stopping before the next repository in this loop is pruned too; recover the snapshot from the repository this run has not reached yet." >&2
    exit 1
  fi
done

echo "[backup] done"
