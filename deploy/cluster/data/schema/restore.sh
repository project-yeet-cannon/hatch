#!/bin/sh
# The cluster plan Phase 4b.9 - runs inside ../restore-job.yaml. Restores the
# newest restic snapshot into aerie-pg and proves the result is real, in the
# order the phase doc specifies:
#   1. restic restore
#   2. pg_restore --no-owner --no-privileges, both databases
#   3. a sanity query
set -eu

# From S3, not the local repo Phase 0's backup.sh also writes to: this Job
# runs inside k3s, which has no network path to the old host's local disk
# (E:\restic-repo). S3 is the documented DR fallback for exactly this case -
# see docs/disaster-recovery.md's "original host is what was lost" scenario -
# so this isn't a shortcut, it's the path this repo already recommends when
# the local repo isn't reachable.
echo "[restore] restic restore latest --tag daily (from S3)"
mkdir -p /restore
restic restore latest --tag daily --target /restore

AERIE_DUMP=$(find /restore -name aerie.dump | head -1)
QUARTZ_DUMP=$(find /restore -name quartz.dump | head -1)

if [ -z "$AERIE_DUMP" ] || [ -z "$QUARTZ_DUMP" ]; then
  echo "[restore] FAILED: aerie.dump and/or quartz.dump not in the restored snapshot - this backup predates the per-database dumps containers/backup/scripts/backup.sh added in Phase 4b.1" >&2
  exit 1
fi

echo "[restore] pg_restore aerie <- $AERIE_DUMP"
pg_restore --no-owner --no-privileges -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d aerie "$AERIE_DUMP"

echo "[restore] pg_restore quartz <- $QUARTZ_DUMP"
pg_restore --no-owner --no-privileges -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d quartz "$QUARTZ_DUMP"

echo "[restore] sanity: table count across public+storage (aerie)"
# 'storage' is in this list because the module contexts
# (src/Aerie.Api/Modules/README.md) put some tables outside 'public' -
# src/Aerie.Api/Modules/Storage/StorageContext.cs is the one that does today.
# A check that only counted 'public' would pass against a database missing
# every module-owned table.
TABLE_COUNT=$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d aerie -tAc \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('public','storage')")
if [ "$TABLE_COUNT" -lt 1 ]; then
  echo "[restore] FAILED: restored 'aerie' database has no tables in 'public' or 'storage'" >&2
  exit 1
fi
echo "[restore] OK: $TABLE_COUNT table(s) across public+storage"

echo "[restore] sanity: row counts on two known aerie tables"
for TABLE in '"Devices"' '"EnvironmentReadings"'; do
  COUNT=$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d aerie -tAc "SELECT count(*) FROM $TABLE")
  echo "[restore]   $TABLE: $COUNT row(s)"
done

echo "[restore] sanity: quartz table count"
QUARTZ_TABLES=$(psql -h "$PGHOST" -p "$PGPORT" -U "$PGUSER" -d quartz -tAc \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name LIKE 'qrtz\_%'")
if [ "$QUARTZ_TABLES" -lt 11 ]; then
  echo "[restore] FAILED: restored 'quartz' database has only $QUARTZ_TABLES qrtz_* table(s), expected 11" >&2
  exit 1
fi
echo "[restore] OK: $QUARTZ_TABLES qrtz_* table(s)"

echo "[restore] Job-side checks passed. This is not the phase's actual deliverable -"
echo "[restore] compare the row counts above and the newest channel-history timestamp"
echo "[restore] against the old host by hand before trusting this restore."
