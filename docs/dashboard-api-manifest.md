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
- **One gate, not per-endpoint permissions.** Every endpoint below sits behind
  the house wall ([`auth-architecture.md`](auth-architecture.md)): a request
  carries a device grant or it is refused, and nothing here checks anything finer
  than that. A refusal is a `302` to the sign-in shell for a document request and
  a bare `401` for a `fetch` — which is a response shape every caller has to
  handle, and for a while did not. Outside the wall are the allow-listed paths,
  each there for a stated reason — the health probes, `/media/*`,
  `/api/ui-logs`, `/api/vm-console-logs` (which carries its own shared-secret
  header, strictly stronger than a cookie), `/api/kiosk/provisioning-info`, and
  the sign-in endpoints themselves. The tailnet boundary is still the outer
  layer: the domain has no public DNS
  ([`reverse-proxy-architecture.md`](reverse-proxy-architecture.md),
  [`tailscale-vpn-architecture.md`](tailscale-vpn-architecture.md)).
- **Secret-valued settings are redacted on read**, wall or no wall.
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
| `GET /api/dashboard` | `DashboardData` | **Primary.** Composes zones + outside + routines + cameras + the calendar agenda + outdoor hazards. Query: `historyHours` (9), `forecastHours` (7), `bucketMinutes` (30). |
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

## Cameras and motion

A camera is a device with two things no other kind has: motion events as they
happen, and live video. Design and reasoning in
[`camera-devices-architecture.md`](camera-devices-architecture.md).

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/motion-events/stream` | `text/event-stream` | One frame per motion transition, `{"deviceId":…,"isActive":…}`. Absolute state, not a toggle — a repeat is permitted and clients must treat it as a no-op. On connect it replays whatever is already in motion, then heartbeats every 20s under `event: heartbeat` (which `EventSource` ignores by default). Per replica: each `api` pod holds its own Home Assistant subscription. |
| `GET /api/devices/{id:guid}/camera/stream` | WebSocket | A byte-transparent relay to go2rtc: a short JSON control exchange, then fragmented MP4 forever. **`400`** without an upgrade, **`404`** no enabled camera with a `CameraFeed` channel, **`409`** the camera has no address configured yet, **`502`** go2rtc refused or is unreachable. Registers the stream with go2rtc on the way past, so a restarted go2rtc self-heals on the next viewer. |
| `GET /api/devices/{id:guid}/camera-connection` | `CameraConnectionDto` | How to reach the camera. Answers for any device, configured or not — an unconfigured camera returns the defaults with no host, which is what the form needs to render itself. Never carries the password; `hasPassword` is the flag that keeps an empty box unambiguous. |
| `PUT /api/devices/{id:guid}/camera-connection` | `CameraConnectionDto` | Upsert. A **null** password leaves the stored one alone, an empty string clears it — a save that only changed the host must not silently drop the credential. Blank host means "use what Home Assistant reported", not the empty string. |
| `DELETE /api/devices/{id:guid}/camera-connection` | 204 | Forgets the connection, password included. |

`GET /api/dashboard` carries the cameras too, as `CameraSummary(Id, Name,
IsConfigured)` — the kiosk's button row is rendered off the snapshot it already
polls rather than a second fetch of every device in the house.

## Family calendar

Google Calendar, connected per-account by an admin, synced on a schedule, read
from Postgres by the dashboard. Design, token handling, and the Google Cloud
setup an operator needs are in
[`kiosk-architecture.md`](kiosk-architecture.md#family-calendar).

The schedule is `SyncCalendarEvents`, every five minutes, caching only today
through today + `CalendarAgendaDays` — so nothing in the request path calls
Google, and an account that stops syncing ages off the wall instead of freezing
last week onto it.

The agenda itself has no endpoint of its own: it rides on `GET /api/dashboard`
as `calendar`, one `CalendarDay` per local day from today through today +
`CalendarAgendaDays`. **Every day in that window is present even when it holds
no events** — the kiosk has to be able to say "nothing tomorrow" rather than
collapse the day, which would read the same as a day that never synced. A
multi-day event appears on each day it covers, all-day events sort ahead of
timed ones, and each event's `color` is the calendar's `colorOverride` when the
admin set one and the provider's color otherwise. Excluded calendars and
disabled accounts are filtered on read as well as at sync, so un-including a
calendar takes it off the wall on the next 60-second poll rather than at the
next prune.

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

## Outdoor hazards

Weather watches, warnings, and advisories plus air quality, fetched on a
schedule and read from Postgres by the dashboard
([`kiosk-architecture.md`](kiosk-architecture.md#outdoor-hazards)).

Both halves sit behind a provider interface selected by a setting —
`WeatherAlertProvider` (default `nws`, keyless and **US-only**) and
`AirQualityProvider` (default `open-meteo`, keyless and global), either of which
takes `none` to turn that half off. A name nothing answers to disables that half
with a logged warning rather than failing to boot, since these settings are free
text an admin types.

The schedule is `SyncOutdoorHazards`, every fifteen minutes, one job for both
providers. Alerts are upserted on (`Source`, `ProviderAlertId`) and
**deactivated rather than deleted** when the provider stops returning them — an
alert ends by disappearing from the feed, and the rows are small enough to keep.
Air quality is stored as hourly samples, insert-only: the unique index on
(`Source`, `Timestamp`) makes re-fetching an hour already stored a no-op.

The alert list rides on `GET /api/dashboard` as `alerts`, most severe first, and
is **empty on a calm, clean-air day** — which is what lets the kiosk banner
render nothing at all rather than reserve space. Both halves are normalized onto
one severity vocabulary (`Unknown` | `Minor` | `Moderate` | `Severe` |
`Extreme`), so the client styles severity once. Two filters are applied on read
rather than trusted to the sync: rows are limited to the *configured* provider,
so switching providers clears the wall on the next poll instead of stranding the
old one's alerts active forever; and expiry is re-checked, so an alert ends on
the minute it ends rather than at the next firing.

Air quality collapses into **at most one** alert, and only when the current
reading or the coming day's peak reaches `AirQualityAlertThresholdAqi` (default
`101`, the bottom of "Unhealthy for Sensitive Groups"). Its title is the band
name, its detail carries the number, and when a later hour is worse the peak's
timestamp is `startsAt` — the client formats it, per the rule at the top of this
file. A newest sample older than three hours produces nothing: stale air quality
is not the same as clean air.

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/alerts` | `HazardAlert[]` | The same list `GET /api/dashboard` carries, for the admin Settings page's "Active alerts" card. Read-only, DB-only, and empty on a calm day — which is a working configuration, not a broken one. |

