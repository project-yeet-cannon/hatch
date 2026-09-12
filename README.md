# Hatch

A kanban board built to be worked by agents as much as by people: issues with
keys like `AER-12`, operator-editable columns, comments, an audit trail, and an
API a Claude session can reach directly. You hatch a plan here, and epics hatch
into stories into shipped work. The design is in [docs/hatch.md](docs/hatch.md);
the one rule every change in this repo has to hold is in
[docs/ethos.md](docs/ethos.md).

This repository is Hatch: the tracker, its API, its runner CLI, and the
[UI Design library](src/Hatch.Web/packages/ui) the board is built on.

## Run it

```
git clone <this-repo-url>
cd hatch
docker compose up -d
```

Open **http://localhost:8080/apps/hatch/**. First run takes a few minutes — it
builds the image, migrates the database, and only then starts the API.

Change the port with `HATCH_PORT`:

```
HATCH_PORT=9090 docker compose up -d
```

Stop it (`down` keeps your data; `down -v` throws it away):

```
docker compose down
```

The full walkthrough — naming yourself, backup and restore, upgrading,
troubleshooting — is
[hatch-at-home.md](src/Hatch.Web/apps/hatch/public/hatch-at-home.md), also
linked from the **Docs** nav item inside the running app.

## Get an agent working tickets

```
hatch config --origin http://localhost:8080   # writes to a per-user file, once
hatch go-to-work                               # the loop: next issue, one increment, repeat
```

`hatch` is the CLI in [`src/Hatch.Cli`](src/Hatch.Cli), published as one
binary for macOS, Windows and Linux. Once the board is up, download it from the
**Runner** page in the nav rather than building it yourself. Inside this
checkout, [`scripts/hatch.sh`](scripts/hatch.sh) reaches the same commands and
builds the CLI on demand if you have no binary yet:

```
./scripts/hatch.sh board      # what a session sees first
./scripts/hatch.sh next       # the top of the queue
./scripts/hatch.sh work       # one increment, unattended
```

Or run the runner as a container instead of a local binary — see the
`runner` service in [`compose.yaml`](compose.yaml) and
[hatch-at-home.md](src/Hatch.Web/apps/hatch/public/hatch-at-home.md):

```
HATCH_CHECKOUT=/path/to/a/checkout \
HATCH_GIT_NAME="Your Name" \
HATCH_GIT_EMAIL=you@example.org \
docker compose --profile runner up -d
```

## The UI Design library

`@hatch/ui` — tokens, day/night theming, and the components Hatch itself is
built from — plus its own browsable gallery app, both kept for their standalone
value. The gallery isn't part of the default stack; bring it up as a hot-
reloading dev server with its own profile:

```
docker compose --profile design up -d
```

Then open **http://localhost:5173/apps/design/**. See
[docs/design-system-architecture.md](docs/design-system-architecture.md) and
[src/Hatch.Web/apps/design/README.md](src/Hatch.Web/apps/design/README.md).

## Working on this repository

```
make build       # dotnet build
make test        # test-api + test-hatch + test-web
make run         # dotnet run, against a `make db` you started separately
```

Layout:

```
src/
  Hatch.Api          the API — Hatch's server half, at api/hatch/*
  Hatch.Cli          the `hatch` CLI: work, go-to-work, board, and the rest
  Hatch.Contracts    the wire types both of the above share
  Hatch.Web/
    apps/hatch       the board's own SPA, served at /apps/hatch/
    apps/design      the design gallery, served at /apps/design/
    packages/ui      @hatch/ui — the design system
containers/
  hatch-db           Postgres, with Hatch's own init
  hatch-runner       the CLI, packaged as a container (the `runner` profile)
```

Always `make`, never a bare `dotnet` — the npm step needs the shell profile.
Never commit a Hatch API key (`hatch_ak_…`) to this repository; see
[CLAUDE.md](CLAUDE.md) for where it lives instead.

For working *on the loop itself* from inside a Claude session — how a ticket is
read, claimed, implemented and handed back — see [CLAUDE.md](CLAUDE.md) and
[docs/hatch.md](docs/hatch.md).
