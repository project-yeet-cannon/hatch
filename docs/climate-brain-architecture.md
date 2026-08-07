# Climate Brain Architecture

## Summary

Aerie's device-mapping layer (see [`device-architecture.md`](device-architecture.md)) ends at "read every channel, write any channel, on demand." This document defines the layer above it: a closed-loop climate controller that decides *on its own* what the AC and fans should be doing, records why, and accumulates the data needed to eventually learn the house's thermal behavior rather than being told it.

Scope for this round is deliberately narrow — **cooling season only**. The baseboard radiators are not controlled; the actuators are the one forced-air AC/blower thermostat and the two standing fans on smart switches. All eleven thermostats and six hygrometers still contribute *readings*, because zone temperature is what the controller optimizes against regardless of which actuator can reach that zone.

Decisions locked in for this round:

- **Shadow before live.** The controller runs, decides, and logs from day one, but dispatches nothing until a mode flag is flipped. Same code path both ways, so the shadow period costs nothing and proves the decisions are sane before the family experiences them.
- **Cost is a runtime proxy.** There is no metering. Cost is estimated watt-minutes from per-device nameplate figures times observed on-time, priced at a flat rate. Good enough to rank actions against each other, which is all the objective function needs; not good enough to predict a bill, and the doc doesn't pretend otherwise.
- **Comfort is expressed as intent, not setpoints.** The family presses ± on a room's kiosk card. That applies a bounded, expiring offset to that zone's comfort band. Nobody types "72".
- **Everything the house-specific physics depends on is data, not code.** AC coverage, device wattage, safety clamps, zone priorities, and experiment tolerance are all rows in tables with an admin UI. This is what lets the learning phase later overwrite a guessed parameter with a measured one without a code change.
- **Every actuation is ledgered.** No command reaches Home Assistant except through a service that first records what is being commanded, by whom, and why.

## Current State (for reference)

What already exists and is being built on:

- `Aerie.Api/Ef/DeviceMapping.cs` — `EfZone` (with `ComfortLowF`/`ComfortHighF`), `EfDevice`, `EfDeviceChannel` (`Direction = ReadWrite` for `SetpointTemperature`/`HvacMode`/`FanMode`/`PowerState`), `EfMeasurement`, `EfStateChange`.
- `Aerie.Api/Services/DeviceMapping/HomeAssistantCommandService.cs` — the complete write side already: `SetPowerAsync`, `SetTemperatureAsync`, `SetHvacModeAsync`, `SetFanModeAsync`. **No new HA capability is needed for this entire document.**
- `Aerie.Api/Jobs/SampleChannels.cs` — polls HA history every minute and writes `Measurement`/`StateChange` per channel.
- `Aerie.Api/Jobs/_Init.cs` — `IAerieJob` + Quartz wiring; a new recurring job is one class.
- `Aerie.Api/Services/DeviceMapping/SiteSettingsService.cs` — cached typed snapshot over `EfSiteSetting`, 30s TTL. New scalars go here.
- `Aerie.Api/Services/Routines/RoutineActionExecutor.cs` and `DevicesController`'s channel-write endpoints — the two existing paths that call HA directly. Both get re-pointed through the ledger in Phase 1.
- `Aerie.Web/apps/admin` — Zones/Devices/Discovery/Settings/Routines pages, the pattern new config UI follows.

### Assumed thermostat capabilities

Per scoping, the AC thermostat is assumed to expose the optimal set: `hvac_action` (so compressor on-time is directly observable rather than inferred), and a `fan_only`/circulate mode in `hvac_modes`. If either turns out to be missing, the fallbacks are contained: without `hvac_action`, compressor on-time is inferred from setpoint-vs-temperature and every cost figure gains a large error bar; without `fan_only`, that value simply never appears in the candidate action set, since the set is enumerated from the channel's `AvailableOptions` rather than hardcoded.

### Data horizon

Sampling has been running only briefly and zone configuration is incomplete, so **this ships onto a nearly empty history**. Two consequences run through the plan:

