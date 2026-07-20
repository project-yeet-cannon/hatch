# Device Mapping Architecture

## Summary

Aerie currently keys everything off raw Home Assistant entity ids: `EfEnvironmentReading.EntityId`, `EfZoneConfig.EntityId`, the `Dashboard` appsettings section, and string-sniffing in `EnvironmentService.MapFromHa` (`_temperature`/`_humidity` suffixes, `climate.`/`sensor.h5110` prefixes). This works for the Mysa thermostats, where one HA entity carries all the readings, but breaks down for hygrometers, where each attribute (temperature, humidity, battery) is a separate HA entity that the app currently has to reassemble via a sparse history model. It also means placement ("this sensor is outside") is expressed as two entity ids buried in config, with `ZoneService` explicitly subtracting them out so they don't render as a zone.

This document defines a device-mapping layer that replaces entity-id string-matching with real domain entities — **Zone**, **Device**, **DeviceChannel** — so that:

- A device-like domain entity can be defined and assigned to a Zone (interior room or Outside) regardless of whether it backs onto one HA entity or several.
- Hygrometers and thermostats are modeled the same way; the "sparse vs. dense" distinction disappears because a channel is always a single (entity, attribute) → single metric mapping.
- Everything currently in the `Dashboard` appsettings section becomes admin-editable data, and that config section is deleted.
- The eventual indoor climate engine (heating/cooling/forced air) has a stable foundation: it operates on Zones, reading `Read` channels and writing `ReadWrite` channels, so adding new actuator types later is new rows, not new architecture.

This is a foundational, multi-phase build. Phases are ordered so each one ships something working; later prompts should reference the phase they're implementing.

## Current State (for reference)

Relevant existing code as of this writing:

- `Aerie.Api/Ef/Models.cs` — `EfEnvironmentReading` (wide/sparse: `Temperature`, `Humidity`, `DesiredTemperature`, `IsHeating` columns, keyed by `EntityId` + `Timestamp`) and `EfZoneConfig` (keyed by HA `EntityId` directly).
- `Aerie.Api/Services/Dashboard/DashboardOptions.cs` — bound from the `Dashboard` appsettings section: `TimeZone`, `SunEntity`, `WeatherEntity`, `OutsideTemperatureEntity`, `OutsideHumidityEntity`, `ComfortToleranceF`, `DefaultComfortLowF`, `DefaultComfortHighF`.
- `Aerie.Api/Services/EnvironmentService.cs` — `MapFromHa` branches on `sensor.`/`climate.` prefixes and `_temperature`/`_humidity` suffixes to decide how to interpret an HA state object.
- `Aerie.Api/Jobs/SampleEnvironments.cs`, `SampleOutside.cs` — poll HA on a fixed interval using hardcoded prefixes (`climate.`, `sensor.h5110`) and the configured outside entity ids.
- `Aerie.Api/Services/Dashboard/ZoneService.cs` — builds dashboard zones from `EfEnvironmentReading` + `EfZoneConfig`, explicitly excluding the configured outside entity ids so they don't appear as zones.
- `Aerie.Api/Services/Dashboard/WeatherService.cs` — sources the Outside card from the same reading table, keyed by the configured outside entity ids.
- `Aerie.Web/apps/admin` — an empty scaffolded app (Vite/React), intended home for this configuration UI.

### Home Assistant examples driving the design

**Baseboard radiator thermostat (Mysa)** — `climate.avery_bedroom` (HA device key `Mysa-1a4c98`, "Avery Bedroom"). One HA entity exposes current temperature and humidity as attributes, plus a mutable setpoint and hvac mode. Trivial to map: one entity, multiple attributes, some read-only, some read-write.

**Hygrometer** — HA device "H6 Kitchen" exposes three separate entities: `sensor.h5110_3853_temperature`, `sensor.h5110_3853_humidity`, and a battery sensor. Naming convention in use: `H# LOCATION` (e.g. `H5 Deck`, `H6 Kitchen`). Harder to map because there is no single entity to key off of — the app currently handles this with a sparse history model.

## Domain Model

**Naming:** the mapped domain entity is called **`Device`**, matching Home Assistant's own "Devices" concept — the admin workflow is literally "import an HA device as an Aerie Device," so keeping the same word avoids a constant mental translation. The entity that does the actual field-level mapping is the **`DeviceChannel`**.

