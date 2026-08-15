# Disaster Recovery

## Summary

Every stateful service is backed up nightly to two [restic](https://restic.readthedocs.io/) repositories — one on a local, physically separate disk (`E:\restic-repo` on the host), one on S3 (`aerie-restic-backups`) — by a dedicated `backup` service ([`compose.backup.yml`](../compose.backup.yml), image built from [`containers/backup/`](../containers/backup/)). A weekly job restores the latest snapshot into a throwaway Postgres and sanity-queries it, so a silently broken backup gets caught automatically rather than discovered during an actual emergency.

This is Phase 0 of [`TODO_SWARM.md`](../TODO_SWARM.md): backup + DR exist before any cluster work starts, on the current single Windows host.

> **The offline-stored `RESTIC_PASSWORD` is the actual recovery credential.** It lives as a GitHub Actions secret so `cd.yml` can inject it into the `backup` container at deploy time — but GitHub Actions secrets are **write-only**; nobody can read `RESTIC_PASSWORD` back out of GitHub once it's set, not even a repo admin. Every manual restore procedure in this document assumes you have the password from wherever you printed and stored it offline. Without it, both restic repositories are permanently unreadable — that's the whole point of encryption, but it means the offline copy isn't optional paperwork.

## Architecture

- **`backup` service**: always-on container running [supercronic](https://github.com/aptible/supercronic) (crontab: [`containers/backup/crontab`](../containers/backup/crontab)), on the `local`/`observability`/`metrics` networks so it can reach `db`, `opensearch`, and `prometheus` by service name.
- **Image**: built on `postgres:18.4-alpine` — version-matched to the `db` service so `pg_dumpall` is never older than the server it's dumping from — plus `restic`, `sqlite3`, and `curl`.
- **Two repos, one dump**: each service is dumped/snapshotted once per run into a scratch directory, then pushed to both the local and S3 repos, so a Postgres outage never gets dumped twice.
- **Secrets**: injected as plain environment variables by `cd.yml` at deploy time (`RESTIC_PASSWORD` and `RESTIC_AWS_SECRET_ACCESS_KEY` as repository secrets, `RESTIC_AWS_ACCESS_KEY_ID` as a repository variable — see [`scripts/secrets/README.md`](../scripts/secrets/README.md#which-tab-ids-are-variables-everything-else-is-a-secret)), the same pattern already used for the Route53 and Home Assistant credentials. The AWS credentials belong to a dedicated `aerie-restic` IAM user scoped to only the `aerie-restic-backups` bucket — a leak of these can't touch Route53, and vice versa. See [`TODO_SWARM.md`](../TODO_SWARM.md)'s Phase 0 section for why this is GitHub Actions secrets rather than the SOPS + age setup planned for Phase 2.

## What's backed up, and how

Every service is backed up by its own correct method — never by copying a live volume directory, which risks an inconsistent copy for anything that isn't already snapshot-safe.

| Service | Method | Why |
|---|---|---|
| Postgres (`db`) | `pg_dumpall` over the network | Logical dump, always consistent, portable across Postgres patch versions |
| Grafana | `sqlite3 grafana.db ".backup"` | SQLite's own online-backup API — safe against a live, concurrently-written database |
| Uptime Kuma | `sqlite3 kuma.db ".backup"` | Same as Grafana |
| OpenSearch | Snapshot API (`_snapshot`) | The only consistent way to capture a running cluster's indices |
| Prometheus | TSDB snapshot endpoint (`/api/v1/admin/tsdb/snapshot`) | Point-in-time copy of the block storage without stopping the scrape loop |

Implementation: [`containers/backup/scripts/backup.sh`](../containers/backup/scripts/backup.sh). OpenSearch and Prometheus snapshots are staged in their own volumes only long enough for restic to capture them, then deleted — restic is where the actual history lives; neither service prunes its own snapshots, so leaving them in place would grow unbounded.

## Retention

`restic forget --keep-daily 7 --keep-weekly 4 --keep-monthly 12 --prune`, run against both repos after every backup. All snapshots are tagged `daily`.

## Schedule

- **03:10 daily** — backup + retention (`backup.sh`)
- **04:00 Sunday** — restore verification (`verify-restore.sh`)

Both run inside the `backup` container via cron; check `docker compose logs backup` for output from either.

## Automated restore verification

Every Sunday, [`verify-restore.sh`](../containers/backup/scripts/verify-restore.sh) restores the latest snapshot from the local repo, starts a throwaway Postgres instance using `initdb`/`pg_ctl` from the backup image's own Postgres install (no extra container, no docker-socket access), replays the `pg_dumpall` output into it, and confirms `information_schema.tables` isn't empty. A failure here means the backup exists but isn't restorable — treat it as a page-worthy incident, not a log line to ignore.

This proves the backup *contents* are valid. It does **not** by itself satisfy the Phase 0 gate below — it restores into a throwaway Postgres inside the same container, not a standalone VM.

## Triggering a backup or verification out of schedule

```
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml exec backup /app/scripts/backup.sh
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml exec backup /app/scripts/verify-restore.sh
```

## Listing snapshots

```
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml exec backup sh -c 'restic -r "$RESTIC_REPOSITORY_LOCAL" snapshots'
```

Swap `$RESTIC_REPOSITORY_LOCAL` for `$RESTIC_REPOSITORY_S3` to check the S3 copy instead — the `backup` container already has both repos' credentials, so no separate setup is needed to query either one. (Note the single quotes: this has to be a literal string handed to the container's own shell, not expanded by your host shell — see the postmortem in this repo's history for what happens when that goes wrong.)

## Manual restore procedures

These are for actual data loss — restoring `pg_dumpall` output over a live, populated database will conflict with existing objects. Don't run the Postgres procedure against a healthy `db`.

All of them start the same way: restore the latest snapshot into a scratch directory inside the `backup` container, then move the relevant piece into place.

```
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml exec backup sh -c '
  restic -r "$RESTIC_REPOSITORY_LOCAL" restore latest --target /tmp/dr-restore --tag daily
'
```

### Postgres

```
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml exec backup sh -c '
  DUMP=$(find /tmp/dr-restore -name postgres-dumpall.sql)
  PGPASSWORD="$POSTGRES_PASSWORD" psql -h db -U "$POSTGRES_USER" -d postgres -f "$DUMP"
'
```

`pg_dumpall` output includes its own `CREATE DATABASE` statements, so this replays cleanly against an empty Postgres instance.

### Grafana / Uptime Kuma

The `backup` container mounts these volumes read-only, so the restored file has to move through the host:

```
docker cp "$(docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml ps -q backup)":/tmp/dr-restore/tmp/grafana.db ./grafana.db.restore
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml stop grafana
docker cp ./grafana.db.restore "$(docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml ps -q grafana)":/var/lib/grafana/grafana.db
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml start grafana
rm ./grafana.db.restore
```

Same pattern for Kuma: substitute `kuma.db`, service `uptime-kuma`, and destination path `/app/data/kuma.db`.

### OpenSearch

```
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml exec backup sh -c '
  cp -a /tmp/dr-restore/mnt/opensearch-snapshots/. /mnt/opensearch-snapshots/
  curl -fsS -X PUT "http://opensearch:9200/_snapshot/aerie_backup" -H "Content-Type: application/json" -d "{\"type\":\"fs\",\"settings\":{\"location\":\"/mnt/snapshots\"}}"
  curl -fsS "http://opensearch:9200/_snapshot/aerie_backup/_all"
'
```

The last command lists the restored snapshot's name. Restore it with:

```
curl -fsS -X POST "http://opensearch:9200/_snapshot/aerie_backup/<snapshot-name>/_restore?wait_for_completion=true" -H "Content-Type: application/json" -d '{"indices":"*","include_global_state":true}'
```

(run from inside the `backup` container, same as above). If indices from before the loss still exist, OpenSearch refuses to restore over them — close or delete the conflicting indices first. Observability data is explicitly out of scope for HA in this stack (see `TODO_SWARM.md`), so this is the lowest-priority restore of the five.

### Prometheus

```
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml stop prometheus
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml exec backup sh -c '
  cp -a /tmp/dr-restore/mnt/prometheus/snapshots/*/. /mnt/prometheus/
'
docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml start prometheus
```

## Full disaster recovery (host is gone)

This is the Phase 0 gate: **do not start Phase 1 until this has actually been performed once**, on a genuine scratch VM, not just talked through.

1. Provision a scratch VM with Docker installed.
2. Check out this repo at the `main` commit currently in production.
3. Bring up the stack with the same env vars `cd.yml` provides (`DOMAIN`, `AWS_ACCESS_KEY_ID`/`AWS_SECRET_ACCESS_KEY` for Route53, `RESTIC_PASSWORD`, `RESTIC_AWS_ACCESS_KEY_ID`/`RESTIC_AWS_SECRET_ACCESS_KEY`, etc. — pull these from wherever they're stored offline, since GitHub Actions won't hand them back):
   ```
   docker compose -f compose.prod.yml -f compose.observability.yml -f compose.metrics.yml -f compose.backup.yml up -d
   ```
4. Point `RESTIC_REPOSITORY_S3` at the same bucket — the local repo won't exist on a fresh VM, so this run recovers from S3 only, which is the realistic scenario if the original host (and its local disk) is what was lost.
5. Run the manual restore procedures above for each service, sourcing from `$RESTIC_REPOSITORY_S3` instead of `$RESTIC_REPOSITORY_LOCAL`.
6. Verify: all services start, the API serves traffic, Grafana/Kuma/OpenSearch show restored data.
7. Record how long this actually took and anything that didn't go as documented — update this file if the real procedure diverged from what's written here.

## Known gap

Per `TODO_SWARM.md` goal 6: the local repo and the live host are in the same building on the same circuit — fire, flood, theft, or a bad surge takes out both at once. Until a UPS and a genuinely offsite second copy exist, S3 is the *real* second copy, not the third, and the full-DR procedure above (S3-only) is the one to trust.
