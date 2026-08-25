# Kiosk Climate

**Status:** Phases 0–5 complete (naming, model, schema, domain rules, the read path, the write path, and the admin UI). Phases 6–8 not started.

New kiosk dashboard UI for house climate controls, and the two new domain
concepts that make it possible: **Panels** and **Controls**.

## The ask

Routines work well, and the next tier up is missing. Between the routine tiles
and Gather, the wall should show a **Panel** — same tile geometry, same
name/icon/color customization — that opens a sub-UI holding several calls to
action. Some of those are existing Routines. Some are **Controls**: richer,
device-specific surfaces that do more than one tap can express.

The user experience this is built for:

- Scroll past the routines, see a "Climate" tile, tap it.
- A full-screen overlay appears with:
  - **Air conditioner** — on/off, ± 1°F, and the current setpoint big enough to
    read across a room.
  - **Fan 1** — on/off.
  - **Fan 2** — on/off.

Later, on the same machinery: an "Environment" panel with simplified smart-light
controls (name, color picker, brightness, on/off), a living-room radiator that is
the same thermostat control with heat instead of cool, and a camera panel whose
control is a live feed with pan/tilt/zoom and a light toggle.

**MVP scope is the Climate panel and nothing else.** Light and camera controls are
a seam in this design, not work in it.

## Decisions

| Question | Decision | Why |
|---|---|---|
| Name for `Submenu` | **Panel** | "The Climate panel" reads the way the household already talks. Unused as a domain noun in the repo — the only hits are prose and the unrelated modeler app. |
| Name for `Control` | **Control**, qualified as `EfPanelControl` in code | The user-facing word stays plain. Bare `Control` collides with [`ClimateControl`](../../src/Aerie.Api/Services/ClimateControl/), `EfControlOverride` and `EfControlDecision`, so the entity, namespace and DTOs all carry the `Panel` qualifier. |
| Is a Control composed of Routines? | **No — typed and device-bound** | A Routine is a fixed-value, fire-and-forget bundle with no read path. A Control has to *report* the current setpoint and mode and write a *parametric* value. Composing one from Routines would need one routine per degree and still could not read anything back. Controls bind channels directly and share Routines' write path (`IClimateCommandService`), not their storage. |
| Set-temperature interaction | **± with hold-to-repeat**, coalesced into one write | Two big touch targets and a large number is the right density for a portrait wall tablet. Taps settle for ~700ms before dispatching, so holding "+" from 68 to 78 lands one ledgered `SetTemperature`, not ten. |
| What happens to existing climate routine tiles | **Nothing — the panel is additive** | The fans in the Climate panel are new `Switch` controls bound straight to their `PowerState` channels. Two routes to the same fan is fine on a wall; a migration that moves tiles around is not worth the disruption. |
| Where panel state comes from | **A dedicated endpoint, polled while open** | `GET /api/dashboard` is a 60s poll and carries every panel whether or not anyone opened one. Live control state is too stale at 60s and too expensive to gather unconditionally, so the snapshot carries only the tile, and the overlay fetches its own state. |
| On/off for a thermostat | **A `Power` binding when the device has one, otherwise `OnMode`** | The AC and the radiator differ only in which HVAC mode counts as "on" (`cool` vs `heat`). Storing that mode per control is what lets both present the single "On/Off" the ask calls for. |

## What the repo already gives us

Read before designing; recorded so the phases below don't re-derive it.

- **Every write to Home Assistant goes through one chokepoint.**
  [`ClimateCommandService.DispatchAsync`](../../src/Aerie.Api/Services/ClimateControl/ClimateCommandService.cs)
  ledgers intent, validates against the channel, checks overrides, dispatches,
  records the outcome. Panels inherit all of that for free by issuing
  `CommandRequest`s — and must not call `IHomeAssistantCommandService` directly.
- **`CommandExpectation.Validate`** already pins which `DeviceChannelMetric` each
  `CommandKind` requires and that the channel is `ReadWrite`. Panel binding
  validation is the same question one level up, and should be a sibling pure
  class, not a re-implementation.
- **`CommandSource.Human` exists.** A person at the wall is `Human`, not
  `Routine`; no enum change needed. (Both are exempt from override suppression,
  which is correct — a hand on the tablet *is* the override.)
