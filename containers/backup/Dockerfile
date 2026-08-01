# postgres:18.4-alpine gives us pg_dumpall/psql AND initdb/pg_ctl/postgres,
# version-matched exactly to the `db` service (Finding: pg_dump must be >=
# the server's major version, so this can't float independently of db's tag).
# initdb/pg_ctl are used by scripts/verify-restore.sh to stand up a throwaway
# instance for restore verification, entirely inside this container.
FROM postgres:18.4-alpine

RUN apk add --no-cache restic sqlite curl ca-certificates

ARG SUPERCRONIC_VERSION=v0.2.48
ARG SUPERCRONIC_SHA256=88c1b66b94c486f972fdd1a4d1f901e3e75ff04f749cddd60c5db573e3a33c6c
RUN curl -fsSLo /usr/local/bin/supercronic \
      "https://github.com/aptible/supercronic/releases/download/${SUPERCRONIC_VERSION}/supercronic-linux-amd64" \
    && echo "${SUPERCRONIC_SHA256}  /usr/local/bin/supercronic" | sha256sum -c - \
    && chmod +x /usr/local/bin/supercronic

WORKDIR /app
COPY scripts/ scripts/
COPY crontab crontab
RUN chmod +x scripts/*.sh

# The base image's own entrypoint (which does the root->postgres privilege
# drop via gosu) is bypassed since ENTRYPOINT is overridden below, so the
# privilege drop is done here instead — initdb/postgres refuse to run as root.
USER postgres

ENTRYPOINT ["supercronic", "/app/crontab"]
