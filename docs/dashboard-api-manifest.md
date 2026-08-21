# Aerie API surface

Every HTTP endpoint `Aerie.Api` serves, what consumes it, and the rules that
hold across all of them.

> **History.** This file began as a design proposal for endpoints that did not
> exist yet, written against a contract-vs-source gap analysis. Everything it
> proposed shipped, and a good deal it never mentioned shipped alongside. It is
> now a reference for what is actually there. The reasoning that used to live
> here — why a BFF aggregate, why comfort ranges need their own table, why
> weather sits behind an interface — moved to
> [`device-architecture.md`](device-architecture.md) and
> [`climate-brain-architecture.md`](climate-brain-architecture.md), which are
> where those decisions are maintained.

---

## Rules that apply everywhere

- **The API is a data provider, not a view builder.** It returns raw physical
  quantities — temperatures, humidity, timestamps — and the client derives every
  label, badge and status from them
  ([`zonePresentation.ts`](../src/Aerie.Web/apps/dashboard/src/lib/zonePresentation.ts)).
  Restyling the UI does not move the API surface.
- **Read paths hit Postgres, jobs talk to the outside world.** Nothing in a
  request path calls Home Assistant, Google, or a weather service on the request's
  behalf — the sampling and sync jobs fetch, the database caches, the endpoints
  read. The two deliberate exceptions are noted where they occur
  (`GET /api/dashboard`'s weather condition note, and the actuation endpoints,
  which exist to reach HA).
- **Outbound calls fail soft.** A dead integration degrades one section of one
  response rather than throwing out of it.
- **Contract parity.** `Models/Dashboard/DashboardData.cs` and
  [`types.ts`](../src/Aerie.Web/apps/dashboard/src/types.ts) are two halves of one
  contract; so are the dashboard's mock and test data sources, which must produce
  every field the C# side does or `make test-web` fails on the type error.
- **No authentication.** Every endpoint below is reachable by anyone who can
  reach the host, which today means anyone on the LAN or the tailnet — the domain
  has no public DNS ([`reverse-proxy-architecture.md`](reverse-proxy-architecture.md),
  [`tailscale-vpn-architecture.md`](tailscale-vpn-architecture.md)). The two
  exceptions carry their own gate:
  `POST /api/vm-console-logs` (shared-secret header, because it is the one
  server-to-server caller) and the secret-valued settings, which are redacted on
  read. Adding auth is worth its own plan; several endpoints below — the OAuth
  start, the provisioning info, the actuation POSTs — assume it does not exist yet.
- **Three replicas.** Nothing may sit in process memory that a second replica
  needs; the OAuth state table is what this rule looks like in practice.
- **Shape conventions.** camelCase JSON, `DateTimeOffset` for every instant,
  `Guid` ids constrained in routes as `{id:guid}`.

---

## Dashboard and climate

The dashboard's primary endpoint is a **backend-for-frontend aggregate**: one
round trip returns the whole `DashboardData` the kiosk renders. The granular
sub-resources under it exist for reuse, debugging, and other screens.

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/dashboard` | `DashboardData` | **Primary.** Composes zones + outside. Query: `historyHours` (9), `forecastHours` (7), `bucketMinutes` (30). |
| `GET /api/zones` · `GET /api/zones/{id}` | `ZoneDto` | Zone CRUD for the admin app. |
| `POST /api/zones` · `PUT /api/zones/{id}` · `DELETE /api/zones/{id}` | `ZoneDto` | |
| `GET /api/zones/climate` · `GET /api/zones/{id}/climate` | `ZoneClimate` | Current snapshot, history, forecast. Same window query params as `/api/dashboard`. |
| `GET /api/zones/{id}/readings` | `TempPoint[]` | Bucketed series. Query: `from`, `to`, `bucketMinutes`. |
| `GET /api/zones/{id}/comfort` · `PUT …` | `ComfortRange` | The zone's comfort band. |
| `GET /api/outside` | `OutsideClimate` | Outside temperature, humidity, sun. |
| `GET /api/sun-events` | `SunEvents` | Computed locally (`SolarCalculator`), not read from HA. Query: `at`, `lat`, `lon` — all defaulting to now and the site's coordinates, which is what makes it testable against simulated dates. |

## Devices, channels, and actuation

Devices and their channels are the mapping layer over Home Assistant entities
([`device-architecture.md`](device-architecture.md)). The `POST` endpoints under
a channel are the ones that reach the house.

| Method & route | Purpose |
|---|---|
| `GET /api/devices` · `GET /api/devices/{id}` | Devices with their channels. |
| `POST /api/devices` · `PUT /api/devices/{id}` · `DELETE /api/devices/{id}` | Device CRUD. |
| `POST /api/devices/{id}/channels` · `PUT …/{channelId}` · `DELETE …/{channelId}` | Channel sub-resource CRUD. |
| `POST /api/devices/{id}/channels/{channelId}/power` · `/setpoint` · `/mode` | Actuation, through the command ledger. |
| `POST …/{channelId}/trigger-scene` · `/play-media` | Actuation for scene and media channels. |
| `POST …/{channelId}/refresh-options` | Re-reads the channel's available options (HVAC modes, sources) from HA. |
| `POST /api/devices/{id}/backfill` | Backfills channel history from HA. |
| `GET /api/devices/{id}/history` · `GET …/{channelId}/history` | Stored measurement history. |
| `GET /api/discovery/unmapped` | HA devices not yet imported, with suggested grouping and channels. |

Every write to HA goes through `IClimateCommandService`, which ledgers it — so
each of the actuation endpoints above is recorded action by action with a
`Source` ([`climate-brain-architecture.md`](climate-brain-architecture.md) Phase 1).

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/commands` | `CommandDto[]` | The ledger, newest first. Query: `deviceId`, `channelId`, `source`, `limit` (100, capped at 500). |
| `GET /api/commands/overrides` | `ControlOverrideDto[]` | Detected manual overrides. Query: `activeOnly`, `limit`. |

