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

echo "[backup] snapshotting grafana sqlite"
sqlite3 /mnt/grafana/grafana.db ".backup '$SCRATCH/grafana.db'"

echo "[backup] snapshotting kuma sqlite"
sqlite3 /mnt/kuma/kuma.db ".backup '$SCRATCH/kuma.db'"

echo "[backup] registering opensearch snapshot repo (idempotent)"
curl -fsS -X PUT "http://opensearch:9200/_snapshot/aerie_backup" \
  -H 'Content-Type: application/json' \
  -d '{"type":"fs","settings":{"location":"/mnt/snapshots"}}' > /dev/null

OS_SNAPSHOT="snapshot-$(date +%Y%m%d-%H%M%S)"
echo "[backup] taking opensearch snapshot $OS_SNAPSHOT"
curl -fsS -X PUT "http://opensearch:9200/_snapshot/aerie_backup/$OS_SNAPSHOT?wait_for_completion=true" \
  -H 'Content-Type: application/json' \
  -d '{"indices":"*","include_global_state":true}' > /dev/null

echo "[backup] taking prometheus tsdb snapshot"
PROM_SNAPSHOT=$(curl -fsS -X POST "http://prometheus:9090/api/v1/admin/tsdb/snapshot" \
  | sed -n 's/.*"name":"\([^"]*\)".*/\1/p')
if [ -z "$PROM_SNAPSHOT" ]; then
  echo "[backup] FAILED: prometheus snapshot did not return a name" >&2
  exit 1
fi

for REPO in "$RESTIC_REPOSITORY_LOCAL" "$RESTIC_REPOSITORY_S3"; do
  echo "[backup] backing up to $REPO"
  restic -r "$REPO" backup \
    "$SCRATCH/postgres-dumpall.sql" \
    "$SCRATCH/grafana.db" \
    "$SCRATCH/kuma.db" \
    /mnt/opensearch-snapshots \
    "/mnt/prometheus/snapshots/$PROM_SNAPSHOT" \
    --tag daily

  echo "[backup] pruning $REPO"
  restic -r "$REPO" forget --keep-daily 7 --keep-weekly 4 --keep-monthly 12 --prune
done

# Both repos now durably hold this run's opensearch/prometheus snapshots, so
# the staging copies can go — restic keeps the history now, these directories
# would otherwise grow unbounded since neither service prunes its own snapshots.
echo "[backup] cleaning up staging snapshots"
curl -fsS -X DELETE "http://opensearch:9200/_snapshot/aerie_backup/$OS_SNAPSHOT" > /dev/null
rm -rf "/mnt/prometheus/snapshots/$PROM_SNAPSHOT"

echo "[backup] done"