```text
Zone          Id, Name, Kind (Interior | Outside), ComfortLowF/HighF, SortOrder, Included
Device        Id, Name, Kind (Thermostat | Hygrometer | ...), ZoneId (nullable FK), HaDeviceId?, Enabled
DeviceChannel Id, DeviceId FK, Metric (Temperature | Humidity | Battery | SetpointTemperature | HvacAction | HeatingMode),
              HaEntityId, HaAttribute?, Direction (Read | ReadWrite)
SiteSetting   Key, Value        (TimeZone, SunEntity, WeatherEntity, ComfortToleranceF, default comfort band)
```

### How this covers both device shapes

- **Mysa thermostat**: one `Device` with several `DeviceChannel` rows that all share `HaEntityId = climate.avery_bedroom` but differ by `HaAttribute` (`current_temperature`, `current_humidity`, `temperature`, `hvac_action`). The setpoint and mode channels are `Direction = ReadWrite`; the rest are `Read`.
- **Hygrometer**: one `Device` with three `DeviceChannel` rows pointing at three different `HaEntityId`s (`sensor.h5110_3853_temperature`, `..._humidity`, `..._battery`), each reading the entity's bare state (no `HaAttribute`).

A channel is always "one HA (entity, attribute) pair maps to one metric." This is what erases the sparse/dense distinction: a hygrometer isn't a special case, it's just a device whose channels happen to point at different entity ids instead of sharing one.

### Zone becomes first-class

Instead of "a climate entity id that isn't excluded," `Zone` is a real table, and **Outside is just a `Zone` with `Kind = Outside`**. The outside card reads whatever devices are assigned to that zone. This one change:

- Deletes `OutsideTemperatureEntity` / `OutsideHumidityEntity` from config.
- Deletes the exclusion-list logic in `ZoneService.GetZonesAsync` that subtracts outside entity ids out of the zone list.
- Sets up the climate engine: its unit of control is a `Zone`, its inputs are the zone's `Read` channels, and its actuators are the zone's `ReadWrite` channels. New actuator types (cooling, forced air) are new `Device.Kind` / `Metric` values, not new architecture.

## Readings: Narrow Instead of Sparse

Replace the wide `EfEnvironmentReading` table with a narrow measurement table keyed by channel:

```text
Measurement   ChannelId FK, Timestamp, Value (decimal)     unique (ChannelId, Timestamp)
```

Every sample becomes "channel X had value V at time T." The sparse-history workaround existed only because hygrometer attributes arrive as separate HA entities with no shared row to land in; once `DeviceChannel` is the key, that's no longer a special case.

Non-numeric state (`hvac_action`, and future modes like fan speed) doesn't fit a `decimal` column — use a parallel table rather than overloading `Measurement`:

```text
StateChange   ChannelId FK, Timestamp, State (string)
```

**Migration note:** keep `EfEnvironmentReading` read-only during the transition. Backfill `Measurement`/`StateChange` by joining old `EntityId` values to the new channels, verify parity, then drop the old table.

## Device Discovery & Auto-Grouping

The goal (from the original ask): recognize that `H5 Deck`'s separate HA entities belong together, suggest they be grouped as one `Device`, and let the admin assign that device to a `Zone` (e.g. Outside) in one action.

**Constraint:** the HA REST API does not expose the device registry, so there's no direct "list devices" REST call. Two ways to get device-grouping info:

1. **Template endpoint (recommended)** — `POST /api/template` can evaluate Jinja templates including `device_id(entity_id)`, `device_attr(id, 'name')`, and `device_entities(id)`. A single templated request can return entity→device grouping as JSON. HADotNet (already in use) has a `TemplateClient` for this — no new client stack.
2. **WebSocket API** (`config/device_registry/list`) — full registry access, but requires maintaining a second connection/protocol stack alongside the REST clients already in use. Not worth adopting until the template approach proves insufficient.

**Discovery service responsibilities:**

- List all HA entities (existing `EntityClient` usage).
- Resolve each entity to its HA device via the template endpoint.
- Group entities by HA device id.
- Infer a suggested `Device.Kind`: a group containing a `climate.*` entity → Thermostat; a group whose entities end in temperature/humidity/battery → Hygrometer.
- Present HA devices with no matching Aerie `Device.HaDeviceId` yet as "unmapped" in the admin UI, with channels pre-populated from the grouping, for one-click import.

