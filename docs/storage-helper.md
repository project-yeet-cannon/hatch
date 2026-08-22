# Storage Helper

## Summary

An index of what's in the house and where. Boxes get a printed code taped to
them; the code resolves to a page listing what's inside; a flat search across
every box answers the only question the app exists for — *where is the drill?*

It is the first app on the family-apps platform described in
[`family-apps-architecture.md`](family-apps-architecture.md), and it is
deliberately the whole of that
platform's proof: a module folder in `Aerie.Api`, a module folder in the family
shell, one line in a registry. It adds no container, no database, no ingress, no
backup entry, and no uptime monitor — it inherits all of them.

Two things follow from that and shape everything below:

- **One process, one database.** The backend is a module in `Aerie.Api` with its
  own `storage` Postgres schema and its own migration history. Splitting it out
  later is a connection string, not a rewrite.
- **Behind the house wall, and behind the tailnet.** Every route here is gated
  by a device grant ([`auth-architecture.md`](auth-architecture.md)) on top of
  the tailnet boundary that was, for a while, the only thing protecting it.
  Worth being plain about why that mattered: a full index of what's in your
  house and where is a burglary aid. One consequence to know about is
  [printed crate labels](#a-cold-scan-hits-the-wall).

## Data model

Three tables in the `storage` schema — `src/Aerie.Api/Modules/Storage/Entities.cs`.

```
storage.locations  id, name, description, created_at
storage.crates     id, code (unique), label, location_id → locations (SET NULL),
                   notes, created_at, updated_at
storage.items      id, crate_id → crates (CASCADE), name, quantity, notes,
                   search_vector (generated, GIN), created_at, updated_at
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
| `GET /search?q=` | Full-text search — see [Searching](#searching). Returns the same rows as `GET /items`, so the list screen and the match screen are one screen. |

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
| Item index | `/items` | The front door. Flat list across every crate, each row showing crate + location. Typing searches the server; with no network it filters the loaded list instead (see below). |
| Crate | `/crates/{id}` and `/c/{code}` | Label, location, notes, item list, and an always-open add-item row. One component, two routes: `c/{code}` is what a label carries, `crates/{id}` is what in-app lists link to. |
| Crate list | `/crates` | Grouped by location, for "what's in the attic". Unplaced crates are their own group at the end rather than hidden. |
| Locations | `/locations` | Flat CRUD. |
| Labels | `/labels` | The print sheet — see below. Not a tab: it's a thing you do once, at a desk, and it's reached from the crate list or a crate. |

Design constraints worth stating, because they explain choices that otherwise
look arbitrary: this is used **standing in a garage, one-handed, holding a box**.
So every tappable thing clears 44px, the add-item field is sticky and keeps
focus after each save (unpacking a box is a run of items, not one), and Delete
sits at the opposite edge of the form from Save.

### Labels

The sheet is the workflow the app is built around: mint N blank crates, print,
tape them onto empty boxes, *then* scan each one as it gets filled. Everything
about it is shaped by the label outliving the software.

- **The QR encodes a full URL** — `{PublicBaseUrl}/apps/family/storage/c/{code}`
  — so the stock iOS camera opens the crate page with nothing installed and no
  account. That is the single most valuable property of the whole app, and it's
  free.
- **The printed code is the recovery path.** It sits beside the QR in mono at a
  size you can read at arm's length, because a scuffed QR is a dead end and
  `CrateCode.Normalize` will take whatever someone types off a worn label.
- **Paper is a setting, not an assumption.** Page size (Letter default, A4) and
  the column × row grid are UI settings kept in `localStorage`, which is where a
  fact about someone's printer belongs. No label product is assumed: it's plain
  paper, dashed cut lines and tape.
- **The sheet previews at true physical size** — it's laid out in `mm`, so the
  preview and the paper are the same thing, and an under-sized QR costs a glance
  rather than a sheet.
- **Which crates a sheet is for lives in the URL** (`?code=…`). A minted batch
  survives a refresh: by then the crates exist, and losing the list of codes
  would mean boxes with no way back to them. Reprinting one faded label is the
  same screen with one code in it, from the crate's edit form.

Codes are generated at 25% error correction (`Q`) rather than the default,
because the expected conditions are dust, scuffs and a photocopier. The encoder
is a dynamic `import()`, so the ~30 kB of it never loads on the path that
matters — pointing a camera at a box.

#### A cold scan hits the wall

The stock-camera property above survived the arrival of auth, but it costs one
tap now. `/apps/family/storage/c/{code}` is behind the house wall
([`auth-architecture.md`](auth-architecture.md)), so a label scanned by a phone
that is not enrolled lands on sign-in rather than on the crate. The gate carries
the original URL through redemption, so the phone arrives at the crate it
scanned — but a *guest* holding a labeled box can no longer scan it at all.

That is the trade the operator accepted rather than a bug to fix, and it is
worth knowing before someone reports it as one. It is also the reason the
printed code beside the QR matters more than it used to: reading a code aloud to
someone whose phone will never be enrolled is the only remaining path from a box
to its contents.

### Configuration

| Key | Default | What it does |
|---|---|---|
| `Apps:PublicBaseUrl` | *(empty)* | Absolute base URL printed into QR labels, e.g. `https://home.example.com`. Set at deploy — `compose.prod.yml` passes `Apps__PublicBaseUrl` built from the `DOMAIN` variable. |

It exists because a QR label is not a link in a page: it is taped to a box for a
decade and must carry the install's canonical host, not whichever hostname or IP
the laptop that printed the sheet happened to be using. Unset, the app falls
back to the printing browser's origin and says so on screen — labels still
print on a fresh install, but the fallback is never a silent choice.
Set-but-unusable (no scheme, not http(s)) is treated as unset and logged.

Served to the shell by `GET /api/apps/config`, which lives beside the module
seam in `Modules/AppsController.cs` rather than inside Storage: the value
describes the install, not this app.

### Searching

Postgres full-text — `GET /api/storage/search?q=`, built in
`SearchQuery.cs` and `StorageController.Search`. Deliberately not OpenSearch: the
cluster is already there for logs, but a few hundred household items is three
orders of magnitude below where that earns its complexity, and `tsvector` won't
need replacing at this scale.

Three properties, each of them about someone thumb-typing while holding a box:

- **Every term is a prefix.** `dri` finds the drill; results narrow while typing
  rather than only once a word is finished.
- **Every term must match, and the document spans all three tables.** `drill
  garage` means the drill *in the garage* — the crate label, the crate code
  (bare, dashed, or either half) and the location name are part of what's
  searched, because that's how people recall where a thing is: by the thing and
  the place, not either alone.
- **The `english` configuration**, so `lights` finds `light`, `batter` finds the
  battery in a note, and `the` doesn't narrow anything.

Only letters and digits survive the trip from the search box to the query, so no
`tsquery` operator can reach the parser from typed text and a stray quote is a
word break rather than a syntax error thrown at someone mid-search.

The item's own text (`name` + `notes`) is a **generated `tsvector` column** with
a GIN index, so it's tokenised on write and no code path can forget to reindex
after an edit. Worth being straight about the index: the crate and location text
is concatenated onto that vector per row, because a generated column can only see
its own row, and a concatenated vector can't use the index — so this query scans.
At a few hundred items that's the right trade against the alternatives (splitting
the query per matched crate, or trigger-maintained denormalisation). The index is
what stops "make it index-backed" from being a schema change later.

**Offline**, the screen falls back to the substring filter over the loaded index
that Phase 3 shipped, and says so under the search box. It can't stem, but a
garage is exactly where the signal goes, and "no results" would be a lie there.
Search responses themselves are fetched `no-store`: one URL per settled keystroke
would fill the service worker's data cache with answers to questions nobody asks
twice.

### Offline

The family shell's service worker is cache-first for the app shell and
network-first for `/api` GETs, so a crate you've already opened still opens with
no signal — the common case in the far corner of a garage. Offline *writes* are
out of scope; they need conflict resolution nothing here justifies yet, so a save
with no network fails and says so.

## Deferred

Named so they're decisions rather than oversights. Full reasoning in
[`family-apps-architecture.md`](family-apps-architecture.md#deferred).

- **Photos of crate contents** — the most valuable v2 feature for an app of this
  kind, deferred on timing: blob storage should land on Longhorn after the k3s
  cutover rather than on the current host's disk and then get migrated.
- **Identity** — the wall authenticates the device, not the person, so there is
  still no notion of *who* filed a crate. See
  [`auth-architecture.md`](auth-architecture.md#deferred-on-purpose).
