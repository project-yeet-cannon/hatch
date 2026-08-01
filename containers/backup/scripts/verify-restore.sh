#!/bin/sh
# Restores the newest snapshot and proves it's real: loads the postgres dump
# into a throwaway instance (initdb/pg_ctl from this image's own postgres
# install, torn down after) and runs a sanity query. An unrestored backup is
# not a backup.
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

DUMP_FILE=$(find "$SCRATCH/restored" -name postgres-dumpall.sql | head -1)
if [ -z "$DUMP_FILE" ]; then
  echo "[verify] FAILED: no postgres-dumpall.sql in the restored snapshot" >&2
  exit 1
fi

echo "[verify] starting scratch postgres"
initdb -D "$PGDATA" -U verify --auth=trust > /dev/null
pg_ctl -D "$PGDATA" -o "-p 5433 -k $SCRATCH" -w start -l "$SCRATCH/pg.log"

createdb -h "$SCRATCH" -p 5433 -U verify aerie_verify
psql -h "$SCRATCH" -p 5433 -U verify -d aerie_verify -f "$DUMP_FILE" > /dev/null

TABLE_COUNT=$(psql -h "$SCRATCH" -p 5433 -U verify -d aerie_verify -tAc \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'")

if [ "$TABLE_COUNT" -lt 1 ]; then
  echo "[verify] FAILED: restored database has no tables in schema 'public'" >&2
  exit 1
fi

echo "[verify] OK: restored database has $TABLE_COUNT tables in schema 'public'"