- **`ChannelLatestValues.GetLatestAsync`** is the batched "latest value per
  channel" read, deliberately per-channel-index rather than set-based. Panel
  state assembly uses it, scoped to the opened panel's channels only.
- **`RoutineService.ComputeIsActive`** is the toggle-active rule. A routine item
  inside a panel needs the identical answer, so that logic gets extracted rather
  than copied.
- **The kiosk overlay idiom** is [`GatherOverlay`](../../src/Aerie.Web/apps/dashboard/src/components/GatherOverlay.tsx)
  and [`CameraFeedModal`](../../src/Aerie.Web/apps/dashboard/src/components/CameraFeedModal.tsx):
  full-screen, mounted **inside `.hf-page`** (the circadian palette is inline
  custom properties on that element, and an overlay outside it resolves none of
  them), with its own longer idle timeout and the dashboard lifecycle held while
  it is open.
- **Optimistic state has a house pattern.** `RoutinesSection` holds an optimistic
  map cleared once the server snapshot agrees; `GatherOverlay` adds a TTL so a
  change made elsewhere eventually wins. Panel controls need both halves.
- **Tile geometry is shared, not duplicated.** `CamerasSection` reuses
  `.hf-routine-btn`'s selectors rather than restating the numbers. Panel tiles do
  the same.
- **`?source=mock|test`** switches the whole screen through `dataSource.ts`.
  Panels need mock and test implementations or that guarantee breaks.

## The model

```
EfPanel                         Table "Panels"
  Id, Name, Description?, Icon?, Color?, SortOrder, Included
  Items: List<EfPanelItem>

EfPanelItem                     Table "PanelItems"
  Id, PanelId, SortOrder
  Kind: PanelItemKind           Routine | Control
  RoutineId?                    set iff Kind == Routine
  ControlKind?                  set iff Kind == Control: Switch | Thermostat
  Label?, Icon?, Color?         control display; a routine item uses the routine's own
  OnMode?                       thermostat: the HVAC mode that means "on" ("cool", "heat")
  MinF?, MaxF?, StepF?          thermostat bounds; null falls back to 60 / 85 / 1
  Bindings: List<EfPanelControlBinding>

EfPanelControlBinding           Table "PanelControlBindings"
  Id, ItemId, Role: ControlRole, ChannelId
```

**One item table with a discriminator, rather than two parallel tables.** What the
kiosk renders is *one ordered list of things in a modal*, and `SortOrder` is the
property that has to be coherent across both kinds — two tables would leave the
admin form reconciling two collections' orderings against each other for no gain.
The nullable columns are the honest cost, and each one is documented on the entity.

**Roles per control kind:**

| Kind | Role | Required | Channel metric | Direction |
|---|---|---|---|---|
| `Switch` | `Power` | yes | `PowerState` | ReadWrite |
| `Thermostat` | `Setpoint` | yes | `SetpointTemperature` | ReadWrite |
| `Thermostat` | `Power` | no | `PowerState` | ReadWrite |
| `Thermostat` | `Mode` | no | `HvacMode` | ReadWrite |
| `Thermostat` | `Ambient` | no | `Temperature` | Read or ReadWrite |

A `Thermostat` needs `Power` **or** (`Mode` **and** `OnMode`) to be switchable;
without either it is setpoint-only, which is legal.

All three enums (`PanelItemKind`, `ControlKind`, `ControlRole`) follow the repo's
append-only rule — the column stores the underlying int, so new values go on the
end. That rule is exactly what makes `Light` and `Camera` a later migration
rather than a later redesign.

## The API

| Endpoint | Who | What |
|---|---|---|
| `GET /api/panels` · `GET /api/panels/{id}` | admin | Panels with their items and bindings. |
| `POST` · `PUT /api/panels/{id}` · `DELETE /api/panels/{id}` | admin | CRUD. Items are embedded and replaced wholesale, exactly as `RoutineWriteRequest` does — the item list *is* the panel. |
| `GET /api/dashboard` | kiosk | Gains `panels: PanelSummary[]` — id, name, icon, color. The tile, nothing more. |
| `GET /api/panels/{id}/state` | kiosk | Live per-item state. Polled ~5s while the overlay is open. |
| `POST /api/panels/{id}/items/{itemId}/power` | kiosk | `{ on: bool }`. Switch → `SetPower`. Thermostat → `SetPower` when `Power` is bound, else `SetHvacMode` to `OnMode` / `"off"`. |
| `POST /api/panels/{id}/items/{itemId}/setpoint` | kiosk | `{ valueF: decimal }`, clamped server-side to `MinF`/`MaxF`. |