- The controller cannot start from a fitted model. Its MVP prediction is a static per-(actuator, zone) gain seeded from admin-entered coverage weights. Phase 7 replaces those seeded gains with fitted ones; the controller's structure does not change.
- The shadow period's value is verifying decision *sanity*, not accumulating data. It should be days, not weeks.

## Domain Model

```text
                    ┌─ EfComfortNudge ──┐
EfZone ─────────────┤                    ├──> ComfortResolver ──> effective band per zone
  Priority          └─ EfZoneComfortWindow                                │
  ExperimentsAllowed                                                      v
                                                             ┌── ClimateController ──┐
EfDevice ───┬── EfDeviceZoneCoverage  (which zones it reaches)│  enumerate candidates │
  Est.Watts │                                                 │  score, clamp, choose │
            └── EfActuatorPolicy      (what it may be told)   └───────────┬───────────┘
                                                                          v
                                                    EfControlDecision ── EfCommand ──> ClimateCommandService
                                                                                              │
                                                    EfClimateScore <── rollup            HA service call
```

### 1. AC coverage — `EfDeviceZoneCoverage`

```text
EfDeviceZoneCoverage  Id, DeviceId FK, ZoneId FK, CoverageWeight (decimal, default 1.0), Source (Manual | Fitted)
                      unique (DeviceId, ZoneId)
```

`EfDevice.ZoneId` already answers "where does this device physically sit" — for the AC thermostat that is the one zone it closes its own loop on, which makes every *other* served zone open-loop from the AC's perspective and is precisely where the fans earn their keep. Coverage answers the different question: "which zones does this actuator actually affect, and how strongly."

This is a table rather than a config constant for two reasons. The standing fans get physically moved — that is the whole point of them — and re-pointing a fan at a different room must be an admin edit, not a deploy. And `CoverageWeight` is the first parameter the science layer corrects: Phase 7 writes fitted values back with `Source = Fitted`, and the admin UI shows guessed-vs-measured side by side. A weight of 0 is meaningful ("this actuator provably does nothing for this room").

### 2. Device energy — on `EfDevice`

```text
EfDevice  + EstimatedWattsActive  (decimal?)   compressor + blower for the AC; motor draw for a fan
          + EstimatedWattsIdle    (decimal?)   blower-only circulation; 0 for a fan
SiteSetting + ElectricityCostPerKwh
```

Cost for any interval is `Σ over devices (watts_in_state × minutes_in_state) / 60000 × rate`. The state timeline comes from `hvac_action` / `PowerState` history already being sampled. Nullable throughout: a device with no wattage entered contributes nothing to the cost term, which degrades the ranking rather than breaking it.

### 3. Safety clamps — `EfActuatorPolicy`

One row per controllable device. Absent row means the controller will not touch the device at all — fail-closed is the correct default for a system that can make your house uncomfortable while you sleep.

```text
EfActuatorPolicy  DeviceId FK (unique), ControlEnabled (bool, default false),
                  MinSetpointF, MaxSetpointF          (clamp on commanded setpoint)
                  MinOnMinutes, MinOffMinutes         (dwell; compressor short-cycle protection)
                  MaxCommandsPerHour                  (rate limit / flapping guard)
                  QuietStartLocal, QuietEndLocal      (nullable time-of-day)
                  QuietBehavior (Unrestricted | NoTurnOn | NoChange)
                  OverrideBackoffMinutes              (defer after a detected human override)
```

Per-device rather than global because the physics differ: a compressor needs minutes of off-time it must never violate, a fan switch needs none but sits near a bedroom and shouldn't click on at 2am. `QuietBehavior` distinguishes "don't start it during quiet hours but let it keep running" from "freeze it entirely."

These clamps are enforced in `ClimateCommandService`, not in the controller. The controller may propose anything; the command layer is the single chokepoint that refuses. That means routines, admin UI writes, and future experiment actions all inherit the same protections without each remembering to.

### 4. Zone priority and time-of-day — `EfZone.Priority` + `EfZoneComfortWindow`

