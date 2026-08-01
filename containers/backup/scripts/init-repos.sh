#!/bin/sh
# Idempotently initializes both restic repositories. Lives in the image (not
# inlined into cd.yml) so no shell source ever crosses the Windows runner ->
# Linux container boundary, where PowerShell's stdin encoding (UTF-8 BOM) and
# CRLF line endings corrupt it. CI invokes this by path, as a fixed argv.
#
# `restic init` errors against an already-initialized repo, so this checks
# first — every deploy after the first is a no-op. `cat config` is the precise
# "is this repo initialized" probe and, unlike `snapshots`, doesn't list the
# whole index just to answer it.
set -eu

# Refuse to run against blank config rather than silently doing the wrong
# thing: a repo initialized with an empty password is unopenable by the real
# backup job, and would only surface as a failure at restore time.
: "${RESTIC_PASSWORD:?is empty — refusing to init (would create a repo the backup job cannot open)}"
: "${RESTIC_REPOSITORY_LOCAL:?is empty — refusing to init}"
: "${RESTIC_REPOSITORY_S3:?is empty — refusing to init}"

for REPO in "$RESTIC_REPOSITORY_LOCAL" "$RESTIC_REPOSITORY_S3"; do
  if restic -r "$REPO" cat config >/dev/null 2>&1; then
    echo "[init-repos] $REPO already initialized"
  else
    echo "[init-repos] initializing $REPO"
    restic -r "$REPO" init
  fi
done
