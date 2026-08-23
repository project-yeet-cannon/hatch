#!/bin/sh
# The weekly restore verification, as run by
# deploy/cluster/data/backup/verify-cronjob.yaml. Restores the newest snapshot
# and proves it's real: loads the `aerie` dump into a throwaway instance
# (initdb/pg_ctl from this image's own postgres install, torn down after) and
# runs a sanity query. An unrestored backup is not a backup.
#
# Against the local repo, which is deliberate on two axes: it exercises the
# copy that is not in AWS, and it exercises the CIFS mount underneath it -
# the least-proven thing in this design, since restic coordinates through lock
# files and CIFS's handling of them is the corner nobody has run yet. A stale
# lock surfacing here is a finding for
# deploy/cluster/data/backup/local-repo-volume.yaml's comment, not a mystery.
set -eu

SCRATCH=$(mktemp -d)
PGDATA="$SCRATCH/pgdata"
cleanup() {
  [ -f "$PGDATA/postmaster.pid" ] && pg_ctl -D "$PGDATA" -o "-k $SCRATCH" stop -m fast >/dev/null 2>&1
  rm -rf "$SCRATCH"
}
trap cleanup EXIT

echo "[verify] restoring latest snapshot from $RESTIC_REPOSITORY_LOCAL"
restic -r "$RESTIC_REPOSITORY_LOCAL" restore latest --target "$SCRATCH/restored" --tag daily

# Found by name rather than by path: the snapshot records the absolute
# staging path ./cluster-backup.sh wrote from, and this script should not
# have to agree with that string to work against an older snapshot.
DUMP_FILE=$(find "$SCRATCH/restored" -name aerie.dump | head -1)
if [ -z "$DUMP_FILE" ]; then
  echo "[verify] FAILED: no aerie.dump in the restored snapshot" >&2
  exit 1
fi

echo "[verify] starting scratch postgres"
initdb -D "$PGDATA" -U verify --auth=trust > /dev/null
pg_ctl -D "$PGDATA" -o "-p 5433 -k $SCRATCH" -w start -l "$SCRATCH/pg.log"

# pg_restore of the custom-format dump, not psql replaying a `pg_dumpall`:
# ./cluster-backup.sh stopped producing postgres-dumpall.sql when CNPG took
# the superuser role away. --no-owner --no-privileges because the roles the
# dump names exist in the cluster and not in this five-second instance, and
# no --clean/--if-exists because this database was created empty a line ago.
# A restore that hits any error at all exits non-zero and `set -e` above ends
# the run, which is the whole point of this job.
echo "[verify] pg_restore aerie_verify <- $DUMP_FILE"
createdb -h "$SCRATCH" -p 5433 -U verify aerie_verify
pg_restore --no-owner --no-privileges -h "$SCRATCH" -p 5433 -U verify -d aerie_verify "$DUMP_FILE"

# 'storage' alongside 'public' for the reason
# deploy/cluster/data/schema/restore.sh gives: the module contexts
# (src/Aerie.Api/Modules/README.md) put some tables outside 'public', so a
# count of 'public' alone would pass against a dump missing every
# module-owned table.
TABLE_COUNT=$(psql -h "$SCRATCH" -p 5433 -U verify -d aerie_verify -tAc \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('public','storage')")

if [ "$TABLE_COUNT" -lt 1 ]; then
  echo "[verify] FAILED: restored database has no tables in 'public' or 'storage'" >&2
  exit 1
fi

echo "[verify] OK: restored database has $TABLE_COUNT table(s) across public+storage"
