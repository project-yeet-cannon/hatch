# Climate Brain — build log

**Plan of record:** [docs/climate-brain-architecture.md](docs/climate-brain-architecture.md). That doc holds the reasoning — why each table exists, what the objective function is, what's deliberately out of scope. This file holds the sequencing.

**How to use this file:** one phase (or lettered sub-phase) per commit, each leaving the app working. Every phase below is written to be picked up from a cold context: it names the goal, the files, the steps, and how to know it's done. Start a phase by reading the architecture doc's matching section, then this entry.

**Verification, everywhere:** `make build`, `make test-api`, and `make test-web` for frontend work. Migrations are generated with `make ef-migration migration=Name` and applied to the local `db` container with `make ef-database-update`. Per repo convention, UI is not browser-tested here — build and lint it and leave the clicking to a human.

## Status

- [x] **Phase 1** — Ledger and safe command path
- [ ] **Phase 1.5** — Fix the `HvacMode` channel attribute (blocks override detection on the AC)
- [ ] **Phase 2a** — Parameterization schema + API
- [ ] **Phase 2b** — Parameterization admin UI
- [ ] **Phase 3** — Comfort intent (nudges)
- [ ] **Phase 4** — Controller in shadow mode
- [ ] **Phase 5** — Go live
- [ ] **Phase 6** — Objective rollups
- [ ] **Phase 7** — System identification
- [ ] **Phase 8** — Experiments and human tasks
- [ ] **Cross-cutting** — Measurement retention (decide before volume forces it)

---

## Phase 1 — Ledger and safe command path ✅

Shipped. Every write to Home Assistant now passes through `IClimateCommandService`, which records the intent, validates it, checks for an active manual override, dispatches, and records the outcome.

- `Ef/ClimateControl.cs` — `EfCommand`, `EfControlDecision`, `EfControlOverride`, plus the `CommandSource`/`CommandKind`/`CommandOutcome`/`ControlMode` enums.
- `Services/ClimateControl/ClimateCommandService.cs` — the single chokepoint. Nothing else resolves `IHomeAssistantCommandService`.
- `Services/ClimateControl/CommandExpectation.cs` — pure validation + command→expected-observation mapping, shared by the service and the reconciler.
- `Jobs/ReconcileCommands.cs` — confirmation and override detection, every 2 minutes.
- `Services/Routines/RoutineCommandMapper.cs` — replaced `RoutineActionExecutor`; routines now map to ledgered `CommandRequest`s instead of calling HA themselves.
- `Controllers/CommandsController.cs` — read-only ledger/override views (`GET /api/commands`, `GET /api/commands/overrides`).
- `DevicesController` channel-write endpoints and `RoutinesController.Trigger` re-pointed through the ledger.
- Migration `AddCommandLedger`. New `SiteSetting`: `OverrideBackoffMinutes` (default 120).

**Behavior changes worth knowing about:** a routine action with a malformed value is now `Rejected` and visible in the ledger instead of being silently coerced (`SetPower` with a non-boolean used to mean "off"); a routine stops at its first failed action instead of pushing on; HA failures surface as 502 rather than 500.

---

## Phase 1.5 — Fix the `HvacMode` channel attribute

**Why first:** `ThermostatChannelBuilder.Build` maps `HvacMode` to `HaAttribute = "hvac_mode"`, but a Home Assistant `climate.*` entity carries its mode as the entity **state**, not as an attribute (the attributes are `hvac_modes`, `hvac_action`, `fan_mode`, `temperature`, `current_temperature`). If that's right, `ChannelValueExtractor` reads null for every `HvacMode` channel, which means no `StateChange` rows, no confirmation, and no override detection on the AC's mode — the single most important actuator in the MVP.

**Steps**

1. Verify against the real HA before changing anything: `GET /api/states/climate.<your_ac>` and check whether `hvac_mode` appears in `attributes`.
2. If confirmed, change the `HvacMode` channel in `Services/DeviceMapping/ThermostatChannelBuilder.cs` to `HaAttribute = null` (bare state), matching how hygrometer channels read.
3. Existing rows won't fix themselves — either re-import the affected devices through Discovery, or `UPDATE "DeviceChannels" SET "HaAttribute" = NULL WHERE "Metric" = <HvacMode>`.
4. Backfill history for the corrected channels via `POST /api/devices/{id}/backfill`.