Routine items reuse `POST /api/routines/{id}/trigger` and `/turn-off` unchanged.

**Writes are item-scoped, not channel-scoped, on purpose.** The panel is the
boundary: the kiosk can act on what an admin deliberately put on a panel, and
cannot address an arbitrary channel by id. That is a smaller surface than
`DevicesController`'s channel-write endpoints, which is what a wall tablet in a
hallway should have.

Every dispatch carries `CommandSource.Human` and a reason naming both halves —
`"Panel 'Climate' — Air conditioner"` — so the ledger says which surface a person
touched, not just that a person touched something.

---

## [x] Phase 1 — Schema

**Status:** complete

- [x] Add `EfPanel`, `EfPanelItem`, `EfPanelControlBinding` and the
      `PanelItemKind` / `ControlKind` / `ControlRole` enums to a new
      [`src/Aerie.Api/Ef/Panels.cs`](../../src/Aerie.Api/Ef/Panels.cs), with the
      append-only comment each existing enum carries.
- [x] Document every nullable column on `EfPanelItem` with what makes it null —
      the discriminator is only honest if the reader can tell which columns
      belong to which kind.
- [x] Register `Panels`, `PanelItems`, `PanelControlBindings` DbSets on
      [`AerieContext`](../../src/Aerie.Api/Ef/AerieContext.cs).
- [x] Configure relationships in `OnModelCreating`: item → panel cascade,
      binding → item cascade, binding → channel cascade (a deleted channel takes
      the binding that pointed at it), item → routine **cascade** (a deleted
      routine must not leave an item referencing nothing).
- [x] `make ef-migration migration=AddPanels` → `20260825223359_AddPanels`.
- [x] `make db` then `make ef-database-update`; all three tables present, and
      `\d` confirms every FK carries `ON DELETE CASCADE` — including
      `PanelItems.RoutineId`, where the nullable column would otherwise have
      defaulted to `SET NULL`.

**What the apply showed.** `EfPanelItem.ControlKind` is a property whose name
matches its own enum type; C#'s color-color rule resolves it and the build is
clean, but any later code that needs the *type* inside that class must qualify
it. `make test-api` stayed at 736 passing — Phase 1 adds tables and touches no
behavior.

## [x] Phase 2 — Domain rules

**Status:** complete

- [x] Add `PanelBindingRules` under
      [`src/Aerie.Api/Services/Panels/`](../../src/Aerie.Api/Services/Panels/) — a
      pure class, no DB or clock, sibling in spirit to `CommandExpectation`.
- [x] `RequiredMetric(ControlRole)` and `RolesFor(ControlKind)` returning
      required/optional roles per the table above, plus `IsWritten(ControlRole)` —
      the direction half of the table needed a name of its own, because `Ambient`
      is the one role that accepts a `Read` channel.
- [x] `Validate(EfPanelItem, IReadOnlyDictionary<Guid, EfDeviceChannel>)`
      returning null or the reason: missing required role, wrong metric,
      read-only channel on a writing role, unknown role for the kind,
      `Thermostat` with `Mode` bound but no `OnMode`, `MinF >= MaxF`,
      non-positive `StepF`.
- [x] Add `PanelDefaults` with `MinF = 60`, `MaxF = 85`, `StepF = 1`, and a
      comment on why they are constants rather than settings for now.
- [x] Extract `RoutineService.ComputeIsActive` into a shared
      `RoutineToggleState.IsActive(routine, latest)`; repoint `RoutineService` at
      it and confirm `RoutineServiceTests` still passes untouched.
- [x] [`src/Aerie.Api.Tests/Panels/PanelBindingRulesTests.cs`](../../src/Aerie.Api.Tests/Panels/PanelBindingRulesTests.cs):
      one test per rejection reason, plus a valid `Switch`, a valid `Thermostat`
      with `Power`, and a valid `Thermostat` with `Mode` + `OnMode`.

