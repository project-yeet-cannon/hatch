#!/usr/bin/env bash
#
# Hatch from a terminal, in this repository.
#
# Almost nothing happens here any more. Every command - board, next, queue,
# show, start, move, comment, pr, depends, ask, questions, answer, api, config,
# work and go-to-work - is a command of the `hatch` program in src/Hatch.Cli,
# and this file finds it, builds it if it has to, and hands over.
#
# It stopped being the CLI in AERIE-934. The reason is not that shell was the
# wrong language for `curl | jq`; it is that an operator who clones Hatch into
# their own house has no copy of this file on their PATH, and a tracker that can
# only be reached from one checkout is a tracker for one person. `hatch` is one
# binary, published for four platforms, with settings that live with the person
# rather than in a scripts/ directory. `make publish-hatch` makes them; the
# Runner page serves them.
#
# What is left here is two things that are genuinely this file's:
#
#   - The door. `./scripts/hatch.sh <anything>` still works, so CLAUDE.md, every
#     playbook, and every habit built in this repository keep their meaning.
#     Nothing here has an opinion about the arguments; they go through whole.
#
#   - The supervisor. `go-to-work` asks to be restarted as a newer build when
#     its own source changes under it, and a process cannot exec itself into a
#     newer build - something that outlives it has to compile the new source and
#     run it again. That something is this file, which was already sitting here
#     as the parent. See "The supervisor" below, and docs/hatch.md.
#
# Settings are the program's and are documented in `hatch --help`: an exported
# variable wins, then scripts/.env in this checkout, then the per-user file
# `hatch config` writes. Never a key in this repository, either way - Hatch
# ships to other operators, and a key in the artifact is one operator's key
# inherited by everybody who clones it.
#
#   ./scripts/hatch.sh --help     every command, from the program itself
#   ./scripts/hatch.sh <command>  ...and running one
#
# Bash 3.2 compatible on purpose - that is what macOS still ships as /bin/bash.

set -euo pipefail

# Where the repository is, whatever directory this was invoked from.
repo_root() { CDPATH= cd -- "$(dirname -- "$0")/.." && pwd; }

# The CLI as a built binary if there is one, and `dotnet run` if there is not.
#
# A published binary starts in milliseconds and a `dotnet run` spends a few
# seconds deciding whether to build first, which matters not at all for
# `go-to-work` - one launch, all night - and is the whole cost of every other
# command. So it prefers a binary and falls back rather than requiring anybody
# to publish one. HATCH_RUNNER_BIN names one directly, for a machine with no SDK.
runner_cmd() {
  local root bin
  root=$(repo_root)

  if [ -n "${HATCH_RUNNER_BIN:-}" ]; then
    [ -x "$HATCH_RUNNER_BIN" ] || {
      echo "hatch: HATCH_RUNNER_BIN is not executable: $HATCH_RUNNER_BIN" >&2; exit 1; }
    echo "$HATCH_RUNNER_BIN"
    return
  fi

  for bin in \
    "${root}/src/Hatch.Cli/bin/Release/net10.0/hatch" \
    "${root}/src/Hatch.Cli/bin/Debug/net10.0/hatch"
  do
    [ -x "$bin" ] || continue
    echo "$bin"
    return
  done

  command -v dotnet >/dev/null || {
    cat >&2 <<'MISSING'
hatch: needs either a build of src/Hatch.Cli or the dotnet SDK.

    make build-hatch          builds it once, and every command after is fast
    make publish-hatch        ...or one self-contained binary per platform
    export HATCH_RUNNER_BIN=… names one you already have
MISSING
    exit 1
  }

  echo "dotnet|run|--project|${root}/src/Hatch.Cli/Hatch.Cli.csproj|--"
}

# The command and how to launch it, in RUNNER_ARGV - a global because bash 3.2
# cannot return an array and this file is bash 3.2 on purpose. Resolved again
# before every launch, since a rebuild between two of them can put a binary
# where there was only the SDK.
runner_argv() {
  local cmd
  cmd=$(runner_cmd)

  case "$cmd" in
    *"|"*) IFS='|' read -r -a RUNNER_ARGV <<<"$cmd" ;;
    *)     RUNNER_ARGV=("$cmd") ;;
  esac
}

