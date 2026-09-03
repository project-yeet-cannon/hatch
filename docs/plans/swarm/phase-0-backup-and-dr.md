[Design & decisions](design.md) · [Phase 1 →](phase-1-node-substrate.md)

---

# Phase 0 — Backup + DR on the current host

**Status: Complete**

*No cluster involved. Delivers goals 4 and 5 immediately and de-risks everything
after it.*

## Phase 0 — The steps

- [x] restic repos: local (second disk) + AWS S3 — `containers/backup/`
      (built on `postgres:18.4-alpine` for a version-matched `pg_dumpall`),
      wired in as `compose.backup.yml`. `cd.yml` inits both repos idempotently
      on every deploy (`restic snapshots` fails → `restic init`)
- [x] Generate the repo password, store as a **GitHub Actions secret**
      (`RESTIC_PASSWORD`), and **print it once for offline storage** — a
      backup you can't decrypt isn't one. *(No longer an exception — this **is**
      the permanent root of trust. GitHub Actions secrets match the pattern
      already used for the Route53/HA credentials — `RESTIC_PASSWORD` /
      `RESTIC_AWS_ACCESS_KEY_ID` / `RESTIC_AWS_SECRET_ACCESS_KEY`, kept
      separate from caddy's Route53 credentials via a dedicated `aerie-restic`
      IAM user, scoped to only the backup bucket. Phase 2 puts ESO downstream
      of these rather than replacing them; the printed copy is what the whole
      bootstrap chain hangs from.)*
- [x] Back up correctly per service, not by copying volume directories:
      `pg_dumpall` for Postgres, `sqlite3 .backup` for Grafana and Kuma, the
      OpenSearch snapshot API, the Prometheus TSDB snapshot endpoint —
      `containers/backup/scripts/backup.sh`, daily via cron (supercronic)
- [x] Retention `--keep-daily 7 --keep-weekly 4 --keep-monthly 12`, scheduled —
      same script, run against both repos after every backup
- [x] Restore-verification job: restore the newest snapshot into a scratch
      Postgres and run a sanity query. Untested backups are not backups —
      `containers/backup/scripts/verify-restore.sh`, weekly via cron, using
      `initdb`/`pg_ctl` from the image's own postgres install (no extra
      container or docker-socket access needed)
- [x] Write `docs/disaster-recovery.md`
- [x] **Perform one full restore onto a scratch VM**.

> **Gate:** do not start Phase 1 until a restore has actually been performed.
> The weekly verification job above proves the backup *contents* are valid —
> it restores into a throwaway Postgres inside the backup container, not a
> standalone VM, so it does not by itself satisfy this gate.