**Three rejection reasons the plan didn't list, and why they're there.** A
binding whose channel id isn't in the caller's dictionary returns "does not
exist" rather than throwing — Phase 4 loads channels from ids in a request body,
so the lookup missing an id is an ordinary bad request, not a bug. A role bound
twice is rejected because the read path assumes one channel per role and would
otherwise silently pick whichever came first. And `Validate` answers for routine
items too (routine set, no bindings), because Phase 4 runs it over *every* item
in a panel, not only the controls.

**`MinF >= MaxF` compares effective values, not stored ones.** `MinF = 90` with
`MaxF` null is rejected against the default 85 — null means "the default", so
validating the raw column would let a nonsensical range through.

**`OnMode` is required whenever `Mode` is bound**, including alongside `Power`,
which is stricter than "needed to be switchable" alone would require. `OnMode` is
what a bound mode channel *means* for that device; making the requirement depend
on which other roles happen to be present would make the stored answer
situational.

**What the apply showed.** `make test-api` went 736 → 758, and
`RoutineServiceTests` passed unchanged — which is the whole point of extracting
`RoutineToggleState` rather than copying it. `RoutineService`'s channel-id
gather also collapsed into `SelectMany(RoutineToggleState.PowerChannelIds)`, so
the "which channels decide this toggle" question now has exactly one answer in
the codebase instead of two that happened to agree.

## [x] Phase 3 — Read path

**Status:** complete

- [x] Add [`src/Aerie.Api/Models/Panels/Dtos.cs`](../../src/Aerie.Api/Models/Panels/Dtos.cs):
      `PanelSummary`, `PanelDto`/`PanelItemDto`/`PanelControlBindingDto`, the
      matching `*WriteRequest` records, and `PanelStateDto` / `PanelItemStateDto`.
- [x] `PanelItemStateDto` carries, per item: id, kind, label, icon, color, and
      for a control — controlKind, `isOn: bool?`, `setpointF: decimal?`,
      `ambientF: decimal?`, `mode: string?`, minF, maxF, stepF; for a routine —
      isToggle, isActive.
- [x] Add `IPanelService` / [`PanelService`](../../src/Aerie.Api/Services/Panels/PanelService.cs)
      under `Services/Panels/`: `GetPanelsAsync` (Included + SortOrder, mirroring
      `RoutineService`) and `GetStateAsync(panelId)`.
- [x] `GetStateAsync` collects only the opened panel's bound channel ids plus its
      routine items' `SetPower` channels, and makes exactly one
      `ChannelLatestValues.GetLatestAsync` call for the union.
- [x] Derive `isOn`: `Power` binding's state `== "on"` when bound; otherwise the
      `Mode` channel's state `!= "off"`. Null when neither is bound.
- [x] Register `IPanelService` in [`Program.cs`](../../src/Aerie.Api/Program.cs)
      next to `IRoutineService`.
- [x] Add `Panels` to `DashboardData` (after `Cameras`, where the tile row sits)
      and compose it into
      [`DashboardService`](../../src/Aerie.Api/Services/Dashboard/DashboardService.cs)'s
      concurrent fan-out.
- [x] [`PanelServiceTests`](../../src/Aerie.Api.Tests/Panels/PanelServiceTests.cs):
      Included/SortOrder filtering; on/off derived from `Power`; on/off derived
      from `Mode` with no `Power`; setpoint and ambient reads; a routine item's
      isActive matching `RoutineService`'s; a control whose channels have no
      samples yet reading all-null rather than throwing.

**`isOn` and `isActive` treat "never reported" differently, on purpose.** A
control whose `Power` channel has no samples reads `isOn: null` — the plan's
own all-null requirement, and the honest answer, since a wall tablet showing a
confident "off" for a device nobody has heard from is worse than one showing
nothing. A toggle routine with no samples reads `isActive: false`, because that
is what `RoutineService` already returns for the same routine's dashboard tile,
and the panel disagreeing with the tile beside it would be the worse bug. Both
behaviors are pinned by tests, including one that asserts the routine answer
equals `RoutineService`'s for the same database.

**A `Power` binding wins over `Mode` even when both are bound.** Phase 2 requires
`OnMode` whenever `Mode` is bound, so a thermostat can carry both; the read path
takes `Power` as the direct answer and falls back to `Mode` only when there is no
`Power` binding. `mode` is reported raw ("cool", "off") rather than compared
against `OnMode`, so the kiosk can show what the device actually says.