**Done when:** an `HvacMode` channel shows a live value in the admin Devices page, and a mode change through the admin UI reaches `ConfirmedAt` in `GET /api/commands` within a few minutes.

---

## Phase 2a — Parameterization schema + API

Architecture doc sections 1–5. Everything house-specific becomes data.

**Steps**

1. New EF entities (a `Ef/ClimateConfig.cs` alongside `Ef/ClimateControl.cs`):
   - `EfDeviceZoneCoverage` — `DeviceId`, `ZoneId`, `CoverageWeight` (default 1.0), `Source (Manual | Fitted)`, unique on (DeviceId, ZoneId).
   - `EfActuatorPolicy` — one row per device, unique `DeviceId`; `ControlEnabled` (**default false** — fail-closed), `MinSetpointF`, `MaxSetpointF`, `MinOnMinutes`, `MinOffMinutes`, `MaxCommandsPerHour`, `QuietStartLocal`, `QuietEndLocal`, `QuietBehavior (Unrestricted | NoTurnOn | NoChange)`, `OverrideBackoffMinutes?`.
   - `EfZoneComfortWindow` — `ZoneId`, `DaysOfWeek` bitmask, `StartLocal`, `EndLocal`, `ComfortLowF?`, `ComfortHighF?`, `Priority?`, `SortOrder`.
   - `EfExperimentWindow` — `DaysOfWeek`, `StartLocal`, `EndLocal`.
2. Column additions: `EfDevice.EstimatedWattsActive/EstimatedWattsIdle` (nullable decimal); `EfZone.Priority` (default 1.0), `EfZone.ExperimentsAllowed` (default false).
3. New `SiteSettingKeys` + `SiteSettingsSnapshot` members: `ElectricityCostPerKwh`, `CostWeight`, `ExperimentsEnabled`, `ExperimentMaxDeviationF`, `ExperimentMaxMinutes`, `ExperimentMinHoursBetween`.
4. `Services/ClimateControl/ComfortResolver.cs` — pure, no DB: `site defaults → zone base band → active comfort window (first match by SortOrder) → nudge offset → effective band`. Nudges arrive in Phase 3; take the offset as a parameter now so the signature doesn't churn.
5. Move clamp enforcement into `ClimateCommandService`: setpoint clamping, dwell/`MinOffMinutes` against the last command for the channel, `MaxCommandsPerHour`, quiet hours, and `ControlEnabled` — all applied to `Controller`/`Experiment` sources only, producing `CommandOutcome.Rejected` with a reason. Move `OverrideBackoffMinutes` lookup from the global setting to `EfActuatorPolicy` with the setting as fallback.
6. API: coverage + policy sub-resources on `DevicesController`; priority + comfort windows on `ZonesController`. `SettingsController` is generic key/value and needs no change.
7. Migration `AddClimateParameterization`.

**Tests:** `ComfortResolver` layer composition (each layer overriding the one before, overlapping windows resolving by `SortOrder`, no window matching), and each new clamp in `ClimateCommandServiceTests` — especially that a device with no `EfActuatorPolicy` row is refused.

**Done when:** every house-specific number named in the architecture doc is settable through the API, and a `Controller`-sourced command to a device with no policy row is `Rejected`.

---

## Phase 2b — Parameterization admin UI

**Steps**

1. `apps/admin/src/pages/DevicesPage.tsx` — per-device editors for zone coverage (zone + weight, showing `Manual` vs `Fitted` provenance), wattage, and actuator policy.
2. `apps/admin/src/pages/ZonesPage.tsx` — priority weight, plus comfort-window CRUD (day-of-week picker, time range, optional band/priority override, sort order).
3. `apps/admin/src/pages/SettingsPage.tsx` — the new scalars, grouped under a "Climate control" heading.