## Gather

Shared shopping lists, read and written by both the family shell PWA and the
kiosk overlay. Design and reasoning in [`gather.md`](gather.md); the module
seam it sits behind is in
[`family-apps-architecture.md`](family-apps-architecture.md). Storage Helper's
surface is documented with the app instead, in
[`storage-helper.md`](storage-helper.md).

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/gather/lists` | `ListSummaryDto[]` | Name, icon, colour and **both** counts — open and checked. A lists screen draws from this alone. Alphabetical. |
| `POST /api/gather/lists` | `ListSummaryDto` | `201`. Icon is an emoji and colour is a palette *name* the client resolves; the server never interprets either. |
| `GET /api/gather/lists/{id:guid}` | `ListDto` | The list and its items in one response, in server-decided display order: unchecked oldest-first, then checked most-recently-checked first. Both clients poll this. |
| `PUT /api/gather/lists/{id:guid}` · `DELETE …` | `ListSummaryDto` / 204 | Delete cascades to the items — a list is an item's only address. |
| `POST /api/gather/lists/{id:guid}/items` | `ItemDto` | **An upsert on the normalized name**, not an insert: re-adding something already on the list brings it back *un-checked*, merging a quantity or note only when the request carries one. `200`, not `201` — on a re-add nothing was created. Enforced by a unique index on `(ListId, NameNormalized)`, so two devices adding "milk" at once produce one row. |
| `PUT /api/gather/lists/{id:guid}/items/{itemId:guid}` | `ItemDto` | Overwrites exactly what it is given, nulls included; that is how a wrong quantity comes off. Renaming onto a name already on the list is a `409`, never a silent merge. Never touches the checkbox. |
| `POST …/items/{itemId:guid}/check` · `…/uncheck` | `ItemDto` | Its own endpoint rather than a field on the edit, so the collision that actually happens — a phone checking things off in an aisle against a rename typed at the wall a second earlier — structurally cannot clobber text. |
| `DELETE …/items/{itemId:guid}` | 204 | Scoped by list id, so an item id from another list is a `404` rather than a cross-list edit. |
| `POST /api/gather/lists/{id:guid}/clear-checked` | `ClearCheckedResult` | Sweeps everything already in the cart and reports the count, so the client can say what it removed rather than diffing a re-fetch. |

Text lengths are validated rather than truncated — item name 120, quantity 32,
note 200, list name 60 — so over-long input is a `400` naming the field, not a
`500` out of the column.

## Settings

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/settings` · `GET /api/settings/{key}` | `SiteSettingDto[]` | Secret-valued keys (`HomeAssistantToken`, `KioskWifiPassword`, `GoogleClientSecret`) come back redacted. |
| `PUT /api/settings/{key}` · `DELETE /api/settings/{key}` | `SiteSettingDto` | Secret-valued keys are obfuscated on write (`SecretObfuscator`). |

## Auth

The wall's own endpoints, and the only ones in the app that mint a credential.
Design and reasoning in [`auth-architecture.md`](auth-architecture.md).

| Method & route | Returns | Notes |
|---|---|---|
| `GET /api/auth/verify` | 204 / 302 / 401 | Traefik's `forwardAuth` target, not called by any app. Reads the original request from `X-Forwarded-Method`/`-Proto`/`-Host`/`-Uri` and **fails closed** when they are absent — its own path is allow-listed, so falling back to it would answer 204 to everything. Echoes `X-Aerie-Grant` / `X-Aerie-Label`. Its 302 is the one `Location` in the app that must be absolute. |
| `POST /api/auth/redeem` | `AuthGrantDto` | Turns an invite code into a grant and sets the cookie. Rate-limited per client IP (per-process, so three replicas is three times the budget — the Warning log per refusal is the real detector). **Not gated by `Auth:Enabled`**, and cannot be: there would be no way to enroll the first device. |
| `GET /api/auth/me` | `AuthGrantDto` | Who this device is. Answers the same way with the wall down, which is why it resolves the grant itself rather than relying on the middleware. |
| `POST /api/auth/sign-out` | 204 | Deletes the grant and expires the cookie. |
| `GET /api/auth/grants` | `AuthGrantDto[]` | The admin app's Sessions list, newest first. Never carries token material. |
| `DELETE /api/auth/grants/{id:guid}` | 204 | Revocation is a row delete. Refuses to revoke the caller's own grant, so nobody revokes their way out of the room. |
| `POST /api/auth/invites` | `AuthInviteDto` | Mints an invite. The plaintext code exists exactly once, in this response — only its hash is stored. |

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
