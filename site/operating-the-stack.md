---
title: Operating the stack
lede: Upgrading, backing up, restoring, stopping, and what to do when the board does not come up.
permalink: /operating-the-stack/
---

Everything on this page is about the board itself: the three containers that
`docker compose up -d` starts. The runner has [its own page](running-the-agent.md).

## What is running

| Service | What it is | Lifecycle |
|---|---|---|
| `db` | Postgres, with Hatch's own init | Always on. Its data is in the `hatch-local_pgdata` volume. |
| `migrate` | The API image in migration mode | Runs to completion on every start, then exits. **Exited is correct.** |
| `api` | The API and the board's SPA, on port 8080 | Starts only after `migrate` has completed successfully. |

```
docker compose ps
docker compose logs api
docker compose logs migrate
```

`api` restarting in a loop usually means the migration did not finish, and its
log says why.

## Upgrading

```
git pull
docker compose up -d --build
```

`git pull` brings the source up to date; `--build` makes `up -d` rebuild the
image from it rather than reusing the one you started with. Your data is
untouched. It is in a volume, and a volume is not part of an image. The
migration re-runs on every start and does nothing when there is nothing to do.

Two things to do after an upgrade:

- **Download the runner again** from the Runner page. It prints the revision it
  was built from, which is how you tell whether the one on your `PATH` is the
  one this board expects.
- **If you run the container runner**, add `--profile runner` to the line
  above. Compose only rebuilds and restarts the services the profile it was
  given turns on. Without it the runner container sits on the version you
  started with.

## Where the data is

Everything Hatch knows, every issue, comment, event and playbook, is in one
Postgres database called `hatch`, inside a named volume called
`hatch-local_pgdata`. Nothing lives in the images and nothing lives in a file on
your desktop. That is the whole reason `down` is safe and `down -v` is not.

## Backup

Do not back up by copying the volume. A file-level copy of a database directory
that is being written to has no consistency guarantee. Ask Postgres for the dump
instead. It is one command, the stack stays up, and what comes back is a text
file you can read:

```
docker compose exec -T db pg_dump -U user --clean --if-exists hatch > hatch-backup.sql
```

A board's worth of history is well under a megabyte. On Windows the `>` needs
PowerShell 7 or newer. PowerShell 5.1 writes UTF-16 there, which `psql` will
not read back.

## Restore

The dump drops every table before it recreates them, so the API is stopped for
the length of the restore:

```
docker compose stop api
docker compose exec -T db psql -U user -d hatch -v ON_ERROR_STOP=1 < hatch-backup.sql
docker compose start api
```

`ON_ERROR_STOP=1` turns a half-restore into a refusal. Without it `psql`
reports each failure and carries on to the next statement.

In PowerShell the restore line is different, because PowerShell has no `<`
input redirection:

```
Get-Content hatch-backup.sql | docker compose exec -T db psql -U user -d hatch -v ON_ERROR_STOP=1
```

## Stopping

```
docker compose down
```

`down` keeps your data. The containers go, the volume outlives them, and
`up -d` again brings back the same board. `down` also removes the runner
container whether or not you name its profile.

```
docker compose down -v
```

`down -v` throws your data away. There is no undo. Use it to start over from an
empty board, and not otherwise.

## If something is wrong

**The system cannot find the file specified, naming a pipe or a socket.** In
full it is `open //./pipe/dockerDesktopLinuxEngine` on Windows, or
`/var/run/docker.sock` on macOS. Nothing about the stack failed: Docker Desktop
is installed and not running. Start it and wait for its dashboard to say
**Engine running**. A fresh install does not start itself, and the first start
can take a couple of minutes. `docker version` printing a **Server** block as
well as a **Client** one is how you know it is ready. If the installer asked for
a sign-out or a reboot and did not get one, the group membership it added is
not in effect until then.

**Port 8080 is taken.** Move the board with `HATCH_PORT`:

```
HATCH_PORT=9090 docker compose up -d
```

In PowerShell: `$env:HATCH_PORT=9090; docker compose up -d`. Then point the
runner at the new address with `hatch config --origin`.

**The Runner page offers no downloads.** The build serving the page published
no runner binaries. That happens on an image built before the publish step, or
on a `dotnet run` from a checkout. Build them yourself with `make publish-hatch`
from the repository root, or rebuild the image.

## A psql shell

For looking at the database by hand:

```
docker compose exec db psql -U user -d hatch
```
