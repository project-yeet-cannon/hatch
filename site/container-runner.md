---
title: The container runner
lede: The same runner, packaged as a container beside the stack. What it can do, what it cannot, and how it gets its credentials.
permalink: /container-runner/
---

[Running the agent bare](running-the-agent.md) is the primary path. This page
is the shortcut: rather than putting a binary on your `PATH` and keeping a
terminal open, the same Compose stack can start a container that carries the
runner, `git` and the `claude` CLI, mounts one of your checkouts, and works
tickets under the control of the Runners page like any other runner.

It is off unless you ask for it. Nothing about the stack changes if you never
type the word `runner`.

## Whether it is for you

The container carries `git`, `openssh-client`, the `claude` CLI, and the
`hatch` binary. It carries **no language runtimes**: no .NET, no Node, no
Python, no compilers.

So it can plan, break work down, analyse a repository and write code in any
repository at all, and it can commit and push what it wrote. It **cannot build
or test** a repository whose toolchain it does not have, and no general image
has everybody's.

For a repository where "done" means a green build, which is most of them, the
bare runner is the one to use, because it works with whatever you already have
installed. The container is for the planning, breaking down and analysing that
make up a good part of a board's work, and for repositories whose tooling is
`git` and a text editor.

## Starting it

Two things it has to be told: which checkout to work in, and whose name goes
on the commits it makes.

macOS and Linux:

```
HATCH_CHECKOUT=/path/to/your/checkout \
HATCH_GIT_NAME="Your Name" \
HATCH_GIT_EMAIL=you@example.org \
docker compose --profile runner up -d
```

PowerShell:

```
$env:HATCH_CHECKOUT="C:\path\to\your\checkout"
$env:HATCH_GIT_NAME="Your Name"
$env:HATCH_GIT_EMAIL="you@example.org"
docker compose --profile runner up -d
```

It appears on the **Runners** page within a minute, called `hatch-runner`.
The controls there, pause, stop after this one, a spend cap, an hour to stop
at, work on it exactly as they do on a runner you started in a terminal.
Stopping it from that page stops the container too, rather than Docker
restarting it behind your back.

```
docker compose --profile runner logs -f runner
```

is what it is saying while it works.

## Its Claude credential

The container's Claude credential comes from the **Settings** page of your
Hatch, and from nowhere else. There is nothing to log in to inside the
container.

1. On a machine with the `claude` CLI logged in, run `claude setup-token` and
   authorise it in the browser prompt that opens.
2. It prints a token starting with `sk-ant-oat-`. Copy it.
3. Paste it into **Settings → Claude subscription token** and save.

The container picks it up on its next look. If none is saved when it starts,
it says so once and then waits, looking again every minute, so the order you
do these two things in does not matter. The same token is what lets the nav
strip show how much headroom the account has left.

## Letting it push

The container starts with no credential of its own, so pushing needs one of
these three. All three are ones you already have.

**A token in the environment.** The simplest, and the only one that needs no
edit to the compose file. `HATCH_GIT_TOKEN` is handed to `git` when a push
over HTTPS asks for a password:

```
HATCH_GIT_TOKEN=ghp_... HATCH_CHECKOUT=... docker compose --profile runner up -d
```

**The credential helper you already have.** If `git push` works from your own
terminal over HTTPS without asking, something is already holding that
credential, and it can be mounted read-only. Uncomment these two lines under
the runner's `volumes:` in `compose.yaml`:

```
      - "${HOME}/.gitconfig:/root/.gitconfig:ro"
      - "${HOME}/.git-credentials:/root/.git-credentials:ro"
```

On Windows, `${HOME}` is `${USERPROFILE}`. This shape only carries what a
*file* holds. A helper that keeps the credential somewhere else, macOS's
Keychain or Windows' Credential Manager, has nothing in `.git-credentials` to
mount, and there a token in the environment is the answer.

