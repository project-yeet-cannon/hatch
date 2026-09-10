#!/bin/sh
# What git asks when a push needs a password, answered from the environment.
#
# git calls GIT_ASKPASS with a prompt and takes the one line it writes back. It
# asks twice over an HTTPS remote - once for the username, once for the
# password - and a token works as either against every forge that takes one, so
# this answers both with the same thing rather than trying to read the prompt.
#
# Only wired up when HATCH_GIT_TOKEN is set; see entrypoint.sh, and
# docs/hatch-at-home.md for the two credential shapes that need none of this.
echo "${HATCH_GIT_TOKEN:-}"