**Done when:** `make test-web` passes and the whole Phase 2a schema is reachable without curl. Leave the clicking to a human.

---

## Phase 3 — Comfort intent (nudges)

**Steps**

1. `EfComfortNudge` — `ZoneId`, `DeltaF` (signed), `CreatedAt`, `ExpiresAt`, `Source` (kiosk id or user). Migration.
2. `SiteSetting`s: `NudgeTtlMinutes` (default 180), `MaxNudgeF` (default 4).
3. `POST /api/zones/{id}/nudge` — applies `DeltaF`, clamping the *cumulative* active offset to ±`MaxNudgeF`. Expire at full magnitude rather than decaying: predictable beats smooth for something a family interacts with. Revisit only if the step-off proves jarring.
4. Wire the resolved offset into `ComfortResolver`'s nudge layer.
5. `apps/dashboard/src/components/ZoneCard.tsx` — ± buttons showing the current offset and when it expires.

**Done when:** pressing − on a kiosk zone card visibly moves that zone's effective band, and the offset lapses on its own.

---

## Phase 4 — Controller in shadow mode

Architecture doc section 8. The loop runs and records; it dispatches nothing.

**Steps**

1. `SiteSetting` `ControlMode` (`Shadow` | `Live`), defaulting to `Shadow`.
2. `Services/ClimateControl/ControlCandidates.cs` — pure enumeration of the action space from `AvailableOptions` + `EfActuatorPolicy` clamps (AC mode × setpoint in 1°F steps × each fan on/off). No hardcoded mode lists.
3. `Services/ClimateControl/ControlScorer.cs` — pure: `J = Σ_zones (Priority_z × predicted_discomfort_z) + CostWeight × estimated_watts`, predicting zone temperature with the static per-(actuator, zone) gain from `CoverageWeight`. Keep the gain lookup behind a seam — Phase 7 replaces it with a fitted coefficient and should touch nothing else.
4. `Jobs/ClimateControl.cs` — 5-minute `IAerieJob`. Load zone states → resolve bands → enumerate → score → filter by clamps → apply hysteresis (keep the current action unless the best candidate improves `J` by more than a threshold) → write `EfControlDecision` with inputs, chosen action, runner-up, and reason. Dispatch only when `ControlMode = Live`. Follow `ReconcileCommands`' split of `Execute(IJobExecutionContext)` from a directly-testable method.
5. Admin decision-log page: what it would have done, why, what it scored, what came second.

**Tests:** the scorer and the candidate enumerator are pure — cover hysteresis holding a marginally-worse current action, clamps removing candidates, and an empty candidate set degrading to "do nothing" rather than throwing.

**Done when:** decisions accumulate for a few days and stop being surprising. Days, not weeks — there's no history being banked here that Phase 7 needs, so the only question this period answers is whether the decisions are sane.

---

## Phase 5 — Go live

**Steps**

1. Flip `ControlMode` to `Live`; the job now dispatches with `Source = Controller` and its decision id attached.
2. Kill switch on both the admin and kiosk dashboards — one tap back to `Shadow`.
3. Confirm `ControlEnabled` works per-device, so the AC can go live while the fans stay in shadow.

**Done when:** the controller has run unattended through a full day and night, and the override ledger shows either nothing or overrides you agree with.

---

## Phase 6 — Objective rollups

**Steps**

1. `EfClimateScore` — hourly, per zone plus a whole-home row: `DiscomfortMinutes`, `WeightedDiscomfortMinutes`, `ActuatorWattMinutes`, `EstimatedCost`. Migration.
2. Hourly rollup job over `Measurement` ⋈ `EfCommand`, computing actuator on-time from `hvac_action`/`PowerState` history and pricing it with the Phase 2a wattage + rate.
3. Dashboard trend for weighted discomfort-minutes and estimated cost.
4. Optional follow-on: export the same figures to the existing Prometheus/Grafana stack ([docs/metrics-architecture.md](docs/metrics-architecture.md)). Postgres stays the source of truth — Phase 7 has to join against it.

**Done when:** you can answer "is this better than leaving the AC at 72" with a number instead of an impression.

