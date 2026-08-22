# Gather

**Status:** not started. The kiosk text-entry risk that used to gate this plan is
**resolved** (Finding 4) — no blocking gates remain, and the design fork it
carried is closed.

Shared shopping lists for the family, reachable from the kitchen wall and from a
phone, backed by one source of truth in `Aerie.Api`. Groceries are the use case
that pays for it; the model is store-agnostic so the hardware store and the
warehouse club get the same treatment.

## The name

`Gather`. The verb is the product — you gather items into a list and then you go
gather them — and it sits with the aviary vocabulary the rest of Aerie is named
out of without being a bird pun. Short enough to fit a dashboard tile and a home
screen icon label.

Everything below is spelled `Gather` / `gather` consistently: C# namespace
`Aerie.Api.Modules.Gather`, Postgres schema `gather`, routes under
`/api/gather`, family shell module id `gather` at `/apps/family/gather/`.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Where the backend lives | A module — `src/Aerie.Api/Modules/Gather/` | [`Modules/README.md`](../../src/Aerie.Api/Modules/README.md) describes exactly this shape. Own schema, own migration history, one line in `AddAerieModules`, nothing in `Program.cs`, CI, or any manifest |
| List shape | Free-form named lists, each with an icon and a color | "Categories of stores" falls out of letting people name lists, without committing to a taxonomy we'd be stuck with. A `StoreKind` enum is a guess until speed-dials need one |
| Item fields | Name, checked, optional free-text quantity, optional note | "2% milk" + "x2" is the whole grocery-list vocabulary. Quantity is free text, not a number — "a dozen", "2 lbs", and "x2" are all real answers |
| Attribution ("who added it") | Deferred | The wall authenticates a *device*, not a person, and there is no `Person` table. Per `Modules/README.md`, a module inventing a user is what turns adding people into a refactor. Gather does not need it to be useful |
| Duplicate adds | Server-side upsert on a normalized name, unique per list | Someone at the kiosk re-adds "milk" because they can't see it in the list. Two `milk` rows is the wrong answer every time. A checked `milk` that gets re-added comes back *unchecked* — that is a re-add, not a no-op |
| Concurrency | Last-write-wins, but check/uncheck is its own endpoint | The common concurrent action (phone in the aisle checking things off, kiosk open in the kitchen) must not be able to clobber a rename typed a second earlier. Separating the toggle from the edit makes that structural rather than a race we hope doesn't happen |
| Freshness | Polling, no realtime | Matches every other read path in the product. The aisle case wants seconds, not milliseconds; SSE is a second transport to operate for a list of eight items |
| Kiosk surface | A full-screen overlay over the dashboard, launched from a tile | The dashboard has no router and its idle/reload lifecycle is tuned to a screen nobody types into. An overlay adds one suspension seam instead of making every existing lifecycle rule route-aware |
| Seeded lists | No migration seed; the empty state offers one-tap starters | A redeployable product shouldn't ship rows that assume a household ([`ethos.md`](../ethos.md)). "Grocery / Hardware / Pharmacy" as tappable suggestions gets the same first-run experience without baking them into the schema |
| Kiosk orientation | Portrait, assumed throughout | The wall tablets are mounted portrait. A tall narrow column suits a checklist, and it keeps the ~20% the keyboard occupies from crowding the list. Landscape is not designed for and not tested |
| Offline writes | Out of scope | The family shell's service worker already declares this: reads fall back to cache, writes fail as they would with no worker. An offline add needs conflict resolution nothing here justifies |

## Findings from the repo

Read before starting; each one changes an estimate.

1. **The module pattern is genuinely turnkey.** Context + design-time factory +
   `AddGatherModule` extension + one line in
   [`ModuleRegistration.cs`](../../src/Aerie.Api/Modules/ModuleRegistration.cs).
   Startup migrates every registered module context automatically. Storage
   Helper is the worked example to copy, file for file.

2. **The family shell is equally cheap.** One `GatherApp.tsx` default-exporting
   a component that renders its own `<Routes>`, plus one entry in
   [`registry.ts`](../../src/Aerie.Web/apps/family/src/modules/registry.ts). The
   home card, bottom tab, lazy chunk, and route all come from the registry
   entry. `react-router-dom` is already a dependency.