**`minF`/`maxF`/`stepF` are resolved server-side, not passed through.** The state
DTO carries `PanelDefaults`-resolved numbers for a thermostat and nulls for a
switch, so the kiosk's client-side clamp and Phase 4's server-side clamp are
working from the same values rather than each re-deriving "null means 60".

**`Read` distinguishes "role not bound" from "bound but silent".** Both would be
an absent dictionary entry if the service looked up channel ids directly, and
`isOn` needs the difference: unbound is null-forever, silent is null-for-now.

**What the apply showed.** `make test-api` went 758 → 777. `DashboardData` now
carries a `panels` field that `dashboard/src/types.ts` does not declare yet —
harmless (an extra JSON key is invisible to TypeScript) and deliberate, because
adding it to the TS type also means updating the mock and test data sources,
which is Phase 6's first two bullets. Phase 3 is service-only: `GetStateAsync`
has no route in front of it until `PanelsController` lands in Phase 4.

## [x] Phase 4 — Write path and admin API

**Status:** complete

- [x] Add [`PanelsController`](../../src/Aerie.Api/Controllers/PanelsController.cs)
      with the CRUD five, items replaced wholesale on write, modelled on
      [`RoutinesController`](../../src/Aerie.Api/Controllers/RoutinesController.cs) —
      plus `GET /{id}/state`, the route Phase 3 left `GetStateAsync` waiting for.
- [x] Create/Update load every referenced channel, run `PanelBindingRules.Validate`
      per item, and answer `BadRequest` with the reason before saving —
      an invalid panel must never reach the database.
- [x] Add `POST /{id}/items/{itemId}/power`: resolve the item, build the
      `CommandRequest` (`SetPower`, or `SetHvacMode` with `OnMode`/`"off"`),
      dispatch through `IClimateCommandService`, map `Rejected` → 400 and
      `Failed` → 502 as the routines endpoints do.
- [x] Add `POST /{id}/items/{itemId}/setpoint`: clamp to `MinF`/`MaxF`, round to
      `StepF`, dispatch `SetTemperature`.
- [x] Reason string on every dispatch: `$"Panel '{panel.Name}' — {item label}"`.
- [x] Both write endpoints 404 on an item that isn't a control, and 400 on a
      control whose kind doesn't support the verb (setpoint on a `Switch`).
- [x] [`PanelCommandTests`](../../src/Aerie.Api.Tests/Panels/PanelCommandTests.cs):
      power via `Power` binding; power via `OnMode` fallback including the
      `"off"` direction; setpoint clamped low; clamped high; rounded to step;
      setpoint refused on a `Switch`.

**The setpoint clamps twice, and the second one is not redundant.** Clamp into
range, snap to the nearest step *measured from `MinF`* (so every reachable value
is one the ⊖/⊕ buttons can also land on), then clamp again — because a range the
step doesn't divide evenly can snap its own top upward and out. 60–73 by 5s is
the case: 73 is 13 above the minimum, the nearest step is 75, and only the
second clamp brings it back to 73. Pinned by its own test.

**"Is this routine id real?" is the one validation `PanelBindingRules` can't
answer.** The class is pure by design, and that question needs the database, so
the controller asks it separately. Without it a bad routine id is a
`DbUpdateException` — a 500 for what is plainly a bad request.

**A routine item, and an item belonging to a different panel, both read as 404.**
`LoadControlAsync` searches the panel's own `Items` and returns nulls for
anything that isn't a control on *that* panel, which makes "the panel is the
boundary" structural rather than a thing each endpoint remembers. The kiosk
triggers a routine item through `/api/routines/{id}/trigger`, as planned.

**`POST .../power` takes `{ on: bool }` rather than toggling.** The wall's copy
of the state can be a poll stale; a toggle would then do the opposite of what
the finger asked for. The absolute form makes a stale client wrong about what it
*displays*, never about what it *sends*.

**What the apply showed.** `make test-api` went 777 → 801. The tests drive the
real `ClimateCommandService` with only Home Assistant faked, which is what makes
them worth having: the whole claim of Phase 4 is that panel writes inherit the
ledger, the guards and the outcome mapping, and a mocked chokepoint would test
none of it. Two of those tests exist because of that choice — a channel turned
read-only *after* a panel bound it reaches the write path as `Rejected` → 400,
and an unreachable HA is `Failed` → 502.