---

## Phase 7 — System identification

**Steps**

1. Nightly job fitting a per-zone lumped RC model — `dT/dt = a(T_out − T_in) + b·Solar + c·AC_state + d·Fan_state` — over `Measurement` ⋈ `EfCommand`. `SolarCalculator` already exists for the solar term.
2. Store coefficients per zone with fit quality; write fitted gains back to `EfDeviceZoneCoverage` with `Source = Fitted` so the admin UI shows guessed vs. measured side by side.
3. Switch `ControlScorer`'s gain lookup from seeded to fitted where a good fit exists; fall back to seeded otherwise.
4. Use predicted time-to-target for pre-cooling.

**Needs:** ~2–3 weeks of data **with actuation variety**. A controller that finds one good setting and holds it produces almost no information, which is the whole argument for Phase 8.

---

## Phase 8 — Experiments and human tasks

**Steps**

1. Experiment scheduler targeting the highest-uncertainty coefficients (e.g. fan A on/off at a fixed AC setpoint, overnight, controlled for outside temperature), dispatching through the ledger with `Source = Experiment` under the Phase 2a policy — enabled, in-window, zone opted in, within `MaxDeviationF`, under `MaxMinutes`, after `MinHoursBetween`.
2. Effect sizes reported against `EfClimateScore`.
3. `HumanTask` table + kiosk card for what the brain can't do itself: "move fan 2 to the hallway for 3 days, then report."
4. CFD stays offline — the modeler and the validated OpenFOAM pipeline ([docs/cfd-validation.md](docs/cfd-validation.md)) generate placement hypotheses that become experiments here. A simulation that hasn't been checked against a measurement doesn't steer the house.

---

## Cross-cutting — Measurement retention

`ChannelLatestValues`' own comment notes there's no retention job and history grows unbounded; the learning layer makes it worse by wanting every raw minute. Proposal on the table: raw 1-minute samples for ~90 days, downsampled to 15-minute aggregates beyond that (long-horizon RC terms fit fine on 15-minute data). Needs a decision before `Measurement` volume forces one. Whatever is chosen must not silently discard the `EfCommand` rows the fits join against.

---

<details>
<summary>Original brief</summary>

I live in a house which is only about 40 years hold but was built for a climate that has changed since its building. It was seemingly built with little to no heating/cooling and all was added afterwards.

We have 11 different baseboard radiator thermostats controlling electric radiators in different rooms. There is a forced-air air conditioner connected to some of the rooms. Our home has a non-standard layout with large open areas and lots of room adjacencies.

I have a smart home system setup using HomeAssistant and the distributed system in this repository. Relevant devices hooked up to the system are:

- 6 hygrometers, read temperature/humidity from known locations
- 10 smart electric baseboard radiator thermostats, provide temperature/humidity; control mode and set temp
- 1 smart AC/blower thermostate, provide temperature/humidity; control fan, mode, and temp
- 2 standing fans controlled by smart switches whos on/off are readable/controllable. These provide air flow around the house where the existing ventilation is lacking

My motivating purpose for writing Aerie is to create a "fly-by-wire" climate brain for our home. With my family as users of the system, I want to be able to provide input to devices around the home to make it cooler or warmer. I want the climate brain to perform the most economical and effective change in the home device topography to accomplish the change. I want it to learn over time, self-perform science to figure out how various weather conditions and home control parameters interplay to create climate conditions throughout the home. I want it to give me suggestions and have me change things (e.g. move standing fans around, add fans, etc) to do hard science and figure out how to take climate control of my home. I want to do engineering shit like 3d model the house and run computational fluid dynamics (CFD) on air flows and deterministically figure out how to improve things. Over time I want to get my power bills down and my home naturally comfortable at all times without thinking about it.

I want to build in MVP which is as minimal as possible. We can make assumptions - for instance, it is August and we do not need to worry about heating the house. No need to control the baseboard radiators, just the AC and fans.

Aerie is the foundation for this brain - what are the best next steps and implementation recommendations for an actionable, reliable MVP climate control?

</details>
