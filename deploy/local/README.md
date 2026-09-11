# Hatch on your computer

One command, no checkout of this repository, no account anywhere. It pulls two
published images, migrates a database, and serves the board.

## What you need

- **Docker Desktop**, running.
- **On Windows: either PowerShell** - 5.1, the blue one that ships with the OS,
  or 7 (`pwsh`). Every line here runs in both.

## Start it

```
docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local up -d
```

Then open **http://localhost:8080/apps/hatch/**.

The same line in both PowerShells and in a macOS terminal, character for
character. The first run takes a few minutes: it pulls the images, runs the
migration to completion, and only then starts the API. Compose has no way to
tell you the address once it has gone to the background, which is why it is
written here rather than printed in your terminal.

## Stop it

```
docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local down
```

**`down` keeps your data.** Your issues, comments and board live in a named
volume (`pgdata`) that outlives the containers, so `down` and then `up -d`
again - even after pulling newer images - brings back the same board. The
migration re-runs on every start and does nothing when there is nothing to do.

**`down -v` throws your data away.** The `-v` removes that volume, and there is
no undo. Use it when you want to start over from an empty board, and not
otherwise.

## Upgrading

```
docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local pull
docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local up -d
```

The file names `:latest` for both images, so `pull` fetches whatever the
project has published since you started. Your data is untouched: the volume is
not part of the image.

## Settings

Two things are worth knowing about, and both are environment variables read at
start.

| Variable | Default | What it does |
| --- | --- | --- |
| `HATCH_PORT` | `8080` | The port on your machine. Change it if something else already has 8080. |
| `Auth__LocalPerson__Name` | your OS username, else `friend` | The name Hatch puts in the nav bar and on everything you do. |

The name needs no setting in the ordinary case: the stack reads `USERNAME` on
Windows and `USER` on macOS, whichever your shell already has. If neither is
set you are `friend`, and the nav bar says which variable to set.

Overriding one is the only place the two shells differ - the start command
above does not, but this does:

macOS / Linux:

```
HATCH_PORT=9090 docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local up -d
```

PowerShell:

```
$env:HATCH_PORT=9090; docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local up -d
```

Same shape for `Auth__LocalPerson__Name`. Today the name changes only by
setting that variable and starting the stack again; a Settings page inside
Hatch that changes it without a restart is on its way.

## What is running

Three services, on a private network of their own:

- **`db`** - Postgres. It publishes **no port**: nothing outside the stack can
  reach your database, and the API talks to it over the compose network.
- **`migrate`** - the API image in its migration mode. It runs once, brings the
  schema up to date, and exits. The API waits for it to finish rather than
  starting alongside it.
- **`api`** - the API and every app it serves, including Hatch itself, on
  `http://localhost:8080` unless you changed `HATCH_PORT`.

## If something is wrong

```
docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local ps
docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local logs api
docker compose -f oci://ghcr.io/eouw0o83hf/hatch-local logs migrate
```

`migrate` showing as exited is correct - that is a finished migration, not a
crash. `api` restarting in a loop usually means the migration did not finish;
its log says why.