3. **The dashboard is the hard part, and not for the reason it looks like.**
   It's a single-screen app with no router
   ([`App.tsx`](../../src/Aerie.Web/apps/dashboard/src/App.tsx)), and
   [`useKioskLifecycle.ts`](../../src/Aerie.Web/apps/dashboard/src/hooks/useKioskLifecycle.ts)
   does two things that are actively hostile to a text field:
   - resets the page to base state 30s after the last touch, and
   - reloads the page when a new build is deployed (deferred to idle, but a
     30s-idle definition means "still typing" can read as idle).

   Both need a suspension seam while the overlay is open. The reset is also
   *keyed* remounting, so a half-typed item would vanish without a trace.

4. **Kiosk text entry works — observed on the wall tablet, 2026-08-21.**
   Redeeming an invite code at `/auth` inside the kiosk's GeckoView raised an
   ordinary tablet soft keyboard: bottom ~20% of the screen, split left/right
   for thumb reach. The tablets are **portrait** (see Decisions). That one
   observation kills the three risks this plan was originally built around:
   - IMEs *are* exempt from the lock-task allowlist in practice, under
     [`MainActivity.kt:129`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/MainActivity.kt#L129)'s
     `LOCK_TASK_FEATURE_NONE` + `startLockTask()`.
   - The tablet has a usable IME, so the non-GMS worry doesn't apply to this
     hardware.
   - GeckoView's IME path delivers characters with only a no-op
     `ContentDelegate` registered, and the auth form's `onSubmit` fired, so the
     enter key reaches the page.

   **What it does not prove, and why it matters here.** The auth page is a
   vertically centered card
   ([`App.css:9`](../../src/Aerie.Web/apps/auth/src/App.css#L9) —
   `align-items: center; justify-content: center`), so its input sits at roughly
   half height — comfortably clear of a keyboard occupying the bottom fifth. The
   overlap case was never exercised. The activity still declares no
   `android:windowSoftInputMode` and still runs immersive, so whether the
   viewport resizes for the IME is genuinely unknown.

   This is now a **layout constraint rather than an open question**: keep every
   input in the Gather overlay in the top half and the answer stops mattering.
   Portrait makes that cheap rather than cramped — a tall narrow column is the
   natural shape for a shopping list anyway, so the add field at the top with
   the items running below it is what you'd draw regardless. Note that a
   bottom-pinned add field — the right design on a *phone*, where the browser
   lifts it above the keyboard — is precisely the wrong one here. The two
   clients diverge on this deliberately.

   One more inherited detail worth copying: the auth input sets
   `autoCorrect="off"` (`SignInPage.tsx:90-91`). Free-text item entry has not
   been tried, and autocorrect mangling a brand name is worse than a lowercase
   one — so Gather's field should carry `autoCorrect="off"` and
   `spellCheck={false}` too, with `autoCapitalize="words"`.

5. **Tests go in `src/Aerie.Api.Tests/Gather/`.** Note that Storage's tests live
   at `Tests/Storage/`, *not* `Tests/Modules/Storage/` — `Tests/Modules/` holds
   only the shared `AppsConfigTests`. Follow Storage.

6. **The dashboard has `vitest`; the family app does not.** Dashboard-side
   logic (overlay lifecycle, optimistic reconciliation) can have unit tests
   without adding a dependency.

---

## Phase 1 — The `gather` module

Backend, complete and independently verifiable through Swagger before any UI
exists.

- [ ] `Modules/Gather/Entities.cs` — `GatherList` (Id, Name, Icon, Color,
      CreatedAt, UpdatedAt) and `GatherItem` (Id, ListId, Name, NameNormalized,
      Quantity, Note, IsChecked, CheckedAt, CreatedAt, UpdatedAt).
      `Quantity` free text, max 32. `Note` max 200.
- [ ] Unique index on `(ListId, NameNormalized)` — this is what makes the
      re-add upsert atomic rather than a check-then-insert race between the
      kiosk and a phone.
- [ ] `Modules/Gather/GatherContext.cs` — `const string Schema = "gather"`,
      `IModuleContext`, `HasDefaultSchema(Schema)`.
- [ ] `Modules/Gather/GatherDesignTimeFactory.cs` extending
      `ModuleDesignTimeFactory<GatherContext>`.
- [ ] `Modules/Gather/GatherModule.cs` — `AddGatherModule` registering the
      context and `IGatherService`.
- [ ] One line in `AddAerieModules`: `services.AddGatherModule(configuration);`
- [ ] `Modules/Gather/Dtos.cs` — `ListSummaryDto` (id, name, icon, color,
      openCount, checkedCount, updatedAt), `ListDto` (summary + items),
      `ItemDto`, and the write requests.
- [ ] `Modules/Gather/GatherService.cs` — the two operations with behavior worth
      isolating: `AddItem` (normalize, upsert, un-check on re-add) and
      `ClearChecked`. Plain CRUD stays on the context in the controller, as
      Storage does.
- [ ] `Modules/Gather/GatherController.cs` at `[Route("api/gather")]`:

      GET    lists                          -> ListSummaryDto[]
      POST   lists                          -> create
      GET    lists/{id}                     -> ListDto (with items)
      PUT    lists/{id}                     -> rename / icon / color
      DELETE lists/{id}                     -> cascades items
      POST   lists/{id}/items               -> add (upsert semantics)
      PUT    lists/{id}/items/{itemId}      -> edit name / quantity / note
      POST   lists/{id}/items/{itemId}/check
      POST   lists/{id}/items/{itemId}/uncheck
      DELETE lists/{id}/items/{itemId}
      POST   lists/{id}/clear-checked       -> { deleted: n }

- [ ] Item ordering is server-side and stable: unchecked first by `CreatedAt`,
      then checked by `CheckedAt` descending. The client should not be deciding
      this independently in two apps.
- [ ] Scaffold the migration into the module folder:
      `dotnet ef migrations add Init --context GatherContext --project ./src/Aerie.Api/Aerie.Api.csproj -o Modules/Gather/Migrations`
- [ ] Verify the migration landed in `gather.__EFMigrationsHistory` and that
      **nothing** appeared in `Migrations/` or `public.__EFMigrationsHistory`.
- [ ] Tests in `src/Aerie.Api.Tests/Gather/`: the upsert (new / existing-open /
      existing-checked), normalization (case, whitespace, trailing plural left
      alone), clear-checked count, cascade delete, and that check/uncheck does
      not touch name/quantity/note.
- [ ] `make build` and the test suite green. Exercise the surface in Swagger.

## Phase 2 — Gather in the family PWA

Independent of Phase 3 — can proceed in parallel.

- [ ] `src/Aerie.Web/apps/family/src/modules/gather/`: `GatherApp.tsx`,
      `routes.ts`, `api.ts`, `types.ts`, `components.tsx`, `gather.css`.
- [ ] Registry entry — id `gather`, title `Gather`, icon, and a tagline in the
      voice of Storage's ("Crates, labels, and where the drill went").
- [ ] Reuse `modules/storage/useResource.ts` as the fetch/refresh pattern rather
      than inventing a second one. If it needs generalizing, move it up to
      `src/lib/` in the same change.
- [ ] **Lists screen** — cards per list showing open count, tap to open, plus
      the empty state offering one-tap starters (Grocery, Hardware, Pharmacy,
      Warehouse).
- [ ] **List screen** — checkbox rows, checked items sunk to the bottom and
      struck through, an add field pinned above the keyboard, and a
      **Clear checked** action showing the count it will remove.
- [ ] Optimistic check/uncheck, reconciling against the next poll. This is the
      one interaction that happens while walking, and it must not wait on a
      round trip.
- [ ] **List settings** — rename, icon, color, delete (with a confirm that names
      the list and its item count).
- [ ] Poll the open list on an interval so the kitchen and the aisle converge;
      pause polling when the document is hidden.
- [ ] Style exclusively from `src/theme.css` tokens — no module palette.
- [ ] `npm run build` and `npm run lint` clean. **UI verification is the user's**
      (per standing preference); the build and lint are the bar for handing it
      over.

## Phase 3 — Kiosk overlay

No longer gated — see Finding 4. Independent of Phase 2.

- [ ] **Portrait-first, and every input lives in the top half of the overlay.**
      The keyboard owns the bottom ~20% and whether the viewport resizes for it
      is untested; this one rule makes that not matter. The add field goes at
      the *top*, with the items running down the column beneath it — never
      pinned to the bottom.
- [ ] The add field carries `autoCorrect="off"`, `spellCheck={false}`,
      `autoCapitalize="words"`, and an `onSubmit` that adds the item and keeps
      focus, so a run of items goes in without re-tapping the field.
- [ ] First time on the tablet, confirm the three things the auth page couldn't:
      that the list still scrolls with the keyboard up, that dismissing the
      keyboard leaves immersive mode intact (`hideSystemBars()` re-fires on
      `onWindowFocusChanged`), and that the split keyboard doesn't sit on top of
      anything that needs tapping. All three are cheap to check and none of them
      block starting.
- [ ] `src/Aerie.Web/apps/dashboard/src/api/gatherClient.ts`, following
      `routinesClient.ts` — including its `handledUnauthorized` handling, which
      is not optional now that the wall is up.
- [ ] Extend `useKioskLifecycle` with a suspension seam (a `hold` registration,
      or an argument) that pauses **both** the idle reset and the deploy reload.
      Deliberately one seam covering both — a reload that fires mid-entry is the
      same bug as a reset that does.
- [ ] Unit-test the suspension in `vitest`: held → no reset, released → timer
      re-arms, held-then-deploy-drift → reload deferred until release.
- [ ] `components/GatherTile.tsx` — compact, readable across the kitchen: list
      names with open counts. Renders nothing when there are no lists.
- [ ] `components/GatherOverlay.tsx` — full-screen over the dashboard: pick a
      list, check items, add an item, clear checked. One portrait column, touch
      targets sized for a wall tablet read at arm's length, not a phone held at
      reading distance.
- [ ] The overlay carries its **own** longer idle timeout (~90s) that closes it
      and hands control back to the normal lifecycle, so the wall always returns
      to the dashboard on its own. A tablet left on the grocery list is a
      regression in what the wall is for.
- [ ] Wire Gather into the mock and test data sources so `?source=mock` and
      `?source=test` still render the whole screen.
- [ ] `npm run build`, `npm run lint`, `npm run test` clean.

## Phase 4 — Docs and dissipation

- [ ] `docs/gather.md`, modeled on `docs/storage-helper.md`.
- [ ] Add the Gather endpoints to
      [`dashboard-api-manifest.md`](../dashboard-api-manifest.md).
- [ ] Move Finding 4 into
      [`kiosk-architecture.md`](../kiosk-architecture.md). That these tablets
      take text input at all, and where the keyboard sits, is a durable fact
      about the hardware that outlives this plan — and the next person to put a
      field on the wall will look there, not here.
- [ ] Delete this plan. Per [`plans/README.md`](README.md), a finished plan is
      not an archive.

## Deferred, on purpose

- **Voice entry** and **speed dials / shortcuts** on the kiosk. Both are named
  as the eventual goal in the original brief; both need the dumb version in
  daily use first, because which items deserve a speed dial is an observation,
  not a guess.
- **Attribution** — who added an item. Blocked on the person-vs-device seam, not
  on effort.
- **Offline writes** in the PWA.
- **Realtime sync.** Polling until someone reports the lag.
- **Store-category taxonomy**, price tracking, recipes-to-list, and pantry stock.

## Risks

| Risk | Handling |
|---|---|
| ~~Kiosk keyboard doesn't work~~ | **Retired** — observed working on the tablet, Finding 4 |
| Keyboard covers a bottom-anchored input | Design around it: inputs stay in the upper half. Never becomes a question |
| Idle reset eats an in-progress entry | The suspension seam is unit-tested, not eyeballed |
| Two devices editing one list | Check/uncheck is a separate endpoint from edit, so the common collision can't clobber text |
| Access from outside the house | TailScale, per the brief. No new exposure |