**An SSH key.** If your remote is `git@…` rather than `https://…`, the
container needs a key. On macOS, Docker Desktop bridges your own running
`ssh-agent` into a container at a fixed path, so no key ever leaves the host.
Uncomment under `volumes:`, and add the matching line under `environment:`:

```
      - "/run/host-services/ssh-auth.sock:/ssh-agent"
```

```
      SSH_AUTH_SOCK: /ssh-agent
```

On Windows that bridge is not available to a Linux container, so the key
itself is mounted instead, read-only, and pointed at with `GIT_SSH_COMMAND`:

```
      - "${USERPROFILE}/.ssh/id_ed25519:/root/.ssh/id_ed25519:ro"
```

```
      GIT_SSH_COMMAND: "ssh -i /root/.ssh/id_ed25519 -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new"
```

The same two lines work on macOS and Linux for a key that has no agent holding
it, with `${HOME}` in place of `${USERPROFILE}`.

## What the container does at start

Its entrypoint checks what has to be true before an increment can run, in the
order a person would fix them:

1. **A checkout at `/checkout`.** A hard exit if there is none. The container
   cannot produce a checkout it was not given, and `restart: on-failure` brings
   it back the moment the mount is fixed. The directory is added to git's
   `safe.directory`, because the bind mount arrives owned by the host's user.
2. **A name on the commits.** A refusal, not a default, if `HATCH_GIT_NAME` or
   `HATCH_GIT_EMAIL` is missing. The default would be `root@` and a container
   hostname on a commit in your repository, forever.
3. **Something to push with.** If `HATCH_GIT_TOKEN` is set, an askpass helper
   answers git's username and password prompts with it. The other two shapes
   arrive already configured through the mounts above.
4. **The Claude credential.** Fetched from Hatch, waited for if absent, as
   described above.
5. `exec hatch go-to-work`, as pid 1, so a `docker stop` reaches the loop and
   an increment interrupted mid-ticket lets go of its claim on the way out.

Anything after `up -d` on the compose command line is not passed through; to
hand the loop bounds such as `--max-runs` or `--under`, add a `command:` to the
`runner:` service. The Runners page controls are usually the easier route.

## The variables

| Variable | Meaning | Default |
|---|---|---|
| `HATCH_CHECKOUT` | The repository to mount at `/checkout` | The directory the compose file was reached from, which is right only inside the Hatch checkout itself |
| `HATCH_GIT_NAME` | `GIT_AUTHOR_NAME` inside the container | Required |
| `HATCH_GIT_EMAIL` | `GIT_AUTHOR_EMAIL` inside the container | Required |
| `HATCH_GIT_TOKEN` | An HTTPS push token, handed to git through askpass | Empty |
| `HATCH_RUNNER_NAME` | What the Runners page calls it | `hatch-runner` |

Inside the container `HATCH_BASE` is the API over Compose's own network, and
`HATCH_ROOT` is `/checkout`. The image sets `IS_SANDBOX=1`, because the
`claude` CLI refuses to bypass permission prompts as root unless it is told it
is in a sandbox, and an unattended increment has nobody to answer a prompt. A
container with one bind-mounted checkout in it is the isolation that guard
asks after.

## One container, one checkout

For a second repository, copy the `runner:` block in the compose file under a
second service name with its own `HATCH_RUNNER_NAME` and its own checkout
mounted, and both appear on the Runners page as themselves.

## Upgrading it

The container carries the binary its image was built with, so it does not
rebuild itself between increments the way the bare runner does. Bring it
forward with the stack:

```
git pull
docker compose --profile runner up -d --build
```

Published images are also pushed to the GitHub container registry under the
repository owner's namespace as `hatch-runner`, for `linux/amd64` and
`linux/arm64`, on every green build of the default branch.

## Restart policy

The runner service uses `restart: on-failure`, deliberately. `go-to-work`
exits 0 when it was told to stop, from the Runners page or by a bound it was
given, and non-zero when something actually went wrong. `unless-stopped` would
restart the container after a deliberate stop and undo the button that stopped
it. `on-failure` obeys the board and still recovers from a crash.