```text
EfZone + Priority (decimal, default 1.0)          relative weight in the objective function
       + ExperimentsAllowed (bool, default false)

EfZoneComfortWindow  Id, ZoneId FK, DaysOfWeek (bitmask), StartLocal, EndLocal,
                     ComfortLowF?, ComfortHighF?, Priority?, SortOrder
```

A window overrides the zone's base band and/or priority during its span — "bedrooms are 3× weight and two degrees cooler from 22:00 to 07:00." Overlaps resolve by `SortOrder`, first match wins, so the admin controls precedence explicitly instead of inferring it from specificity rules nobody can remember.

**`ComfortResolver`** is a pure function composing the layers in fixed order, which makes it trivially unit-testable and makes any surprising band explainable by naming the layer responsible:

```text
site defaults  →  zone base band  →  active comfort window  →  active nudge offset  →  effective band
```

### 5. Experiment tolerance

```text
SiteSetting  ExperimentsEnabled, ExperimentMaxDeviationF, ExperimentMaxMinutes, ExperimentMinHoursBetween
EfExperimentWindow  Id, DaysOfWeek (bitmask), StartLocal, EndLocal
EfZone.ExperimentsAllowed  (per-zone opt-in, default false)
```

An experiment may run only when: globally enabled, inside an experiment window, in a zone that opted in, pushing no more than `MaxDeviationF` outside that zone's effective band, for no longer than `MaxMinutes`, no sooner than `MinHoursBetween` after the last one. Every one of those is a row an admin can change. Nothing is compiled in. The experiment engine itself is Phase 8, but the policy schema lands in Phase 2 so the shape is settled before anything depends on it.

### 6. The ledger — `EfCommand` + `EfControlDecision`

```text
EfCommand  Id, ChannelId FK, Kind (SetPower | SetTemperature | SetHvacMode | SetFanMode | TriggerScene | PlayMedia),
           Value, Source (Human | Routine | Controller | Experiment), Reason,
           DecisionId FK?, RequestedAt, DispatchedAt?,
           Outcome (Pending | Succeeded | Failed | Suppressed | Rejected), Error?,
           ConfirmedAt?, ConfirmedValue?, OverriddenAt?

EfControlDecision  Id, Timestamp, Mode (Shadow | Live), InputsJson, ChosenActionJson,
                   Reason, Score, RunnerUpJson?
```

`Kind` covers all six actuation types rather than only the four climate ones, because the ledger's value comes from being *complete* — routines that trigger scenes and play media go through the same path, and a command that isn't in this table didn't happen.

`Rejected` (failed validation here, never reached HA) is separate from `Failed` (HA errored) and `Suppressed` (an override held it back): once the controller is proposing actions, "the clamps refused this" and "Home Assistant was down" need different responses from whoever reads the log. Rejected and Suppressed attempts are persisted rather than dropped, for the same reason.

This is the highest-leverage table in the document and the reason Phase 1 comes first. Every learning step downstream is a join of *"what did we do, when, in what context"* against *"what did the house do next."* Without the ledger, that join does not exist, and no amount of accumulated `Measurement` rows can reconstruct it after the fact. Building it later means discarding every month of data collected before it.

`InputsJson` snapshots the zone states, effective bands, and outside conditions the decision was made from — stored as JSON rather than modeled, because the input set will change as the brain grows and normalizing it now would guarantee a migration every time it does. `RunnerUpJson` keeps the second-best candidate, which turns "why did it do that" into a diff instead of a re-derivation.

### 7. Override detection — `EfControlOverride`

```text
EfControlOverride  Id, DeviceId FK, ChannelId FK, CommandId FK, DetectedAt,
                   ExpectedState, ObservedState, SuppressedUntil
```

A reconciler job compares each channel's latest sample against the most recent dispatched `EfCommand` for it, in two stages:

- **Confirm.** Sample matches what was commanded → set `ConfirmedAt`/`ConfirmedValue`. The observed value is recorded separately from the requested one so a thermostat clamping a requested 60°F up to its own 62°F minimum reads as the discrepancy it is rather than as agreement.
- **Detect.** A *previously confirmed* command whose channel later reads something else → record the override, suppress the device until `now + OverrideBackoffMinutes`, and stamp `OverriddenAt` on the command so one divergence produces one row instead of a fresh one every pass.

