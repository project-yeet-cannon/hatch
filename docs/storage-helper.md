# Storage Helper

## Summary

An index of what's in the house and where. Boxes get a printed code taped to
them; the code resolves to a page listing what's inside; a flat search across
every box answers the only question the app exists for — *where is the drill?*

It is the first app on the family-apps platform described in
[`TODO_APPS.md`](../TODO_APPS.md), and it is deliberately the whole of that
platform's proof: a module folder in `Aerie.Api`, a module folder in the family
shell, one line in a registry. It adds no container, no database, no ingress, no
backup entry, and no uptime monitor — it inherits all of them.

Two things follow from that and shape everything below:

- **One process, one database.** The backend is a module in `Aerie.Api` with its
  own `storage` Postgres schema and its own migration history. Splitting it out
  later is a connection string, not a rewrite.
- **Tailnet only, no auth.** Two trusted adults, no public ingress. Worth being
  plain about: a full index of what's in your house and where is a burglary aid,
  so the tailnet boundary is doing real security work here, not just saving a
  login screen. The tripwire for building real auth is in `TODO_APPS.md`.

## Data model

Three tables in the `storage` schema — `src/Aerie.Api/Modules/Storage/Entities.cs`.

```
storage.locations  id, name, description, created_at
storage.crates     id, code (unique), label, location_id → locations (SET NULL),
                   notes, created_at, updated_at
storage.items      id, crate_id → crates (CASCADE), name, quantity, notes,
                   created_at, updated_at
```

The two delete behaviours are the interesting part, and they point opposite ways
on purpose:

- **Deleting a location does not delete its crates.** They go unplaced. Losing a
  shelf must not silently lose the record of everything that was on it, so the
  UI says so in the confirm prompt.
- **Deleting a crate takes its items with it.** An item with no box has no answer
  to the only question this app asks.

Locations are flat. Nesting is a guess until someone actually misses it, and a
flat list is what a phone screen wants anyway.

## Crate codes

Six characters of [Crockford base32](https://www.crockford.com/base32.html) —
the digits plus the letters, minus `I`, `L`, `O` and `U`. Stored bare and
uppercase (`R83018`); displayed and printed in halves (`R83-018`). The dash is
presentation only and never stored. See
`src/Aerie.Api/Modules/Storage/CrateCode.cs`.

Every choice here is about a label that has been in a garage for a decade:

- `I`, `L` and `O` are dropped because they're indistinguishable from `1`, `1`
  and `0` on a worn label. `U` is dropped so six random characters can't spell
  something unfortunate.
- `CrateCode.Normalize` accepts what a person reads *off* a bad label —
  lowercase, with or without the dash, and with `I`/`L`/`O` typed in place of
  the digits they resemble — so typing the code is a real recovery path when the
  QR won't scan.
- Codes are generated server-side with a cryptographic RNG and are never
  editable, because the authoritative copy is on tape on a box. 32⁶ is ~1.07
  billion, so a household will never see a collision; `StorageService` retries on
  one anyway rather than surfacing a unique violation to someone standing at a
  label printer.

## API

All under `/api/storage`, in `StorageController.cs`. Plain CRUD for
`locations`, `crates` and `items`, plus two endpoints that exist for specific
physical workflows:

| Endpoint | Why it exists |
|---|---|
| `GET /crates/by-code/{code}` | The scan path. Takes whatever a QR carries or a person types; returns the crate **and its items** in one response, so the scan destination never needs a second round trip. |
| `POST /crates/batch { count }` | Mints N blank crates for a label sheet. The workflow that matters is print → tape onto empty boxes → scan each one as it gets filled. Creating a crate in the app before the box physically exists is backwards. Capped at 200 per call so a typo can't mint ten thousand. |

`GET /items` is the flat index: each row carries its crate code, crate label and
location name, so the "where is the drill" screen draws from one request and
needs no follow-up lookups.

## The app

`src/Aerie.Web/apps/family/src/modules/storage/` — a module inside the family
shell PWA, served at `/apps/family/storage/`. It styles itself out of the
shell's theme tokens and nothing else; that is the whole mechanism keeping the
suite looking like one product.

| Screen | Route | Notes |
|---|---|---|
| Item index | `/items` | The front door. Flat list across every crate, each row showing crate + location. Filtering is client-side over the whole index (see below). |
| Crate | `/crates/{id}` and `/c/{code}` | Label, location, notes, item list, and an always-open add-item row. One component, two routes: `c/{code}` is what a label carries, `crates/{id}` is what in-app lists link to. |
| Crate list | `/crates` | Grouped by location, for "what's in the attic". Unplaced crates are their own group at the end rather than hidden. |
| Locations | `/locations` | Flat CRUD. |

Design constraints worth stating, because they explain choices that otherwise
look arbitrary: this is used **standing in a garage, one-handed, holding a box**.
So every tappable thing clears 44px, the add-item field is sticky and keeps
focus after each save (unpacking a box is a run of items, not one), and Delete
sits at the opposite edge of the form from Save.

### Searching

Client-side substring matching over the loaded index, where every
whitespace-separated term must match somewhere on the row — item name, notes,
crate label, crate code or location name. That's what makes `drill garage` work,
which is how people actually recall where something is: by the thing and the
place, not either alone.

This is deliberate at household scale. A few hundred items is a small payload,
it filters faster than anyone types, and it keeps working with no network once
the service worker has the list. `TODO_APPS.md` Phase 5 replaces it with
Postgres full-text (`GET /api/storage/search`), which buys stemming and
misspelling tolerance at the cost of the offline half.

### Offline

The family shell's service worker is cache-first for the app shell and
network-first for `/api` GETs, so a crate you've already opened still opens with
no signal — the common case in the far corner of a garage. Offline *writes* are
out of scope; they need conflict resolution nothing here justifies yet, so a save
with no network fails and says so.

## Deferred

Named so they're decisions rather than oversights. Full reasoning in
[`TODO_APPS.md`](../TODO_APPS.md).

- **QR labels and the print sheet** (Phase 4) — the label encodes a full URL, so
  the stock iOS camera opens the crate page with nothing installed.
- **Postgres full-text search** (Phase 5).
- **Photos of crate contents** — the most valuable v2 feature for an app of this
  kind, deferred on timing: blob storage should land on Longhorn after the k3s
  cutover rather than on the current host's disk and then get migrated.
- **Auth** — see the tripwire in `TODO_APPS.md`.
