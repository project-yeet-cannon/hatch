# Dashboard API Manifest

A proposed set of controllers / REST actions for `Aerie.Api` to serve the
dashboard (`Aerie.Dashboard`) from real data, replacing the current
`MockDashboardDataSource`.

Status: **proposal / design doc.** Nothing here is implemented yet.

---

## Guiding principle

The TypeScript contract ([`types.ts`](../src/Aerie.Dashboard/src/types.ts)) deliberately
carries **only raw physical quantities** — temperatures, humidity, timestamps.
All presentation (badge text, "warming"/"cool" status, comfort classification,
the outside `note`) is derived on the client in
[`zonePresentation.ts`](../src/Aerie.Dashboard/src/lib/zonePresentation.ts).

**The API keeps that split.** It is a *data provider*, not a view builder: it
returns numbers that satisfy `DashboardData`, and the client keeps deriving
labels. This means the API surface is stable even if we restyle the UI.

---

## Endpoint manifest

Primary endpoint is a **backend-for-frontend (BFF) aggregate** that returns the
whole `DashboardData` in one call — this is what `App.tsx` consumes. The
granular sub-resources exist for reuse, debugging, caching, and future screens.

All under an `/api` prefix to stay clearly separated from the static `/dashboard`
route and `/swagger`.

| Method & route | Returns | Purpose |
|---|---|---|
| `GET /api/dashboard` | `DashboardData` | **Primary.** Composes zones + outside into the exact client contract. |
| `GET /api/zones` | `ZoneClimate[]` | All indoor zones with current snapshot + today's low/high. |
| `GET /api/zones/{id}` | `ZoneClimate` | One zone, including history + forecast. |
| `GET /api/zones/{id}/readings?from&to&bucketMinutes` | `TempPoint[]` | Raw/bucketed time series for a zone. |
| `GET /api/outside` | `OutsideClimate` | Weather + sun for the house location. |
| `GET /api/zones/{id}/comfort` · `PUT …` | `ComfortRange` | Read/set a zone's comfort band (admin). |

Query params on `/api/dashboard` (all optional, sensible defaults):
`historyHours` (default 9), `forecastHours` (default 7), `bucketMinutes`
(default 30) — these mirror what `MockDashboardDataSource` produces today.

### Proposed controllers & services

```
Controllers/
  DashboardController.cs      GET /api/dashboard          -> IDashboardService
  ZonesController.cs          GET /api/zones[/{id}[/readings]]
                              GET/PUT /api/zones/{id}/comfort
  OutsideController.cs        GET /api/outside            -> IWeatherService
  HomeAssistantController.cs  (existing) ingestion only — see note below

Services/
  IDashboardService     composes ZoneClimate[] + OutsideClimate
  IZoneService          reading queries, bucketing, daily extremes  (extends EnvironmentService)
  IWeatherService       current + forecast + sun  (HA weather/sun, or NWS)
  IForecastService      short-horizon temp projection per zone
  IZoneRegistry         entityId -> {displayName, comfortRange, order, included}
```

