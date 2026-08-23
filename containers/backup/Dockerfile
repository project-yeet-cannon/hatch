# postgres:18.4-alpine gives us pg_dump/pg_restore/psql AND initdb/pg_ctl/
# postgres, version-matched exactly to the CNPG cluster
# (deploy/cluster/data/cluster/cluster.yaml's
# ghcr.io/cloudnative-pg/postgresql:18.4). Finding: pg_dump must be >= the
# server's major version, so this tag can't float independently of that one.
# initdb/pg_ctl are used by scripts/cluster-verify.sh to stand up a throwaway
# instance for restore verification, entirely inside this container.
FROM postgres:18.4-alpine

# One image, both directions: this is also the image
# deploy/cluster/data/schema/restore-job.yaml pulls, which is what keeps
# pg_dump and pg_restore version-matched to each other as well as to the
# cluster. Four packages, one reason each:
#   restic    - the backup and verify jobs themselves
#   sqlite    - provides the sqlite3 binary. Nothing dumps SQLite on a
#               schedule any more (the cluster plan Phase 8's finding 1 moved
#               Grafana's and Kuma's databases onto Longhorn's backup target),
#               but the Phase 8b.15 rehearsal restores one of those volume
#               backups and then has to prove the database inside it opens,
#               and this is the image with a shell in it.
#   aws-cli   - scripts/export-parameters.sh, which pulls the /aerie/*
#               parameter tree into the same snapshot as the dumps.
#   curl      - ad-hoc probing from inside the cluster's network position.
RUN apk add --no-cache restic sqlite aws-cli curl ca-certificates

WORKDIR /app
COPY scripts/ scripts/
RUN chmod +x scripts/*.sh

# The base image's own entrypoint (which does the root->postgres privilege
# drop via gosu) is bypassed since ENTRYPOINT is overridden below, so the
# privilege drop is done here instead - initdb/postgres refuse to run as root.
USER postgres

# A shell, not a scheduler. This image used to run supercronic against its own
# crontab; the schedule now belongs to the CronJobs in
# deploy/cluster/data/backup/, because a container that schedules itself
# inside a cluster that schedules containers is two schedulers disagreeing
# about what "daily" means. Each Job names the script it wants
# (`command: ["sh", "/app/scripts/cluster-backup.sh"]`), and a bare
# `docker run` of this image drops you into a shell rather than idling.
ENTRYPOINT ["sh"]