Requiring prior confirmation is the load-bearing rule. Without having seen its own value in place first, "the channel doesn't match what we asked for" is just as likely to mean the command never took as it is to mean somebody countermanded it — and inventing overrides out of the former would have the controller disabling itself for reasons nobody could explain. This also sidesteps the sampling lag entirely: `SampleChannels` polls once a minute, so a command simply stays unconfirmed for a poll or two rather than being mistaken for its own override.

Only `Controller`- and `Experiment`-sourced commands defer to an override. A person pressing a button in the admin UI or triggering a routine is telling Aerie what to do, which is the opposite of the situation an override describes.

This is what "single writer" actually means in practice — not that nothing else *can* write, but that Aerie notices when something did and yields instead of fighting it. `OverrideBackoffMinutes` is a global `SiteSetting` for now; Phase 2 moves it onto `EfActuatorPolicy` so a compressor and a fan switch can back off for different lengths of time.

### 8. The controller

One `IAerieJob` at a 5-minute interval:

1. Load every included interior zone's latest temperature/humidity and resolve its effective band.
2. Enumerate the candidate action space — AC mode × setpoint in 1°F steps within the policy clamp × fan1 on/off × fan2 on/off. Roughly 120 candidates, from `AvailableOptions` and `EfActuatorPolicy` rather than hardcoded lists.
3. Score each: `J = Σ_zones (Priority_z × predicted_discomfort_z) + CostWeight × estimated_watts`, where predicted zone temperature applies the static per-(actuator, zone) gain from `CoverageWeight`.
4. Drop candidates violating dwell, rate limits, quiet hours, or override backoff.
5. Keep the current action unless the best candidate improves `J` by more than a hysteresis threshold — this, plus dwell, is what stops the house from oscillating.
6. Write `EfControlDecision`. If `Mode = Live`, write and dispatch `EfCommand` rows for the deltas.

Enumerate-and-score is chosen over a rules ladder specifically because step 3 is the only part that becomes learned. Phase 7 swaps a seeded gain for a fitted coefficient and touches nothing else. A rules ladder would have to be rewritten to become a model.

### 9. Objective — `EfClimateScore`

Hourly rollup, per zone plus a whole-home row: `DiscomfortMinutes`, `WeightedDiscomfortMinutes`, `ActuatorWattMinutes`, `EstimatedCost`. Computed from `Measurement` ⋈ `EfCommand`, stored in Postgres because the learning layer needs to join against it — a Prometheus gauge alone would not survive as training data. Exporting the same figures to the existing Prometheus/Grafana stack ([`metrics-architecture.md`](metrics-architecture.md)) is a small follow-on, not a prerequisite.

Define this early and log it from Phase 1. It is the only thing that can answer "is the brain actually better than leaving the AC at 72," and retrofitting an objective function tends to produce one that flatters whatever was built.

## Plan of Attack

Each phase leaves the app working.

### Phase 1 — Ledger and safe command path

- `EfCommand`, `EfControlDecision` tables + migration.
- `ClimateCommandService` wrapping `IHomeAssistantCommandService`: record → clamp → dispatch → record outcome.
- Re-point `RoutineActionExecutor` and `DevicesController`'s channel-write endpoints through it, so manual actions are ledgered from day one and the controller's later history has a clean baseline of human behavior to compare against.
- Reconciler job for confirmation + override detection.

### Phase 2 — Parameterization schema + admin UI

- `EfDeviceZoneCoverage`, `EfActuatorPolicy`, `EfExperimentWindow`, `EfZoneComfortWindow`; `EfDevice` wattage columns; `EfZone.Priority`/`ExperimentsAllowed`; new `SiteSetting` scalars.
- Admin UI: coverage + wattage + policy editors on the Devices page, priority and comfort windows on the Zones page, experiment policy on Settings.
- `ComfortResolver` with unit tests over the layer-composition order.

### Phase 3 — Comfort intent

