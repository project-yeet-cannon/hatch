# Plans

Work in progress. Everything here is a plan that is being executed — not a
description of how Aerie works today. That distinction is the whole reason this
directory is separate from the rest of [`docs/`](../): a plan is allowed to
contain decisions that haven't happened yet, gates that haven't been passed, and
phases marked `[ ]`. The architecture docs one level up are not.

## The lifecycle

1. **A plan starts as one file** — `docs/plans/<name>.md`. Goals, the decision
   table, the findings that came out of reading the repo, and a checklist.
2. **It becomes a directory when it outgrows one file.** The trigger is
   practical rather than a line count: when working on one phase means reading
   the other eight, or when the file is large enough that opening it is itself a
   cost. Split it into `docs/plans/<name>/design.md` — everything the phases
   share — plus one file per phase.
3. **It is dissipated when it closes out.** Delete the plan file or directory
   and move what survives into the permanent docs: architecture notes into the
   matching `docs/*-architecture.md`, runbooks into their own doc, and anything
   that was only ever scaffolding into nothing at all. A finished plan is not an
   archive — the knowledge either earned a permanent home or it didn't.

Step 3 is the one that's easy to skip and expensive to skip. A `plans/`
directory full of completed work is indistinguishable from a `plans/` directory
full of active work, which is how a plan directory stops being read.

## Current plans

| Plan | What it covers |
|---|---|
| [`swarm/`](swarm/design.md) | Moving from one Windows Docker host to a 3-node k3s cluster. Split per phase; phases 0–3 complete |
| [`video.md`](video.md) | Video capture and streaming |
| [`kiosk_brightness.md`](kiosk_brightness.md) | Backlight, idle dim and presence control for the wall tablets |
| [`immich.md`](immich.md) | Photos on Aerie: Immich, the bulk-disk substrate, the offsite archive, and the historical ingest |
| [`node-storage.md`](node-storage.md) | The VM disk layout: getting etcd onto a fast volume, and the root filesystem out of its own way |

## Conventions

- **Phase files carry a status line** under the H1, so a phase's state is
  visible without reading its checklist.
- **Links are relative** and resolve from the file's own location — two levels
  up to the repo root from `docs/plans/`, three from a plan subdirectory. The
  docs browser resolves them the same way the filesystem does.
- **Plans are portable too.** [`ethos.md`](../ethos.md) applies here: no
  hardcoded domains, addresses, or hostnames, in a plan any more than in the
  code it describes.