## [x] Phase 5 — Admin UI

**Status:** complete

- [x] Add the `Panel` types and the `getPanels`/`createPanel`/`updatePanel`/
      `deletePanel` calls to `src/Aerie.Web/apps/admin/src/{types.ts,api/client.ts}`.
- [x] Add [`PanelsPage.tsx`](../../src/Aerie.Web/apps/admin/src/pages/PanelsPage.tsx)
      modelled on `RoutinesPage.tsx` — list, create form, per-row edit form,
      `IconPicker`, color, sortOrder, included.
- [x] Item editor: add-routine (a select over existing routines) and
      add-control (a `ControlKind` select), reorderable, removable.
- [x] Per control, render one channel select per role for that kind, filtered to
      channels whose metric matches — and, for the written roles, to `ReadWrite`.
- [x] Thermostat extras: `OnMode` select populated from the bound `HvacMode`
      channel's `availableOptions`, and MinF/MaxF/StepF number inputs showing the
      defaults as placeholders.
- [x] Client-side mirror of the required-role check so the form says what's
      missing before the POST does.
- [x] Add the `/panels` route and nav link in
      [`admin/src/App.tsx`](../../src/Aerie.Web/apps/admin/src/App.tsx), between
      Routines and Calendars.

**The combobox got extracted rather than mirrored.** The plan said to mirror
`RoutinesPage`'s `eligibleChannelOptions`, and that part is a mirror — the
filter is keyed on role rather than kind, and adds the `ReadWrite` half that
`PanelBindingRules.IsWritten` describes. But the ~100-line searchable
`ChannelSelect` under it is not something to mirror, so it moved to
[`components/ChannelSelect.tsx`](../../src/Aerie.Web/apps/admin/src/components/ChannelSelect.tsx)
with a `ChannelChoice` supertype: `RoutinesPage`'s `ChannelOption` now extends
it with the `RoutineActionKind` the metric implies, and passes its richer rows
straight in. `RoutinesPage` lost 90 lines and gained no behavior.

**The rules are duplicated in TypeScript on purpose, and it is not only about
validation.** `ROLE_METRIC`, `roleIsWritten` and `ROLES_FOR` are the form's
*shape*, not its checker: which channel selects exist at all is the question a
control editor has to answer before it renders, and it cannot ask the server
that per keystroke. Each constant names the C# member it mirrors, the error
wording tracks the server's so the same problem reads the same either way it is
caught, and the server still runs the real rules on every write.