The existing `HomeAssistantController` mixes **ingestion** (`POST` pulls from HA
into the DB — used by the Quartz job's manual trigger) with **ad-hoc reads**.
Recommend narrowing it to ingestion/admin and moving all dashboard reads to the
controllers above, so "serve the dashboard" has one clear home.

---

## Contract → real-source mapping (the gap analysis)

This is where the real work is. `✓` = data exists today; `⚠` = exists but needs
a fix; `✗` = **no source yet**.

| Contract field | Real source | Status |
|---|---|---|
| `generatedAt` | server clock | ✓ |
| `timezone` | config — already `America/New_York` | ✓ |
| `zones[].id` | `EfEnvironmentReading.EntityId` (`climate.*`) | ✓ |
| `zones[].name` | Mysa `FriendlyName` — **parsed but not persisted** | ⚠ needs zone registry or persist-on-ingest |
| `zones[].currentTempF` | latest reading | ⚠ **mapping bug — stores setpoint, drops measured temp** (see prerequisites) |
| `zones[].history` | `EnvironmentReadings` query, bucketed | ✓ |
| `zones[].forecast` | — | ✗ no forecasting exists |
| `zones[].low` / `high` | derived from history (daily min/max) | ✓ computed |
| `zones[].comfortRange` | — | ✗ not an HA concept; needs config |
| `outside.currentTempF` / `humidityPct` | — | ✗ `climate.*` are **indoor**; needs weather source |
| `outside.sunHoursRemaining` / `sunsetTime` | — | ✗ needs `sun.sun` or astronomy calc |
| `outside.precipitation` / `hourly[].cloudCoverPct` / `precipIn` | — | ✗ needs weather forecast |
| `outside.note` | derived text | ✓ derivable (keep on client, like today) |

Available-but-unused-by-contract (future enhancements, not gaps): indoor
`Humidity`, thermostat setpoint (`DesiredTemperature`), and `IsHeating` are all
already stored — the chart could later show setpoint lines or a "heating now"
indicator.

---

## Tradeoffs & mitigations

### 1. Aggregate (BFF) vs. granular REST
- **Chosen: BFF primary + granular sub-resources.** One round trip for the
  dashboard, client stays dumb, response maps 1:1 to `DashboardData`.
- *Tradeoff:* `/api/dashboard` is a bespoke, non-orthogonal endpoint; it can
  fan out to several slow sources (HA weather) and be as slow as the slowest.
- *Mitigation:* build it *on top of* the granular services so nothing is
  duplicated; fetch zones and outside concurrently; short-cache outside (below).

### 2. Comfort ranges have no source
- *Tradeoff:* every option trades config effort against flexibility. Hardcoding
  in `appsettings` is trivial but static; deriving from the thermostat setpoint
  is zero-config but wrong (the comfort band would jump every time someone nudges
  the thermostat).
- **Recommended: a `ZoneConfig` table** (`entityId, displayName, comfortLowF,
  comfortHighF, sortOrder, included`). One table simultaneously fixes the
  **name** gap, comfort band, zone **ordering**, and *which* `climate.*` entities
  count as dashboard zones.
- *Mitigation for MVP:* when a zone is unconfigured, fall back to
  `DesiredTemperature ± 2°F` so the dashboard works before the table is seeded.

### 3. No forecast exists
- *Tradeoff:* the chart's dashed "forecast" line is core to the design. Returning
  `forecast: []` is honest but leaves the chart visually bare on the right.
- **Recommended phasing:** v1 returns `forecast: []` (the chart already tolerates
  it — `TempChart` just draws no dashed segment); then add `IForecastService`
  with a **naive short-horizon projection** (port the diurnal model already in
  [`diurnal.ts`](../src/Aerie.Dashboard/src/mock/diurnal.ts) to C#, or extend the
  last-N-sample slope). A real thermal/weather-coupled model is a later, separate
  effort.
- *Mitigation:* mark projected points clearly (they already live in a separate
  `forecast[]` array, so no ambiguity with measured `history[]`).

### 4. The entire Outside card needs a weather integration
- *Tradeoff:* new external dependency. Two realistic sources:
  - **HA `weather.*` + `sun.sun` entities** — reuses the existing HADotNet
    plumbing, no new API key, single integration surface. Requires those
    integrations to be configured in *your* HA. **Recommended primary.**
  - **NWS `api.weather.gov`** — free, no key, US-only (fine for NY), strong
    hourly precip/cloud forecast; sunrise/sunset via calc. Good documented
    fallback if HA has no weather entity.
- *Mitigation:* put it all behind `IWeatherService` so the source is swappable;
  `sunHoursRemaining = next_setting − now`, `sunsetTime = next_setting`.

### 5. Time-series volume
- Minutely samples over 9h ≈ 540 points/zone — small, but noisy for the chart
  and needless payload.
- *Mitigation:* **bucket server-side.** Postgres 18 has `date_bin('30 minutes',
  timestamp, …)` for clean averaged buckets. Note EF Core can't translate
  `date_bin` — use `FromSqlRaw`/a raw aggregate query in `IZoneService`.

### 6. Freshness vs. load
- Dashboard polls every 60s; the Quartz job also ingests every 60s, so DB reads
  are ≤1 min stale regardless. Weather calls are the expensive part.
- *Mitigation:* response-cache `/api/outside` ~5 min (weather doesn't move faster)
  and leave zone reads uncached. ETag on `/api/dashboard` is a cheap win.

### 7. JSON contract drift
- The C# DTOs must serialize to *exactly* `types.ts` (camelCase, ISO-8601 dates).
- *Mitigations:* (a) confirm System.Text.Json camelCase is on (default) and dates
  are `DateTimeOffset` → ISO; (b) **generate the TS types from OpenAPI** — Swagger
  is already wired, so `openapi-typescript` against `/swagger/v1/swagger.json`
  makes the C# side the single source of truth and kills drift.

### 8. Exposure / auth
- No auth today; fine on the LAN, and the dashboard is same-origin with the API.
- *Flag (out of scope):* if this is ever exposed beyond the house, the read
  endpoints and especially `PUT …/comfort` and the ingestion `POST` need auth.

---

## Prerequisites (data-quality fixes that block correctness)

1. **`EnvironmentService.MapFromHa` records the wrong temperature.** It sets both
   `Temperature` and `DesiredTemperature` to the Mysa **setpoint** (`temperature`)
   and never reads `CurrentTemperature`. The dashboard's `currentTempF` and
   history would show *setpoints, not measured room temp*. Fix: `Temperature =
   CurrentTemperature`, keep `DesiredTemperature = temperature`. (Historical rows
   already stored are similarly affected.)
2. **Persist the zone display name** (or resolve it via the `ZoneConfig` table) —
   `FriendlyName` is parsed then dropped.
3. **Confirm units** — Mysa values are `int`; assume °F (NY house). No conversion
   needed if so; assert it somewhere so a Celsius device doesn't silently corrupt
   the chart.

---

## Suggested build order

1. **Prereq fix #1** (measured-temp mapping) — nothing downstream is right without it.
2. `ZoneConfig` table + `IZoneRegistry`; `IZoneService` (bucketed readings, daily
   extremes). Ship `GET /api/zones` + `/api/zones/{id}/readings`.
3. `DashboardController` / `IDashboardService` returning zones with
   `forecast: []` and a real (or setpoint-fallback) comfort band. **At this point
   the indoor half of the dashboard runs on real data.**
4. Point the frontend at it: add `ApiDashboardDataSource` in
   [`dataSource.ts`](../src/Aerie.Dashboard/src/dataSource.ts) as the default,
   keep mock/test behind `?source=mock|test`.
5. `IWeatherService` (HA weather/sun) + `OutsideController` → the Outside card.
6. `IForecastService` — replace the empty forecast with a real projection.
```