## Routines

| Method & route | Purpose |
|---|---|
| `GET /api/routines` · `GET /api/routines/{id}` | Routines with their actions. |
| `POST /api/routines` · `PUT /api/routines/{id}` · `DELETE /api/routines/{id}` | CRUD. A routine's actions are embedded in the write request and replaced wholesale — the action list *is* the routine. |
| `POST /api/routines/{id}/trigger` | Runs the actions in `SortOrder`, stopping at the first that doesn't succeed. |
| `POST /api/routines/{id}/turn-off` | The inverse for a toggle routine: `SetPower false` to every `SetPower` action's channel. |

## Family calendar

Google Calendar, connected per-account by an admin, synced on a schedule, read
from Postgres by the dashboard ([`plans/kiosk.md`](plans/kiosk.md) track A).

The schedule is `SyncCalendarEvents`, every five minutes, caching only today
through today + `CalendarAgendaDays` — so nothing in the request path calls
Google, and an account that stops syncing ages off the wall instead of freezing
last week onto it.

The two OAuth endpoints are **navigated to, not fetched**: the admin page links
to `start`, Google redirects the browser to `callback`, and both outcomes end as
a 302 to `/apps/admin/calendars?connected=<email>` or `?error=<code>`.

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/calendar/oauth/start` | 302 to Google | 400 with an actionable message when `GoogleClientId`/`GoogleClientSecret` are unset. Records PKCE state in `OAuthStates` — a table, because the callback can land on a different replica. |
| `GET /api/calendar/oauth/callback` | 302 to the admin app | Query: `code`, `state`, `error`. Single-use state; upserts the account by (`Provider`, `AccountEmail`) and runs calendar discovery before redirecting. |
| `GET /api/calendar/accounts` | `CalendarAccountDto[]` | Connected accounts, each with its calendars, `NeedsReauth`, `LastSyncedAt`, `LastSyncError`. **Never carries token material** — no field on the DTO can. |
| `POST /api/calendar/accounts/{id}/refresh-calendars` | `CalendarDiscoveryDto` | Re-runs discovery. 502 when the provider could not be listed; the same message lands on the account's `LastSyncError`. |
| `PUT /api/calendar/calendars/{id}` | `CalendarDto` | `{ included, colorOverride, sortOrder }` — the admin-owned half. `colorOverride` must be a hex color; discovery never writes these three fields. |
| `DELETE /api/calendar/accounts/{id}` | 204 | Best-effort revoke with Google, then delete. Calendars and cached events cascade. A revoke failure is logged, not fatal. |
| `POST /api/calendar/sync` | `CalendarSyncDto` | Runs the event sync now instead of at the `SyncCalendarEvents` job's next firing, for the minute after a calendar is included. Always 200 — the service is fail-soft, and per-account reasons come back on the account rows. |

## Settings

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/settings` · `GET /api/settings/{key}` | `SiteSettingDto[]` | Secret-valued keys (`HomeAssistantToken`, `KioskWifiPassword`, `GoogleClientSecret`) come back redacted. |
| `PUT /api/settings/{key}` · `DELETE /api/settings/{key}` | `SiteSettingDto` | Secret-valued keys are obfuscated on write (`SecretObfuscator`). |

## Kiosk, apps, and logs

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/kiosk/provisioning-info` | `ProvisioningInfoDto` | Everything the admin app needs to build the Android QR provisioning payload — signing-cert checksum, APK URL, Wi-Fi credentials, timezone. The Wi-Fi password is deobfuscated here, by design. |
| `GET /api/app-version/{app}` | `AppVersionInfo` | Lets a long-lived frontend notice its bundle was superseded. The kiosk is why this exists: it loads once at boot and never navigates again. |
| `GET /api/docs` · `GET /api/docs/{**slug}` | `DocSummary[]` / `text/markdown` | Backs the docs browser app. Slugs are paths (`plans/swarm/design`). |
| `POST /api/ui-logs` | 204 | Client-side log events from the frontend apps, written through `ILogger` so they reach the same OpenSearch index as everything else. Same-origin, so no CORS handling. |
| `POST /api/vm-console-logs` | 204 | Hyper-V serial console lines from the VM log shipper. Server-to-server across the LAN, so gated on the `X-Vm-Log-Token` shared secret rather than same-origin trust. |

## Home Assistant (legacy ingestion)

Predates the device/channel model and the sampling job. Narrow it to ingestion,
or retire it — the dashboard reads none of it.

| Method & route | Purpose |
|---|---|
| `GET /api/homeassistant` | Stored environment readings. |
| `POST /api/homeassistant` | Pulls the last two hours of `climate.*` and `sensor.h5110` history from HA. |
| `GET /api/homeassistant/currentStates` | Live HA states for those two prefixes. |