- `EfComfortNudge` (ZoneId, DeltaF, CreatedAt, ExpiresAt, Source) + endpoint.
- Kiosk `ZoneCard` gets ± buttons showing the active offset and when it expires.
- Cumulative offset clamped to ±`MaxNudgeF`, expiring at full magnitude rather than decaying — predictable beats smooth for something the family interacts with; decay is a later refinement if the step-off proves jarring.

### Phase 4 — Controller in shadow

- The job, the candidate enumeration, the scorer, `Mode = Shadow`.
- Admin decision-log page: what it would have done, why, what it scored, what came second.
- Watch until the decisions stop surprising you. Days, not weeks, given there is no history to accumulate anyway.

### Phase 5 — Live

- Flip `ControlMode` to `Live`; kill switch on both kiosk and admin that returns to `Shadow` in one tap.
- `ControlEnabled` stays per-device, so the AC can go live while the fans stay in shadow, or vice versa.

### Phase 6 — Objective rollups

- `EfClimateScore` + the hourly rollup job.
- Dashboard trend for weighted discomfort-minutes and estimated cost. Everything after this is judged against these two numbers.

### Phase 7 — System identification

- Nightly fit of a per-zone lumped RC model — `dT/dt = a(T_out − T_in) + b·Solar + c·AC_state + d·Fan_state` — regressed over `Measurement` ⋈ `EfCommand`, coefficients stored per zone with fit quality.
- Fitted gains overwrite `CoverageWeight` with `Source = Fitted`; the controller's scorer switches from seeded gain to fitted coefficient.
- Immediate payoff: predicted time-to-target enables pre-cooling and eliminates overshoot, and zone coupling stops being a guess.
- Needs roughly 2–3 weeks of data *with actuation variety*. A controller that finds one good setting and holds it produces almost no information, which is the entire argument for Phase 8.

### Phase 8 — Experiments and human tasks

- Scheduler targeting the highest-uncertainty coefficients (fan A on/off at fixed AC setpoint, overnight, controlled for outside temperature), executing through the same ledger with `Source = Experiment` under the Phase 2 policy.
- Effect sizes reported against `EfClimateScore`.
- A `HumanTask` table and kiosk card for the things the brain cannot do itself — "move fan 2 to the hallway for 3 days, then report."

### Out of scope this round

Baseboard/heating control (wrong season, and 10 more actuators would triple the action space before the loop has proven itself), CFD in the control loop, occupancy detection, and forecast coupling beyond the existing weather entity.

**CFD stays an offline design tool.** The modeler and the validated OpenFOAM pipeline ([`cfd-validation.md`](cfd-validation.md)) generate *hypotheses* about placement — "a fan here should couple the living room to the back bedroom" — which then become Phase 8 experiments whose measured effect sizes confirm or falsify them. A simulation that has never been checked against a measurement should not be steering the house.

## Open Questions / Decisions to Confirm Before Building

- **Retention.** `ChannelLatestValues`'s own comment notes there is no retention job and history grows unbounded. The learning layer makes this materially worse — it wants every raw minute it can get. Proposal: keep raw 1-minute samples for ~90 days, downsample to 15-minute aggregates beyond that, since RC fitting works fine on 15-minute data for long-horizon terms. Needs a decision before `EfMeasurement` volume forces one.
- **Controller interval as config.** `IAerieJob.Interval` is a compile-time property. Making the control loop's cadence a `SiteSetting` means either re-scheduling the Quartz trigger on change or running at a fixed fast tick and no-oping. Fixed 5 minutes for MVP; revisit if it chafes.
- **Nudge scope.** A nudge applies to one zone. Should a nudge in a zone the AC serves implicitly pull its neighbors, given coverage weights already describe the coupling? Deferring — explicit per-zone is easier to explain to the family, and Phase 7's fitted coupling is a better basis for this than a guess would be.
- **Whether `EfActuatorPolicy` should be per-device or per-channel.** Currently per-device, since dwell and quiet hours are properties of the physical machine. Setpoint min/max is arguably a property of the setpoint *channel*. Keeping it on the device for now; revisit if a device ever gains two setpoint channels.