The `H# LOCATION` naming convention becomes a suggested display name for the admin to accept or edit, not something the app parses for meaning.

## Plan of Attack

Each phase should leave the app in a working state.

### Phase 1 — Schema + migration

- Add `Zone`, `Device`, `DeviceChannel`, `SiteSetting`, `Measurement` (and `StateChange`) tables via EF migration.
- Migrate existing `EfZoneConfig` rows into `Zone`.
- Auto-create a `Device` + channels for each currently-known `climate.*` entity.
- Seed `SiteSetting` rows from the current `Dashboard` appsettings section on first run.

### Phase 2 — Discovery + CRUD API

- `DevicesController` — CRUD for devices/channels, assign device to zone.
- `ZonesController` — extend existing controller for zone CRUD (currently only exposes comfort/readings endpoints).
- `SettingsController` — CRUD over `SiteSetting`.
- `DiscoveryService` — wraps the template-endpoint grouping described above; exposes "HA devices not yet mapped."

### Phase 3 — Admin UI

Build out `Aerie.Web/apps/admin` (currently an empty scaffold):

- **Zones page** — list/create/edit zones, reorder, set comfort bands.
- **Devices page** — list devices, assign to zone, enable/disable.
- **Discovery/import page** — shows unmapped HA devices with suggested grouping/kind, one-click import.
- **Settings page** — edit `SiteSetting` scalars (timezone, sun entity, weather entity, comfort defaults).

### Phase 4 — Channel-driven sampling

- Replace `SampleEnvironments` + `SampleOutside` with a single job: load enabled `DeviceChannel`s, group by `HaEntityId` (to minimize HA calls), fetch history once per entity, and write `Measurement`/`StateChange` rows per channel.
- This removes the hardcoded `climate.`/`sensor.h5110` prefixes and the `MapFromHa` suffix-sniffing in `EnvironmentService`.

### Phase 5 — Re-point the read path

- `ZoneService` builds `ZoneClimate` DTOs from `Zone` → `Device` → `DeviceChannel` → `Measurement`, instead of `EfEnvironmentReading` + entity-id exclusion lists.
- `WeatherService` reads the `Zone` with `Kind = Outside` instead of the configured outside entity ids.
- `DashboardOptions` scalar values are replaced by a cached `ISiteSettingsService` reading from `SiteSetting`, instead of `IOptions<DashboardOptions>`.

### Phase 6 — Delete the `Dashboard` config section

- Remove the `Dashboard` section from `appsettings.json`, `appsettings.Docker.json`, and `appsettings.Development.json` (if present there).
- Remove the `DashboardOptions` binding/registration in `Program.cs`.
- Confirm every former key has a new home: entity ids → `Zone`/`Device`/`DeviceChannel` rows; `TimeZone`, `SunEntity`, `WeatherEntity`, comfort defaults → `SiteSetting`.

### Future — Climate engine groundwork (later, related work)

Notes for anticipated future implementation:

- A command service that writes to `ReadWrite` channels via HA's service-call API (e.g. `climate/set_temperature`) — the only new capability the engine needs from this layer.
- The engine's control loop operates per-`Zone`: read current values from `Read` channels, decide, write via `ReadWrite` channels. Adding cooling or forced-air control later means adding new `Device.Kind`/`Metric` values and new command types, not restructuring this layer.

## Open Questions / Decisions to Confirm Before Building

- Naming: `Device` / `DeviceChannel` / `Zone` — confirmed direction, but worth a final check before Phase 1 since every later phase keys off these names.
- Whether `StateChange` is needed at Phase 1 or can be deferred until the climate engine actually needs `hvac_action`/mode history (Phase 1 could ship with `Measurement` only, and `StateChange` added in a future phase when it's first consumed). - ANSWER: DEFER UNTIL NEEDED
- Whether zone comfort band editing stays on `Zone` directly (as it is today on `EfZoneConfig`) or moves to a separate config table — current plan keeps it on `Zone` since it's a per-zone property, not a device property. - ANSWER: KEEP IT ON ZONE