**Bindings are held keyed by role, not as a list.** `toItemRequest` sends only
the roles the *current* kind understands, so flipping a control Thermostat →
Switch → Thermostat does not discard the setpoint channel already picked —
and a leftover role can never reach the API as the "`A Switch control has no
Setpoint role`" rejection it would otherwise be.

**A blank number box is null, which is the default — not zero.** `MinF`/`MaxF`/
`StepF` show `PanelDefaults` as placeholders and send null when empty, and the
client's `min >= max` check compares *effective* values for the same reason
Phase 2's does: a min of 90 against a blank max is a min of 90 against 85.

**The form requires at least one item; the API does not.** An empty panel is a
legal row — nothing in `PanelBindingRules` has an opinion about a panel with no
items — but it is a tile on a wall that opens onto nothing, so the form refuses
to create one. Deliberate asymmetry: the API stays permissive about what can
exist, the admin UI is opinionated about what is worth making.

**What the apply showed.** `make test-web` clean across all six apps. One lint
warning appeared and was fixed rather than tolerated: exporting a plain function
(`channelLabel`) beside a component breaks React fast refresh, and nothing
outside the module needed it. No C# changed, so `make test-api` stays at 801.
Browser verification is the user's — Phase 7 is where this page gets driven for
the first time, building the Climate panel itself.

## [] Phase 6 — Kiosk UI

**Status:** not started

- [ ] Add `PanelSummary` and the panel state types to
      [`dashboard/src/types.ts`](../../src/Aerie.Web/apps/dashboard/src/types.ts),
      plus a `PanelSource` interface (state read, power write, setpoint write) —
      the same seam `GatherSource` uses.
- [ ] Add `api/panelsClient.ts` (`ApiPanelSource`), and `mock/mockPanelSource.ts`
      + `mock/testPanelSource.ts`; wire all three into `getPanelSource()` in
      `dataSource.ts` so `?source=` still switches the whole screen.
- [ ] Add `panels` to the mock and test dashboard data sources.
- [ ] Add `PanelsSection.tsx` — the tile row, reusing `.hf-routine-btn` and
      `.hf-routine-circle` selectors rather than restating the geometry.
- [ ] Render it in `App.tsx` between `CamerasSection` and `GatherTile`, guarded on
      `data.panels.length > 0`.
- [ ] Add `hooks/usePanelState.ts`: fetch on open, poll every 5s, and hold an
      optimistic overlay per item with a 30s TTL — `RoutinesSection`'s
      reconcile-when-the-server-agrees plus `GatherOverlay`'s expiry, for the same
      reasons each has.
- [ ] Add `PanelOverlay.tsx` on `GatherOverlay`'s shape: full-screen, mounted
      inside `.hf-page`, a 56px close target, its own ~60s idle close, and the
      dashboard lifecycle held while it is open.
- [ ] Add `SwitchControl.tsx` — label plus a large on/off control, pending and
      error states per control the way `RoutinesSection` does per tile.
- [ ] Add `ThermostatControl.tsx` — an On/Off pill, `⊖ 72° ⊕` with the setpoint as
      the largest thing on the card, ambient beneath it, and the bounds enforced
      client-side too so a disabled `⊕` at max is visible rather than silently
      refused.
- [ ] Hold-to-repeat on `⊖`/`⊕` (~400ms delay, then ~150ms repeat) driving local
      state only; dispatch a single `setpoint` write after ~700ms of quiet.
- [ ] Render a routine item with `RoutinesSection`'s existing trigger/turn-off
      behavior — extracted into a shared component rather than a second copy.
- [ ] Add the `.hf-panel-*` styles to `theme.css`, reusing tile selectors where the
      geometry is shared.
- [ ] `make test-web` clean (lint + build). Leave browser verification to the user.

## [] Phase 7 — The Climate panel itself

**Status:** not started

- [ ] Confirm the AC's channels exist and are mapped: `PowerState` or `HvacMode`,
      `SetpointTemperature`, and a `Temperature` channel for ambient.
- [ ] Confirm both fans have `ReadWrite` `PowerState` channels.
- [ ] In admin, create the "Climate" panel — icon, color, sort order after the
      routines.
- [ ] Add the AC thermostat control: bindings, `OnMode = "cool"`, bounds 60–85,
      step 1.
- [ ] Add Fan 1 and Fan 2 as `Switch` controls.
- [ ] Verify on the wall tablet: on/off both directions, ± single step, hold from
      one end of the range to the other landing exactly **one** command.
- [ ] Check the command ledger — every action present, `Source = Human`, reason
      naming the panel and the control.

## [] Phase 8 — Documentation and dissipation

**Status:** not started

- [ ] Add a Panels section to [`docs/kiosk-architecture.md`](../kiosk-architecture.md):
      where the tile sits and why, the overlay's idle rules, the optimistic model.
- [ ] Add the Panels rows to [`docs/dashboard-api-manifest.md`](../dashboard-api-manifest.md),
      including the new `panels` field on `DashboardData`.
- [ ] Note in [`docs/device-architecture.md`](../device-architecture.md) that a
      panel control binds channels by role, and that new control kinds are an
      enum append plus a kiosk component.
- [ ] Add this plan's row to [`docs/plans/README.md`](README.md)'s Current plans
      table when Phase 1 starts, and remove it at dissipation.
- [ ] Delete this file once the above land — a finished plan is not an archive
      (README, lifecycle step 3).

## Deliberately not in this plan

- **Light and camera controls.** Both are an append to `ControlKind`, a role set
  in `PanelBindingRules`, and a kiosk component. Nothing here should need to move
  for them — that is the test of whether this model was right.
- **Nested panels.** A panel holds routines and controls, not other panels.
- **Panel-level scheduling or automation.** Panels are a surface a person touches;
  the control loop ([`docs/climate-brain-architecture.md`](../climate-brain-architecture.md))
  is the other half and stays separate.
- **Per-control override policy.** A hand on the tablet is `Human`, which already
  bypasses suppression. Actuator policy clamps belong in `ClimateCommandService`
  when they arrive, and panels inherit them without asking.
