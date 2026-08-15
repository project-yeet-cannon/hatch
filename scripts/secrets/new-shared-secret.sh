#!/usr/bin/env bash
#
# Mints a random shared secret for one of the repository secrets Aerie
# generates rather than receives.
#
# Most values in scripts/secrets/parameters.json come from somewhere else: a
# Home Assistant token comes from Home Assistant, an AWS key pair comes from
# IAM. A few have no issuer at all - both sides just have to hold the same
# string - and VM_LOG_SHIPPER_TOKEN is the one you hit first, because
# Provision 2 marks it required and refuses to seed the parameter tree without
# it. There is nowhere to go and fetch it; you make it up. This makes "make it
# up" reproducible, and keeps the value off the command line.
#
# Bash 3.2 compatible on purpose - that is what macOS still ships as /bin/bash.
#
# Usage:
#   ./new-shared-secret.sh                    # print a token
#   ./new-shared-secret.sh --set              # write it straight to the repo
#   ./new-shared-secret.sh --name X --bytes 48
#
# See scripts/secrets/README.md.

set -euo pipefail

name='VM_LOG_SHIPPER_TOKEN'
bytes=32
do_set=0
repo=''

usage() {
    cat >&2 <<'EOF'
Usage: new-shared-secret.sh [--name NAME] [--bytes N] [--set] [--repo OWNER/REPO]

  --name NAME    Repository secret to name in the output, and to write with
                 --set. Default: VM_LOG_SHIPPER_TOKEN
  --bytes N      Entropy in bytes, rendered as 2N hex characters.
                 Default: 32 (256 bits)
  --set          Write the value to the repository secret with `gh secret set`
                 instead of printing it. Needs the GitHub CLI, authenticated.
  --repo         Passed through to gh. Default: the repo this checkout points at.

Without --set the token is the only thing on stdout, so it composes:

  TOKEN=$(./new-shared-secret.sh)
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --name)  name="${2:-}";  shift 2 ;;
        --bytes) bytes="${2:-}"; shift 2 ;;
        --repo)  repo="${2:-}";  shift 2 ;;
        --set)   do_set=1;       shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage; exit 2 ;;
    esac
done

case "$name" in
    ''|*[!A-Za-z0-9_]*)
        echo "--name must be a GitHub secret name (letters, digits, underscore): '$name'" >&2
        exit 2 ;;
esac
case "$bytes" in
    ''|*[!0-9]*) echo "--bytes must be a number: '$bytes'" >&2; exit 2 ;;
esac
if [ "$bytes" -lt 16 ]; then
    echo "--bytes below 16 (128 bits) isn't worth generating - refusing." >&2
    exit 2
fi

# Hex rather than base64: this value ends up as an HTTP header, as a Scheduled
# Task argument on a Windows host, and in a compose environment variable. Hex
# has no characters any of those three quote, escape, or line-wrap. The cost is
# length, which nothing here reads by hand.
if command -v openssl >/dev/null 2>&1; then
    token="$(openssl rand -hex "$bytes")"
else
    # Fallback for a machine with no openssl. /dev/urandom is the same CSPRNG
    # openssl seeds from on macOS and Linux; xxd ships with vim, which both
    # have by default.
    token="$(dd if=/dev/urandom bs=1 count="$bytes" 2>/dev/null | xxd -p | tr -d '\n')"
fi

expected_length=$((bytes * 2))
if [ "${#token}" -ne "$expected_length" ]; then
    echo "Generated $((${#token} / 2)) bytes, expected $bytes. Refusing to hand out a short secret." >&2
    exit 1
fi

if [ "$do_set" -eq 0 ]; then
    # Value on stdout, everything else on stderr, so command substitution gets
    # the token and nothing else.
    printf '%s\n' "$token"
    cat >&2 <<EOF

Add this as the '$name' repository secret:
  Settings > Secrets and variables > Actions > Secrets tab

Or re-run with --set to write it directly. Note it is now in this terminal's
scrollback and, depending on your shell, its history.
EOF
    exit 0
fi

if ! command -v gh >/dev/null 2>&1; then
    echo "--set needs the GitHub CLI (https://cli.github.com), which isn't on PATH." >&2
    exit 1
fi

# Piped, not passed as --body: an argument is visible in the process list to
# everything else on the machine. Same rule as Sync-AerieSecrets.ps1's
# --cli-input-json.
if [ -n "$repo" ]; then
    printf '%s' "$token" | gh secret set "$name" --repo "$repo"
else
    printf '%s' "$token" | gh secret set "$name"
fi

cat >&2 <<EOF

Wrote a fresh $((bytes * 8))-bit value to the '$name' repository secret. It was
not printed - GitHub cannot show it back to you either, so re-run this to
replace it if you need a copy.

This is a shared secret: every consumer has to be handed the new value before
it works end to end. For VM_LOG_SHIPPER_TOKEN that means re-running the deploy
(cd.yml) so Aerie.Api picks it up, and Provision 2 so the parameter tree does.
Hyper-V hosts get it on the next Provision 0 run for a given VM.
EOF