# Every command except `go-to-work`, through one door. HATCH_ROOT is the
# checkout: this script knows where it lives, and "which checkout" is the
# question the whole of AERIE-794 was about. The program only requires one for
# `work` and `go-to-work`, but naming it is right for all of them - a `show` run
# from a subdirectory of this repository is still a call about this repository.
#
# One command has nothing to carry forward and nothing to come back as, so it
# replaces this process rather than being watched by it.
exec_hatch() {
  runner_argv
  HATCH_ROOT="$(repo_root)" exec "${RUNNER_ARGV[@]}" "$@"
}

# ---- The supervisor ----

# `go-to-work` is run rather than exec'd, and this is the whole reason why: a
# loop that spends the night improving this repository is running the version it
# started with, and would be until somebody came and stopped it. Work that lands
# at one in the morning does not reach the run that wrote it.
#
# A process cannot exec itself into a newer build - relaunching the same binary
# relaunches the same code, and the new source has to be compiled by something
# that outlives the process being replaced. That something is this file, which
# was already sitting here as the parent. So the runner asks to come back by
# exiting 75, and everything interesting - deciding to restart, letting go of
# the claim, carrying the night's totals - stays in the runner, where
# `make test-hatch` can assert them.
#
# The totals travel in a file this names once per night: a restart that started
# the budget over would be a way of outspending `--max-spend` by restarting.
night_state=""

forget_night_state() {
  [ -n "$night_state" ] && rm -f "$night_state"
  return 0
}

supervise_go_to_work() {
  local status root
  root=$(repo_root)

  night_state=$(mktemp "${TMPDIR:-/tmp}/hatch-night.XXXXXX")

  # Nothing is ever backgrounded here, so there is nothing to orphan: a signal
  # reaches the runner directly, and bash runs the trap once the runner has
  # finished giving its ticket back.
  trap forget_night_state EXIT
  trap "forget_night_state; exit 130" INT
  trap "forget_night_state; exit 143" TERM

  while :; do
    runner_argv

    # Captured rather than read after the fact, because `set -e` would otherwise
    # end the night on the exit code that is asking for it to continue.
    status=0
    HATCH_ROOT="$root" HATCH_NIGHT_STATE="$night_state" \
      "${RUNNER_ARGV[@]}" go-to-work "$@" || status=$?

    [ "$status" -eq 75 ] || exit "$status"

    rebuild_runner
    echo "hatch: restarting" >&2
  done
}

# The new source, compiled - the one place in this file that builds anything.
#
# A build that fails is not the end of a night: the loop comes back on the
# binary that is there, and having taken its baseline at startup it will not ask
# again for the same change. So this says what went wrong and returns, always.
#
# `build-hatch` and not `publish-hatch`: this is on the hot path of an
# unattended loop reconnecting at two in the morning, and a single fast Release
# build of this machine's own platform is what it wants. The four-RID
# self-contained publish is a different, much slower operation aimed at somebody
# downloading the thing.
rebuild_runner() {
  local root cmd
  root=$(repo_root)

  if [ -n "${HATCH_RUNNER_BIN:-}" ]; then
    # A machine with no SDK on it. Restart anyway: hatch.sh itself may be what
    # changed, and this file is read fresh on the way back in.
    echo "hatch: HATCH_RUNNER_BIN names the binary, so there is nothing here to rebuild" >&2
    return 0
  fi

  cmd=$(runner_cmd)
  case "$cmd" in
    *"|"*)
      # The `dotnet run` fallback, which builds on its own. Building here would
      # leave a Release binary that the next launch prefers - a change nobody
      # asked for.
      return 0
      ;;
  esac

  echo "hatch: rebuilding" >&2

  if command -v make >/dev/null; then
    make -C "$root" build-hatch >&2 \
      || echo "hatch: the rebuild failed - carrying on with the binary that is there" >&2
  elif command -v dotnet >/dev/null; then
    dotnet build "${root}/src/Hatch.Cli/Hatch.Cli.csproj" --configuration Release >&2 \
      || echo "hatch: the rebuild failed - carrying on with the binary that is there" >&2
  else
    echo "hatch: neither make nor dotnet is here - carrying on with the binary that is there" >&2
  fi

  return 0
}

# `go-to-work` is the one command that is watched rather than replaced.
# Everything else - including a name that is not a command at all, which the
# program refuses better than a case statement here could - goes straight
# through.
case "${1:-}" in
  go-to-work) shift; supervise_go_to_work "$@" ;;
  *)          exec_hatch "$@" ;;
esac
