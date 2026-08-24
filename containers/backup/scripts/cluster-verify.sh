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

# Every non-system schema, and deliberately not a list of the ones that
# existed when this was written. This check used to read
# `table_schema IN ('public','storage')` - correct in August 2026, when
# Storage was the only module context (src/Aerie.Api/Modules/README.md: one
# schema per module). `gather` and `game` arrived days later and nothing told
# this line, so for a while a dump that had lost both modules entirely would
# have counted 27 tables and passed. The 8b.15 rehearsal found that; naming
# schemas here is what made it possible, so no schema is named here now.
TABLE_COUNT=$(psql -h "$SCRATCH" -p 5433 -U verify -d aerie_verify -tAc \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema')")

# The per-schema breakdown, in the log rather than in an assertion, because
# this Job has no live database to compare against: verify-cronjob.yaml gives
# it RESTIC_PASSWORD and RESTIC_REPOSITORY_LOCAL and nothing else, on purpose.
# So the weekly proof is "the dump restores and has tables", and "every schema
# the live database has is in the dump" belongs to scripts/k3s/Invoke-DrRehearsal.ps1,
# which does have both sides. Printed anyway: a module that stops appearing on
# this line week over week is visible to anyone reading the log, and costs one
# query to make so.
SCHEMA_BREAKDOWN=$(psql -h "$SCRATCH" -p 5433 -U verify -d aerie_verify -tAc \
  "SELECT string_agg(s || ':' || c, ', ' ORDER BY s) FROM (SELECT table_schema s, count(*) c FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema') GROUP BY 1) t")

if [ "$TABLE_COUNT" -lt 1 ]; then
  echo "[verify] FAILED: restored database has no tables in any non-system schema" >&2
  exit 1
fi

echo "[verify] OK: restored database has $TABLE_COUNT table(s) across ${SCHEMA_BREAKDOWN:-no schemas}"
