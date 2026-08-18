#!/bin/sh
# Backs up every service by its own correct method (logical dump / consistent
# snapshot), never by copying a live volume directory, then pushes the result
# to both restic repos and prunes each to the standard retention.
set -eu

# Idempotent, and cheap when both repos already exist. Run before the dumps so
# a misconfigured repo fails fast, and so the daily job stands on its own
# rather than depending on a deploy having ever run the same check.
/app/scripts/init-repos.sh

SCRATCH=$(mktemp -d)
trap 'rm -rf "$SCRATCH"' EXIT

echo "[backup] dumping postgres"
PGPASSWORD="$POSTGRES_PASSWORD" pg_dumpall -h db -U "$POSTGRES_USER" > "$SCRATCH/postgres-dumpall.sql"

# Per-database, custom-format dumps alongside the dumpall above (the cluster
# plan Phase 4b.1). The dumpall stays — it is the globals/roles carrier and
# verify-restore.sh's own restore target — but it is the wrong artifact to
# restore into CNPG: it carries `CREATE ROLE user` / `CREATE DATABASE aerie`,
# both of which collide with what CNPG's initdb bootstrap and the Database CRD
# already created. Custom format (-Fc), not plain SQL, so the CNPG restore Job
# can `pg_restore --no-owner --no-privileges` these onto whatever role CNPG
# generated instead of requiring the old `user` role to exist.
echo "[backup] dumping aerie (custom format)"
PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -Fc -h db -U "$POSTGRES_USER" aerie > "$SCRATCH/aerie.dump"
echo "[backup] dumping quartz (custom format)"
PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -Fc -h db -U "$POSTGRES_USER" quartz > "$SCRATCH/quartz.dump"

echo "[backup] snapshotting kuma sqlite"
sqlite3 /mnt/kuma/kuma.db ".backup '$SCRATCH/kuma.db'"

for REPO in "$RESTIC_REPOSITORY_LOCAL" "$RESTIC_REPOSITORY_S3"; do
  echo "[backup] backing up to $REPO"
  restic -r "$REPO" backup \
    "$SCRATCH/postgres-dumpall.sql" \
    "$SCRATCH/aerie.dump" \
    "$SCRATCH/quartz.dump" \
    "$SCRATCH/kuma.db" \
    --tag daily

  echo "[backup] pruning $REPO"
  restic -r "$REPO" forget --keep-daily 7 --keep-weekly 4 --keep-monthly 12 --prune
done

echo "[backup] done"
