# Gather

## Summary

Shared shopping lists for the household, reachable from the kitchen wall and
from a phone, backed by one source of truth in `Aerie.Api`. Groceries are the
use case that pays for it; the model is store-agnostic, so the hardware store
and the warehouse club get the same treatment.

It is the second app on the family-apps platform described in
[`family-apps-architecture.md`](family-apps-architecture.md), and the one that
tested the platform's central claim — that app #2 costs a folder and an
afternoon. It did: one folder under `src/Aerie.Api/Modules/`, one folder under
the shell's `src/modules/`, one line in each registry, no container, database,
ingress, backup entry, uptime monitor, or CI job.

Three properties shape everything below:

- **One process, one database.** The backend is a module in `Aerie.Api` with its
  own `gather` Postgres schema and its own migration history.
- **Two clients, one contract.** Gather is the first app that renders on the
  family PWA *and* on the kiosk. Ordering, duplicate handling and merge
  semantics are all decided server-side, because two clients each deciding for
  themselves is two chances to disagree about what the list says.
- **Behind the house wall, and behind the tailnet.** Every route here is gated
  by a device grant ([`auth-architecture.md`](auth-architecture.md)). The wall
  authenticates a *device*, not a person, which is why nothing here records who
  added an item — see [Deferred](#deferred).

The name is the product: you gather items into a list and then you go gather
them. It is spelled `Gather` / `gather` everywhere — C# namespace
`Aerie.Api.Modules.Gather`, Postgres schema `gather`, routes under
`/api/gather`, shell module id `gather` at `/apps/family/gather/`.

## Data model

Two tables in the `gather` schema —
[`Entities.cs`](../src/Aerie.Api/Modules/Gather/Entities.cs).

```
gather.Lists  id, name, icon, color, created_at, updated_at
gather.Items  id, list_id → Lists (CASCADE), name, name_normalized,
              quantity, note, is_checked, checked_at, created_at, updated_at
              unique (list_id, name_normalized)
```

**Lists are free-form and named, not a store taxonomy.** Letting people name
lists gets "categories of store" for free without committing to a vocabulary
we'd be stuck with; a `StoreKind` enum is a guess until speed dials need one.
A list carries an icon and a colour, both of which are
[names, not values](#colours-and-icons).

**An item is a name, a checkbox, an optional free-text quantity and an optional
note.** "2% milk" plus "x2" is the whole grocery-list vocabulary. Quantity is
text rather than a number because "a dozen", "2 lbs" and "x2" are all real
answers to it.

**Deleting a list takes its items with it.** A list is an item's only address —
deleting "Grocery" is deleting the groceries, not orphaning them somewhere
nothing can reach. This points the opposite way from Storage's locations, and
for the opposite reason: an unplaced crate is still a box you own.

**A list's `updatedAt` moves on item activity too**, not just a rename. It is
the "last used" a lists screen wants, and one that froze at creation while the
list churned daily would be a lie.

**Lengths are validated, not truncated** — item name 120, quantity 32, note 200,
list name 60, icon 32, colour 32. Over-long text comes back `400` naming the
field rather than `500` out of the column.

### Normalization, and the index that enforces it

Every item carries a `NameNormalized` alongside its display name: the same
string with runs of whitespace collapsed and case folded away. It is never
displayed and never leaves the server. `(ListId, NameNormalized)` is unique.

- **Case and spacing only. Deliberately not a stemmer.** "Apple" and "apples"
  stay two rows, because the cost of being wrong runs one way: a stray duplicate
  is a line someone crosses off, while silently merging "battery" into
  "batteries" loses a note or a quantity and nobody sees it happen.
- **Setting `Name` recomputes `NameNormalized`** in the property setter, so no
  write path can drift from the index by forgetting to normalize.
- **The index is the enforcement; the lookup is an optimisation.** The kiosk and
  a phone can add "milk" in the same instant, so `AddItemAsync` catches the
  unique violation and retries into the merge path rather than surfacing a
  `500`. `UpdateItem` does the same on rename. A check-then-insert would have
  been a race nobody notices losing.

## The contract

Four behaviours both clients are built on. They are the answers that were
expensive to decide and are cheap to break.

- **A re-add merges; an edit overwrites.** `POST items` applies a quantity or
  note only when the request carries one, so re-adding a bare "milk" cannot
  erase the "2 gal" someone else put on it. `PUT items/{id}` writes exactly what
  it is given, nulls included — that is how a wrong quantity comes off at all.
  A re-add also keeps the spelling already on the list: a second person typing
  MILK is not a rename, and the row shouldn't flicker between capitalisations.
- **A re-add of a checked item brings it back un-checked.** Someone at the wall
  who can't see "milk" on the list types it again; they want milk, and the row
  saying it was already bought is the stale one. That is a re-add, not a no-op.
- **Renaming onto an existing item is a `409`, not a merge.** Silently folding
  two rows into one loses whatever was on the other. The client says so and
  leaves the text in the field.
- **Checking is its own endpoint, never a field on the edit.** The two writes
  that collide in practice are a phone checking things off in an aisle and the
  kitchen wall renaming one a second earlier. Separating them makes the common
  collision *structurally* unable to clobber text, rather than a race we hope
  doesn't happen. Concurrency is otherwise last-write-wins.

**Display order is server-side and total** —
[`GatherService.InDisplayOrder`](../src/Aerie.Api/Modules/Gather/GatherService.cs):
unchecked first oldest-first (the order they were thought of, which is the order
the aisle gets walked), then checked most-recently-checked first, so the last
thing crossed off is the easiest to un-cross, with `Id` breaking any remaining
tie so a poll never reshuffles rows under a thumb. One expression, used by every
read path, for the same reason the rest of this section exists.

## API

All under `/api/gather`, in
[`GatherController.cs`](../src/Aerie.Api/Modules/Gather/GatherController.cs).
Endpoint-level notes are in
[`dashboard-api-manifest.md`](dashboard-api-manifest.md#gather); what follows is
the shape.

| Endpoint | Notes |
|---|---|
| `GET /lists` | Summaries: name, icon, colour, **both** counts. "4 to get" and "11 already in the cart" are different facts, and a card shows the first without a second request for the second. |
| `GET /lists/{id}` | The list *and its items*, in display order, in one response — what opening a list lands on and what every poll re-fetches. |
| `POST /lists/{id}/items` | The upsert. Returns `200`, not `201`: on a re-add nothing was created, and the client's next move is the same either way. |
| `POST /lists/{id}/items/{itemId}/check` · `.../uncheck` | Separate from the edit, per [the contract](#the-contract). |
| `POST /lists/{id}/clear-checked` | Sweeps everything already in the cart off the list and reports the count, so the client can say what it just did rather than guessing from a re-fetch. |

Plain CRUD for lists and items runs against `GatherContext` directly in the
controller; only the add upsert and the clear-checked sweep go through
`IGatherService`, because only they have behaviour worth isolating. That split
follows Storage.

## Colours and icons

**A list's colour is a palette name — `sky`, `moss`, `clay`, `plum`, `sun`,
`slate` — never a hex.** The column is free text and would hold a hex fine, but
a hex chosen in daylight is wrong in dark mode forever, and the wall would have
had to re-derive a readable version of whatever a phone picked. A name defers
the colour to CSS, where each client already knows how to answer:

- The **phone** resolves the name to a light/dark pair in `gather.css`.
- The **wall** has no two modes to resolve into. The six landed in the
  dashboard's [`theme/tokens.ts`](../src/Aerie.Web/apps/dashboard/src/theme/tokens.ts)
  as `--tint-*` tokens and blend through the day like `--ink` and `--card` do,
  per the rule that no colour on that page is ever stated absolutely
  ([`kiosk-architecture.md`](kiosk-architecture.md#what-the-wall-shows)). The
  phone's light values became the day palette and its dark values the amber and
  night ones, so a `moss` list is recognisably the same list on both clients at
  both ends of the day.

An unrecognised value falls back to the first name rather than rendering
untinted, so a hand-edited row is never the odd one out by accident. The six
names are written down twice — [`palette.ts`](../src/Aerie.Web/apps/family/src/modules/gather/palette.ts)
and [`listColor.ts`](../src/Aerie.Web/apps/dashboard/src/lib/listColor.ts) —
because the two apps build independently with no shared package between them.
Six strings and a fallback is the right size for that duplication; each file
names the other.

**A list's icon is an emoji**, chosen from a fixed set, defaulting to `🧺`.
Worth stating because the kiosk renders routine icons through Font Awesome, and
reaching for `iconFor()` here would give every list on the wall the same
fallback bolt. Both clients render the character as text, in a square tinted
with the list's colour — an emoji keeps its own colours whatever `color` says,
so the tint has to be the background.

## The phone

[`src/Aerie.Web/apps/family/src/modules/gather/`](../src/Aerie.Web/apps/family/src/modules/gather/) —
a module inside the family shell PWA at `/apps/family/gather/`. Two screens:
the lists, and a list. No subnav, unlike Storage — there is one hierarchy here,
and a tab strip over a two-deep tree is chrome standing in for a back link.

- **The lists screen** shows open counts, and what's already in the cart when
  there is any. Its empty state offers one-tap starters (Grocery, Hardware,
  Pharmacy, Warehouse) rather than the schema shipping seeded rows: a
  redeployable product shouldn't create rows that assume a household
  ([`ethos.md`](ethos.md)), and the tappable version gets the same first-run
  experience.
- **The list screen** is checkbox rows, a **Clear checked** action showing the
  count it will remove, and an add field **pinned to the bottom**, above the tab
  bar. That last one is the deliberate opposite of the wall — see
  [Text entry](#text-entry) — because a phone browser lifts a focused input
  above the keyboard.
- **Optimistic check/uncheck**, reconciled against the next poll. A tap flips
  the row instantly but does *not* re-sort; a debounced refresh 500ms after the
  last tap brings back the server's ordering, so no comparator is written twice.
- **List settings** — rename, icon, colour, delete, with a confirm that names
  the list and its item count. One `ListForm` serves creation and settings both.

Gather is also where four pieces of the module's plumbing moved **up into the
shell**, because module two wanted them verbatim: `lib/http.ts` (`HttpError`,
`createClient`), `lib/useResource.ts`, the new `lib/usePolling.ts`, and
`components/Notices.tsx`. That is the bar the shell's own
[`family-apps-architecture.md`](family-apps-architecture.md#the-seams-that-make-an-app-cheap)
sets: shared when a second app wants it unchanged, not before.

## The wall

The kiosk half is a tile on the dashboard that opens a full-screen overlay:
[`GatherTile.tsx`](../src/Aerie.Web/apps/dashboard/src/components/GatherTile.tsx),
[`GatherOverlay.tsx`](../src/Aerie.Web/apps/dashboard/src/components/GatherOverlay.tsx),
and [`gatherClient.ts`](../src/Aerie.Web/apps/dashboard/src/api/gatherClient.ts).
Why an overlay rather than a route, what it does to the idle lifecycle, and the
soft-keyboard facts behind its layout are in
[`kiosk-architecture.md`](kiosk-architecture.md#gather-on-the-wall).

The tile renders nothing when there are no lists, so a fresh deployment looks
like the feature simply isn't there yet — which it isn't.

## Freshness

**Polling, no realtime.** It matches every other read path in the product; the
aisle case wants seconds, not milliseconds; and SSE is a second transport to
operate for a list of eight items. Three cadences, each matched to what the
screen is for:

| Where | Interval |
|---|---|
| The open list on a phone | 10s, paused while the document is hidden and fired immediately on becoming visible again |
| The dashboard tile | 60s — a shopping list on a wall is not a live feed |
| The open list in the overlay | 10s, with values applied but **rows not re-sorted** until nobody has tapped for 4s |

That last rule is the one to keep: the server sinks a checked item to the bottom,
and a list that re-sorts under a finger is how you check off the wrong thing.

**An optimistic tick expires after 30 seconds** on both clients, and has to. The
family shell's service worker serves the last good body when a fetch fails, so a
read can arrive stale; and another device un-checking the same item is a
disagreement the server has to win. Without a deadline the wall would hold a
tick that is no longer true until somebody closed the overlay.

**Adds refetch rather than splice.** The server decides where a new item sits
and whether it merged onto one already there, and it is ten milliseconds away.

**Offline writes are out of scope**, as they are for the rest of the shell: reads
fall back to cache, writes fail and say so. An offline add needs conflict
resolution nothing here justifies.

## Text entry

Gather is the first thing in Aerie that put a text field on the kitchen wall.
Two facts came out of that, and both live where the next person will look for
them rather than here:

- **What the tablets do with a soft keyboard**, what was confirmed on the
  hardware and what is still assumed —
  [`kiosk-architecture.md`](kiosk-architecture.md#text-entry-on-the-wall).
- **The design rule that falls out of it**: every input and every button in the
  overlay lives in the top half of the screen, so whether the viewport resizes
  for the IME never becomes a question. Note that a bottom-pinned add field —
  the right design on a phone, which is what the family app does — is precisely
  the wrong one there. The two clients diverge on this deliberately.

## Deferred

Named so they're decisions rather than oversights.

- **Voice entry** and **speed dials** on the kiosk. Both were named as the
  eventual goal in the original brief; both need the dumb version in daily use
  first, because which items deserve a speed dial is an observation, not a guess.
- **Attribution** — who added an item. Blocked on the person-vs-device seam, not
  on effort: the wall authenticates a device and there is no `Person` table, and
  a module inventing its own user is what turns adding people into a refactor
  ([`Modules/README.md`](../src/Aerie.Api/Modules/README.md)).
- **Offline writes** in the PWA, and **realtime sync** — polling until someone
  reports the lag.
- **A store-category taxonomy**, price tracking, recipes-to-list, and pantry
  stock.
