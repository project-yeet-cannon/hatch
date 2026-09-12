#!/bin/sh
# What has to be true before an increment can run, checked in the order a
# person would fix them.
#
# POSIX sh, not bash: the shell the runtime base guarantees.
set -eu

say() {
    echo "hatch-runner: $1" >&2
}

# ---- A checkout to work in ----
#
# A hard exit, not a wait. Nothing about this container can produce a checkout
# it was not given, so retrying would be a container that looks alive and does
# nothing - and `restart: on-failure` will bring it back the moment the person
# fixes the mount.
if [ -z "${HATCH_ROOT:-}" ] || [ ! -e "$HATCH_ROOT/.git" ]; then
    say "there is no checkout at ${HATCH_ROOT:-<unset>} - mount one there, or set HATCH_ROOT."
    say "  docker compose --profile runner up -d, with HATCH_CHECKOUT naming the repository."
    exit 1
fi

# The mount arrives owned by whoever owns it on the host, which is not this
# container's uid, and git refuses a repository it thinks belongs to somebody
# else. Named rather than the '*' wildcard: this is the one directory this
# container has any business in.
git config --global --add safe.directory "$HATCH_ROOT"

# ---- A name on the commits (criterion 5) ----
#
# Read natively by git, so no .gitconfig has to be mounted for this alone. A
# refusal rather than a default, because the default would be `root@` and some
# container's hostname - a commit that names nobody, in somebody's repository,
# forever.
if [ -z "${GIT_AUTHOR_NAME:-}" ] || [ -z "${GIT_AUTHOR_EMAIL:-}" ]; then
    say "set GIT_AUTHOR_NAME and GIT_AUTHOR_EMAIL - commits made in here carry them,"
    say "  and a commit that names nobody is worse than one that never happened."
    say "  In the compose file those are HATCH_GIT_NAME and HATCH_GIT_EMAIL."
    exit 1
fi

export GIT_COMMITTER_NAME="$GIT_AUTHOR_NAME"
export GIT_COMMITTER_EMAIL="$GIT_AUTHOR_EMAIL"

# ---- Something to push with (criterion 6) ----
#
# One of the three shapes needs anything from this script: a token in the
# environment, which git reads through an askpass helper. A mounted credential
# helper and an SSH agent socket arrive already configured - as a bind-mounted
# .gitconfig / .git-credentials, or as SSH_AUTH_SOCK - and wiring anything up
# for them here would override what the person mounted. src/Aerie.Web/apps/hatch/public/hatch-at-home.md
# shows all three, on both platforms.
if [ -n "${HATCH_GIT_TOKEN:-}" ]; then
    export GIT_ASKPASS=/usr/local/bin/git-askpass.sh
fi

# ---- The Claude credential (criterion 4) ----
#
# Fetched from Hatch rather than configured here, so a friend sets it on the
# Settings page and in exactly one place. Waiting rather than exiting is the
# whole of criterion 4: a stack brought up before anybody has pasted a token is
# the ordinary first five minutes, and a container that died over it would be a
# restart loop in the logs instead of one sentence.
#
# Two states are waited out and they say different things, each said once: no
# token saved yet (exit 2), and a call that did not get an answer at all - an
# API still starting, a wall that has since gone up (any other exit).
errors=/tmp/hatch-runner-token.err
told=""

while true; do
    code=0
    token=$(hatch runner-claude-token 2>"$errors") || code=$?

    if [ "$code" -eq 0 ]; then
        break
    fi

    if [ "$code" -eq 2 ]; then
        state=none
    else
        state=unreachable
    fi

    if [ "$state" != "$told" ]; then
        if [ "$state" = none ]; then
            say "no Claude token saved on Hatch's Settings page yet - waiting, and looking again every minute."
        else
            cat "$errors" >&2
            say "could not read the Claude token - waiting, and looking again every minute."
        fi
        told="$state"
    fi

    sleep 60
done

export CLAUDE_CODE_OAUTH_TOKEN="$token"
unset token

# exec, so the loop is pid 1 and gets the signal a `docker stop` sends: an
# increment interrupted mid-ticket lets go of its claim on the way out, and
# that only happens if the signal reaches it rather than this script.
#
# "$@" so the compose file - or `docker run` - can hand it bounds
# (--max-runs, --until, --under) without a second entrypoint.
exec hatch go-to-work "$@"
