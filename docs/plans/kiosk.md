# Kiosk upgrades — calendar and outdoor hazards

Two additions to what the kiosk dashboard shows, requested together because
they share one shape: data that Aerie does not own, fetched on a schedule,
cached in Postgres, and served through the existing `GET /api/dashboard`
backend-for-frontend.

1. **Family calendar** — Google Calendar. An admin connects any number of
   Google accounts; every calendar those accounts can see becomes individually
   toggleable, and the included ones render as a today/tomorrow agenda.
2. **Outdoor hazards** — weather advisories (watches/warnings/advisories) and
   bad air quality for today and tomorrow, surfaced only when something is
   actually active.

**How to use this file:** one phase per commit, each leaving the app working.
Every phase is written to be picked up from a cold context: it names the goal,
the files, the steps, and how to know it's done. The two tracks (A: calendar,
B: hazards) are independent — either can be worked first, and A6/B4 both touch
the same two files (`Models/Dashboard/DashboardData.cs`,
`apps/dashboard/src/types.ts`), so expect a trivial merge if they land close
together.

**Verification, everywhere:** `make build`, `make test-api`, and `make test-web`
for frontend work. Migrations are generated with `make ef-migration
migration=Name` and applied to the local `db` container with `make
ef-database-update`. Per repo convention, UI is not browser-tested here — build
and lint it and leave the clicking to a human.

## Status

- [x] **A1** — Calendar schema + settings keys
- [x] **A2** — Google OAuth connect flow + token refresh
- [x] **A3** — Calendar discovery + visibility API
- [x] **A4** — Admin Calendars page
- [x] **A5** — Event sync job
- [x] **A6** — Calendar on the dashboard contract + provisional kiosk UI
- [x] **B1** — Hazard schema + provider interfaces
- [x] **B2** — NWS weather alert provider
- [ ] **B3** — Open-Meteo air quality provider
- [ ] **B4** — Hazard sync job + dashboard contract + provisional kiosk UI
- [ ] **C1** — Design pass (placement and styling of both new elements)
- [ ] **C2** — Dissipate this plan into the permanent docs

## Decisions already made

| Decision | Choice | Why |
|---|---|---|
| Calendar auth | Full OAuth **inside Aerie** — Aerie owns the client, stores refresh tokens, discovers calendars | Matches the request literally: any number of accounts, per-calendar toggles, all from the admin app. Home Assistant's integration would have moved "add an account" into HA's config-entry flow. |
| Google client credentials | Operator-supplied, entered on the admin Settings page, stored in `SiteSettings` | Same shape as `HomeAssistantToken` / `KioskWifiPassword`. Nothing operator-specific enters the repo — see [`ethos.md`](../ethos.md). |
| Google API access | Hand-rolled `HttpClient` calls, **not** `Google.Apis.Calendar.v3` | Three endpoints (`token`, `calendarList`, `events`) plus revoke. The SDK's value is its credential store, which would need a custom EF-backed `IDataStore` anyway — comparable code, much larger dependency tree. |
| Weather alerts | `api.weather.gov` (NWS), keyless, US-only | No operator credential at all, which suits redeployability. Its US-only limit is why it sits behind an interface. |
| Air quality | Open-Meteo Air Quality API, keyless, global | US AQI + component pollutants with no key. Same reasoning. |
| Provider seam | `IWeatherAlertProvider` / `IAirQualityProvider`, selected by a `SiteSetting` | A non-US operator adds a class and flips a setting; no UI or contract change. |
| Agenda window | **Today + tomorrow**, one color per calendar | Matches the "day/next day" framing of the hazard half and keeps the panel a fixed, glanceable height. |
| Where the data lives | Core `AerieContext`, alongside `Routines` — not a `Modules/` schema | Modules are family apps with their own schema and lifecycle ([`Modules/README.md`](../../src/Aerie.Api/Modules/README.md)). This is dashboard data consumed by the dashboard BFF, which is core. |
| Kiosk layout | **Deferred to a design pass** — see C1 | The operator is placing these elements with Claude Design. A6 and B4 render provisional, minimally-styled components so the data path is verifiable on the tablet; C1 replaces the placement and styling. |

## Conventions that apply to every phase

- **Fail soft, like `WeatherService` already does.** Every outbound call is
  best-effort with an explicit timeout. A dead Google, a throttling NWS, or a
  missing setting degrades one section of the dashboard — it never throws out
  of `GET /api/dashboard`.
- **The dashboard endpoint reads Postgres only.** Nothing in the request path
  calls a third party. Jobs fetch, the DB caches, the BFF reads. This is the
  same split `SampleChannels` → `Measurements` → `ZoneService` already uses.
- **Multi-replica safe.** The API runs three replicas in the cluster
  ([`charts/aerie/`](../../charts/aerie/)). Nothing may live in process memory
  that a second replica needs — OAuth state goes in a table, not a static
  dictionary. Quartz clustering already guarantees one replica runs a given
  firing.
- **Contract parity.** `Models/Dashboard/DashboardData.cs` and
  `apps/dashboard/src/types.ts` are two halves of one contract; changing one
  without the other is the bug this note exists to prevent. The dashboard's
  **mock and test data sources** (`src/mock/mockDataSource.ts`,
  `src/mock/testDataSource.ts`) must also produce any new field, or
  `make test-web` fails on the type error.
- **`docs/dashboard-api-manifest.md`** documents the endpoints. Update it in
  the phase that changes them, not at the end.
- **No hardcoded domains.** Write `${DOMAIN}` or `<domain>` in docs, and derive
  URLs from configuration or the request in code.

## Accepted risks

Read these before A2 — they are decisions, not open questions.

1. **The API has no authentication.** Anyone who can reach `home.${DOMAIN}` —
   which means anyone on the LAN or the tailnet, since the domain has no public
   DNS at all ([`reverse-proxy-architecture.md`](../reverse-proxy-architecture.md),
   [`tailscale-vpn-architecture.md`](../tailscale-vpn-architecture.md)) — can
   start the OAuth flow, read every synced event, and delete a connected
   account. This is the existing posture (`SettingsController` hands out the
   HA connection settings, `KioskProvisioningController` hands out the Wi-Fi
   password in plaintext), so calendar data does not introduce the exposure —
   but it does raise what's behind it. Adding auth is out of scope here and
   worth its own plan.
2. **Refresh tokens get `SecretObfuscator`, not encryption.** XOR against a
   fixed key, same as `HomeAssistantToken`. Consistency with the existing store
   beats a one-off scheme. The upgrade path, if it's ever wanted, is ASP.NET
   DataProtection with a Postgres-backed key ring (see the DataProtection note
   in [`Program.cs`](../../src/Aerie.Api/Program.cs) — the key ring is
   deliberately ephemeral today, which is exactly what would have to change).
3. **Google consent-screen publishing status matters.** While the operator's
   OAuth app sits in *Testing*, Google expires refresh tokens after **7 days**,
   which presents as the calendar silently going stale every week. The app must
   be set to *In production*; unverified is fine at family scale (Google allows
   it with a warning screen, capped at 100 users). A4's admin page should say
   this next to the connect button, not just in this file.
4. **NWS is US-only.** A non-US operator gets no weather alerts until a
   provider for their country exists. The interface in B1 is the whole
   mitigation; do not let NWS-shaped assumptions (`event`, `headline`,
   `messageType`) leak past `NwsAlertProvider` into the entity or the contract.

---

# Track A — Family calendar

## Phase A1 — Calendar schema + settings keys

**Goal:** every table and setting the rest of track A writes to, in one
migration, with nothing calling Google yet.

**Steps**

1. New `src/Aerie.Api/Ef/Calendar.cs`:
   - `EfCalendarAccount` (`[Table("CalendarAccounts")]`) — `Id`, `Provider`
     (string, `"google"`, so a second provider doesn't need a migration),
     `AccountEmail` (unique with `Provider`), `DisplayName?`, `RefreshToken`
     (obfuscated), `AccessToken?` (obfuscated), `AccessTokenExpiresAt?`,
     `ConnectedAt`, `LastSyncedAt?`, `LastSyncError?`, `NeedsReauth` (bool,
     default false), `Enabled` (bool, default true).
   - `EfCalendar` (`[Table("Calendars")]`) — `Id`, `AccountId` + nav,
     `ProviderCalendarId`, `Name`, `ProviderColor?`, `ColorOverride?`,
     `Included` (bool, **default false**), `SortOrder`, `TimeZone?`,
     `IsPrimary`. Unique index on (`AccountId`, `ProviderCalendarId`).
   - `EfCalendarEvent` (`[Table("CalendarEvents")]`) — `Id`, `CalendarId` +
     nav, `ProviderEventId`, `Title`, `Location?`, `IsAllDay`, `StartsAt`,
     `EndsAt` (both `DateTimeOffset`, UTC instants), `LocalStartDate`,
     `LocalEndDate` (both `DateOnly`, resolved in the site timezone at sync
     time), `Status?`, `FetchedAt`. Unique index on (`CalendarId`,
     `ProviderEventId`).
   - `EfOAuthState` (`[Table("OAuthStates")]`) — `Id`, `State` (unique),
     `CodeVerifier`, `RedirectUri`, `CreatedAt`, `ExpiresAt`. A table rather
     than memory because three replicas share one ingress: the callback can
     land on a different replica than the one that started the flow.
2. Register all four in `Ef/AerieContext.cs` — `DbSet`s plus cascade
   relationships (`EfCalendar` → account, `EfCalendarEvent` → calendar, both
   `DeleteBehavior.Cascade`), following how `EfRoutineAction` is wired.
3. `Included` defaults to **false** deliberately: connecting an account must
   not dump a work calendar onto a kitchen wall display. The admin opts each
   one in.
4. New `SiteSettingKeys` in `Ef/DeviceMapping.cs`, each with an XML doc
   comment matching the file's style:
   - `GoogleClientId`, `GoogleClientSecret` (secret — add it to the obfuscate
     list *and* the redact list in `Controllers/SettingsController.cs`,
     alongside `HomeAssistantToken`/`KioskWifiPassword`).
   - `GoogleOAuthRedirectUri` — optional exact-match override. When blank, A2
     derives it from the incoming request. It exists because Google requires
     the redirect URI to match a registered value byte-for-byte, and a proxy
     can rewrite what the app thinks its own host is.
   - `CalendarAgendaDays` — int, default `2` (today + tomorrow).
5. Add `GoogleClientId`/`GoogleClientSecret`/`GoogleOAuthRedirectUri`/
   `CalendarAgendaDays` to `SiteSettingsSnapshot` and `SiteSettingsService`,
   using the existing `Parse*`/`NullIfEmpty` helpers. **Deobfuscate
   `GoogleClientSecret` in the snapshot** (it's the read side; the value has to
   be usable), the way `KioskProvisioningController` deobfuscates the Wi-Fi
   password.
6. `make ef-migration migration=AddFamilyCalendar` and `make
   ef-database-update`.

**Tests:** `SiteSettingsServiceTests`-style coverage that `CalendarAgendaDays`
falls back to 2 and that a set `GoogleClientSecret` round-trips through
obfuscation.

**Done when:** the migration applies cleanly to the local `db` container, and
`GET /api/settings` lists the new keys with `GoogleClientSecret` redacted.

---

## Phase A2 — Google OAuth connect flow + token refresh

**Goal:** an admin can complete Google's consent flow and Aerie ends up holding
a usable refresh token for that account.

**Steps**

1. Register a named `HttpClient` in `Program.cs`:
   `builder.Services.AddHttpClient("Google")` — no `BaseAddress`; the service
   uses absolute URLs, since OAuth and the Calendar API live on different
   hosts.
2. `Services/Calendar/GoogleOAuthService.cs`:
   - `BuildAuthorizationUrl(clientId, redirectUri, state, codeChallenge)` →
     `https://accounts.google.com/o/oauth2/v2/auth` with
     `response_type=code`, `scope=openid email
     https://www.googleapis.com/auth/calendar.readonly`,
     `access_type=offline`, `prompt=consent` (without it, a re-consent returns
     no refresh token), `code_challenge_method=S256`.
   - `ExchangeCodeAsync(code, codeVerifier, redirectUri, ct)` → form POST to
     `https://oauth2.googleapis.com/token`, returns access token, refresh
     token, `expires_in`, `id_token`.
   - `RefreshAsync(refreshToken, ct)` → same endpoint,
     `grant_type=refresh_token`.
   - `RevokeAsync(token, ct)` → `https://oauth2.googleapis.com/revoke`.
   - `EmailFromIdToken(idToken)` — base64url-decode the middle JWT segment and
     read `email`. No signature validation: the token came straight from
     Google's token endpoint over TLS in the same request, so there's no
     untrusted hop to defend against. Say that in a comment, or the next reader
     will "fix" it.
3. `Services/Calendar/GoogleTokenProvider.cs` —
   `Task<string?> GetAccessTokenAsync(Guid accountId, CancellationToken ct)`:
   returns the cached access token when it's more than 60 seconds from expiry,
   otherwise refreshes, persists the new access token + expiry (obfuscated),
   and returns it. On `invalid_grant`, set `NeedsReauth = true` and
   `LastSyncError`, and return null — the account stays in the list so the
   admin page can offer "Reconnect" instead of silently vanishing.
4. `Controllers/CalendarOAuthController.cs` (route `api/calendar/oauth`):
   - `GET start` — 400 with an actionable message if `GoogleClientId`/
     `GoogleClientSecret` are unset ("Set them on the admin Settings page").
     Otherwise generate `state` + PKCE verifier, insert an `EfOAuthState` row
     with a 10-minute expiry, and 302 to Google.
   - `GET callback?code&state&error` — look up and **delete** the state row
     (single use), reject an expired or missing one, exchange the code, resolve
     the email from the id token, upsert `EfCalendarAccount` by
     (`Provider`, `AccountEmail`), clear `NeedsReauth`, then run A3's discovery
     so the account arrives with its calendar list already populated. Finish
     with a 302 to `/apps/admin/calendars?connected=<email>`; on `error`,
     302 to `/apps/admin/calendars?error=<reason>`.
   - Redirect URI resolution: `GoogleOAuthRedirectUri` when set, else
     `$"{Request.Scheme}://{Request.Host}/api/calendar/oauth/callback"` —
     correct because `UseForwardedHeaders` is already configured in
     `Program.cs`.
   - Opportunistically delete `EfOAuthState` rows past their expiry on each
     `start`; a sweeper job for this would be more machinery than the problem.

**Tests:** `GoogleOAuthServiceTests` — authorization URL contains
`access_type=offline`, `prompt=consent`, and the S256 challenge;
`EmailFromIdToken` parses a hand-built unsigned JWT and returns null on
garbage. `GoogleTokenProviderTests` with a stubbed `HttpMessageHandler` —
cached token reused inside the window, refreshed outside it, `invalid_grant`
sets `NeedsReauth`.

**Done when:** hitting `/api/calendar/oauth/start` from a browser on the LAN
completes Google's consent screen and lands back on the admin app with a row in
`CalendarAccounts` carrying a non-empty `RefreshToken`.

---

## Phase A3 — Calendar discovery + visibility API

**Goal:** the calendars behind each connected account are listed, and each one
can be toggled on or off.

**Steps**

1. `Services/Calendar/GoogleCalendarClient.cs`:
   - `ListCalendarsAsync(accountId, ct)` → `GET
     https://www.googleapis.com/calendar/v3/users/me/calendarList`, following
     `nextPageToken`. Map `id`, `summaryOverride ?? summary`,
     `backgroundColor`, `primary`, `timeZone`.
   - `ListEventsAsync(accountId, providerCalendarId, timeMin, timeMax, ct)` →
     `GET .../calendars/{urlEncodedId}/events` with `singleEvents=true`,
     `orderBy=startTime`, `maxResults=250`, RFC3339 `timeMin`/`timeMax`,
     paginated. `singleEvents=true` is what makes Google expand recurring
     events server-side — without it, the response is rules, not instances.
   - Both take the access token from `IGoogleTokenProvider` and return an empty
     result (logged, not thrown) when it's null.
2. `Services/Calendar/CalendarDiscoveryService.cs` —
   `SyncCalendarListAsync(accountId, ct)`: upsert by `ProviderCalendarId`
   (updating `Name`/`ProviderColor`/`TimeZone`/`IsPrimary`, **never**
   `Included`/`ColorOverride` — those are the admin's), and delete local rows
   whose calendar no longer appears.
3. Call `SyncCalendarListAsync` from `CalendarOAuthController.Callback`, where
   A2 left the comment marking the spot — A2 shipped without it because
   discovery didn't exist yet, so a freshly connected account currently arrives
   with an empty calendar list.
4. `Controllers/CalendarsController.cs` (route `api/calendar`), following
   `RoutinesController`'s DTO-and-write-request shape with records in
   `Models/Calendar/`:
   - `GET accounts` → accounts, each with its calendars, `NeedsReauth`,
     `LastSyncedAt`, `LastSyncError`. Never returns token material.
   - `POST accounts/{id}/refresh-calendars` → re-runs discovery.
   - `PUT calendars/{id}` → `{ included, colorOverride, sortOrder }`.
   - `DELETE accounts/{id}` → best-effort `RevokeAsync` on the refresh token,
     then delete the account (calendars and events cascade). A revoke failure
     is logged, not fatal: the operator asked for it gone locally.
   - `POST sync` — **deferred to A5**, which is where the service it calls is
     built. Adding the route here would only have shipped an endpoint with
     nothing behind it.
5. Update `docs/dashboard-api-manifest.md` — which was still the stale
   "nothing here is implemented yet" proposal it was written as, listing none
   of the endpoints added since (`/api/routines`, `/api/settings`,
   `/api/kiosk`…). A2 deliberately didn't append two OAuth routes to a document
   that wasn't tracking the surface. **Brought current rather than retired**:
   rewritten as a reference for the whole endpoint surface, with the design
   reasoning it used to carry pointed at `device-architecture.md` and
   `climate-brain-architecture.md`, which are where those decisions are
   maintained. A6, B4 and C2 update it in place from here.

**Tests:** `CalendarDiscoveryServiceTests` against the in-memory provider —
upsert preserves `Included` and `ColorOverride` across a re-sync, and a
disappeared calendar is removed.

**Done when:** `GET /api/calendar/accounts` returns the connected account with
its real calendar list, and a `PUT` flips `Included` and survives a
`refresh-calendars`.

---

## Phase A4 — Admin Calendars page

**Goal:** all of the above is reachable without curl.

**Steps**

1. `src/Aerie.Web/apps/admin/src/pages/CalendarsPage.tsx` — a "Connect a
   Google account" button (plain link to `/api/calendar/oauth/start`, since it
   is a full-page redirect, not a fetch), then one card per account listing its
   calendars with an include checkbox, a color swatch input bound to
   `colorOverride`, a "Refresh calendars" button, and a "Remove" button with a
   confirm. Show `LastSyncedAt` / `LastSyncError`, and a prominent "Reconnect"
   link when `needsReauth`.
2. Read `?connected=` / `?error=` off the query string on mount and show the
   result, then strip the param.
3. A short setup note in the page body, not just in this plan: the operator
   needs a Google Cloud project with an OAuth client whose redirect URI is
   `<this origin>/api/calendar/oauth/callback`, the Calendar API enabled, and
   the consent screen **published to Production** — in Testing, Google expires
   refresh tokens after 7 days.
4. Wire it up: types in `src/types.ts`, calls in `src/api/client.ts`, a
   `NavLink` and `Route` in `src/App.tsx` (between Routines and Devices).
5. Add `GoogleClientId`, `GoogleClientSecret` (`type: 'password'`),
   `GoogleOAuthRedirectUri`, and `CalendarAgendaDays` to `FIELDS` in
   `SettingsPage.tsx`.

**Done when:** `make test-web` passes and the full connect → toggle → remove
loop works from the admin app. (UI verification is the operator's, per repo
convention.)

---

## Phase A5 — Event sync job

**Goal:** included calendars' events for the agenda window are in Postgres and
stay fresh.

**Steps**

1. `Services/Calendar/CalendarSyncService.cs` — `SyncAsync(ct)`:
   - Resolve the window from the site timezone: `TimeZoneInfo` for
     `settings.TimeZone` (IANA ids work on Windows too on .NET 6+, which
     matters — the legacy production host is Windows, see
     [`delivery-architecture.md`](../delivery-architecture.md)), local
     midnight today through local midnight + `CalendarAgendaDays`, converted to
     UTC for `timeMin`/`timeMax`.
   - Per enabled account, per `Included` calendar: fetch, then upsert by
     (`CalendarId`, `ProviderEventId`) and delete rows for that calendar that
     the response no longer contains. Skip `status == "cancelled"`.
   - All-day events arrive as `start.date`/`end.date` (date-only, and `end` is
     **exclusive**). Store `IsAllDay = true`, `LocalStartDate` from the date
     itself with no timezone math, `LocalEndDate` as the exclusive end minus
     one day, and `StartsAt`/`EndsAt` as those local midnights converted to
     UTC. Timed events get `LocalStartDate` from converting `StartsAt` into the
     site timezone. Getting this wrong is how an all-day event lands on the
     wrong day for half the year — it deserves a test, not a careful read.
   - Wrap each account in its own try/catch: one broken account writes
     `LastSyncError` and does not stop the others. Clear the error and stamp
     `LastSyncedAt` on success.
2. `Jobs/SyncCalendarEvents.cs` — `IAerieJob`, `Interval` 5 minutes,
   delegating to the service. Register it in `Program.cs` next to
   `SampleChannels`/`ReconcileCommands`.
3. Prune events outside the window on each run, so an account that stops
   syncing doesn't leave last week on the wall.
4. `POST /api/calendar/sync` on A3's `CalendarsController` — runs `SyncAsync`
   immediately, so "I just toggled a calendar on" doesn't mean waiting for the
   next firing. Deferred from A3, where the service didn't exist yet.

**Tests:** `CalendarSyncServiceTests` — an all-day event on a DST-shifting day
lands on the right `LocalStartDate`; a multi-day all-day event spans the right
`LocalStartDate`..`LocalEndDate`; a removed event is deleted; a throwing
account leaves the other account's events intact.

**Done when:** with one calendar included, `CalendarEvents` fills within five
minutes and matches what Google Calendar shows for today and tomorrow.

---

## Phase A6 — Calendar on the dashboard contract + provisional kiosk UI

**Goal:** the kiosk renders the agenda. Placement and styling are C1's job —
this phase is about the data arriving correctly.

**Steps**

1. `Models/Dashboard/DashboardData.cs`:
   ```csharp
   public record CalendarEventSummary(
       Guid Id, string CalendarName, string? Color, string Title,
       string? Location, bool IsAllDay,
       DateTimeOffset StartsAt, DateTimeOffset EndsAt);

   public record CalendarDay(DateOnly Date, IReadOnlyList<CalendarEventSummary> Events);
   ```
   and add `IReadOnlyList<CalendarDay> Calendar` to `DashboardData`.
   `DateOnly` serializes as `"2026-08-18"` by default, which is what the client
   wants for grouping.
2. `Services/Calendar/CalendarAgendaService.cs` — DB-only read: events whose
   `LocalStartDate..LocalEndDate` intersects the window, grouped into one
   `CalendarDay` per date in the window (**including empty days**, so the UI
   renders "nothing tomorrow" rather than a collapsed section), all-day events
   first, then by `StartsAt`. `Color` resolves `ColorOverride ?? ProviderColor`.
3. `DashboardService` — add the agenda to the existing `Task.WhenAll` fan-out
   and to the returned record.
4. Mirror the records in `apps/dashboard/src/types.ts`, and add the field to
   **both** `mock/mockDataSource.ts` and `mock/testDataSource.ts` (the test
   source's all-X/all-9999 convention exists to expose hardcoded UI values —
   honor it).
5. `apps/dashboard/src/components/CalendarSection.tsx` — provisional: day
   heading, then per event a colored dot, a time (`formatShortTime` from
   `lib/format.ts`, or "All day"), and the title. Render it in `App.tsx` below
   the zones and above `RoutinesSection`, and mark the placement with a comment
   pointing at C1. Skip the section entirely when every day is empty.
6. Update `docs/dashboard-api-manifest.md`.

**Tests:** `CalendarAgendaServiceTests` — grouping puts an event on its local
date, empty days survive, all-day sorts first, `ColorOverride` wins over
`ProviderColor`.

**Done when:** `GET /api/dashboard` carries a populated `calendar` array and
the kiosk shows today's and tomorrow's events.

---

# Track B — Outdoor hazards

## Phase B1 — Hazard schema + provider interfaces

**Goal:** the storage and the seam, with no provider behind them yet.

**Steps**

1. New `src/Aerie.Api/Ef/Hazards.cs`:
   - `EfWeatherAlert` (`[Table("WeatherAlerts")]`) — `Id`, `Source` (provider
     name), `ProviderAlertId` (unique with `Source`), `Event` (e.g. "Winter
     Storm Warning"), `Headline?`, `Description?`, `Instruction?`, `Severity`
     (enum), `Onset?`, `Ends?`, `AreaDescription?`, `Active` (bool),
     `FetchedAt`.
   - `enum WeatherAlertSeverity { Unknown, Minor, Moderate, Severe, Extreme }`
     — append-only, per the enum convention in `DeviceChannelMetric`.
   - `EfAirQualitySample` (`[Table("AirQualitySamples")]`) — `Id`, `Source`,
     `Timestamp` (unique with `Source`), `UsAqi`, `Pm25?`, `Pm10?`, `Ozone?`,
     `No2?`, `FetchedAt`. Hourly rows, so a chart is possible later without a
     second migration.
2. Register both in `AerieContext`.
3. `Services/Hazards/IWeatherAlertProvider.cs` and `IAirQualityProvider.cs`:
   ```csharp
   public interface IWeatherAlertProvider
   {
       string Name { get; }
       Task<IReadOnlyList<WeatherAlertRecord>> GetActiveAlertsAsync(
           double latitude, double longitude, CancellationToken ct);
   }

   public interface IAirQualityProvider
   {
       string Name { get; }
       Task<AirQualityReading?> GetCurrentAsync(
           double latitude, double longitude, CancellationToken ct);
   }
   ```
   `WeatherAlertRecord` / `AirQualityReading` are provider-neutral records —
   no NWS field names, no Open-Meteo response shapes. That neutrality is the
   entire point of the seam (risk 4 above).
4. New `SiteSettingKeys` + snapshot members:
   - `WeatherAlertProvider` (default `"nws"`, `"none"` disables),
     `AirQualityProvider` (default `"open-meteo"`, `"none"` disables).
   - `WeatherAlertContact` — contact string for the outbound `User-Agent`; NWS
     asks for one and may throttle without it.
   - `AirQualityAlertThresholdAqi` — int, default `101` (the bottom of
     "Unhealthy for Sensitive Groups"). Below it, air quality is not news and
     shows nothing.
   - `HazardMaxSeverityAgeHours` — int, default `48`, bounding "today/next
     day".
5. Providers are resolved from `IEnumerable<IWeatherAlertProvider>` by `Name`
   against the setting; an unknown name logs a warning and disables the
   feature rather than throwing at startup.
6. `make ef-migration migration=AddOutdoorHazards`.

**Done when:** the migration applies and the new settings appear in
`GET /api/settings` with their defaults.

---

## Phase B2 — NWS weather alert provider

**Steps**

1. Register a named `HttpClient` `"WeatherAlerts"` in `Program.cs` with a
   10-second timeout.
2. `Services/Hazards/NwsAlertProvider.cs` (`Name => "nws"`):
   - `GET https://api.weather.gov/alerts/active?point={lat},{lon}`.
   - **`User-Agent` is mandatory** — NWS returns 403 without one. Build it as
     `Aerie/1.0 ({WeatherAlertContact})` when the setting is set; otherwise
     `Aerie/1.0 (self-hosted)` plus a warning logged once, since NWS documents
     that anonymous traffic may be throttled or blocked.
   - Parse the GeoJSON `features[].properties`: `id`, `event`, `headline`,
     `description`, `instruction`, `severity`, `onset`, `ends`/`expires`,
     `areaDesc`, `messageType`, `status`.
   - Keep only `status == "Actual"` and `messageType != "Cancel"`, dropping
     anything already expired or starting beyond `HazardMaxSeverityAgeHours`.
   - Map `severity` onto `WeatherAlertSeverity`, defaulting to `Unknown` for
     an unrecognized value rather than throwing — NWS adds vocabulary.

**Tests:** `NwsAlertProviderTests` with a stubbed `HttpMessageHandler` over a
captured sample payload — a cancelled alert is dropped, an expired one is
dropped, severity maps, an unknown severity becomes `Unknown`, a 403 returns
empty rather than throwing.

**Done when:** with the site's real latitude/longitude, the provider returns
whatever `api.weather.gov` currently has for that point (an empty list on a
calm day is a pass — verify against the URL in a browser).

---

## Phase B3 — Open-Meteo air quality provider

**Steps**

1. `Services/Hazards/OpenMeteoAirQualityProvider.cs` (`Name => "open-meteo"`):
   - `GET https://air-quality-api.open-meteo.com/v1/air-quality` with
     `latitude`, `longitude`, `current=us_aqi,pm2_5,pm10,ozone,nitrogen_dioxide`,
     `hourly=us_aqi`, `forecast_days=2`, `timezone=UTC`.
   - Return the current reading plus the **peak `us_aqi` over the next 24
     hours** and when it occurs — that peak is what makes this cover "the
     day/next day" rather than only this instant.
   - Also return the hourly series so B4 can persist it.
2. A pure `AirQualityBands` helper mapping US AQI to its band name — 0–50
   Good, 51–100 Moderate, 101–150 Unhealthy for Sensitive Groups, 151–200
   Unhealthy, 201–300 Very Unhealthy, 301+ Hazardous. Pure and testable, and
   the display string comes from here rather than being spelled out in the UI.

**Tests:** `AirQualityBandsTests` on the boundaries (50/51, 100/101, 300/301);
`OpenMeteoAirQualityProviderTests` on a captured payload — current parses, the
24-hour peak is found, a malformed payload returns null.

**Done when:** the provider returns a plausible current AQI for the site's
coordinates.

---

## Phase B4 — Hazard sync job + dashboard contract + provisional kiosk UI

**Goal:** hazards reach the kiosk. Placement and styling stay C1's job.

**Steps**

1. `Jobs/SyncOutdoorHazards.cs` — `IAerieJob`, `Interval` 15 minutes, one job
   for both providers (their natural cadences differ, but a second job to save
   a keyless HTTP call every half hour is machinery for its own sake):
   - Upsert alerts by (`Source`, `ProviderAlertId`); set `Active = false` on
     stored rows the provider no longer returns, rather than deleting them —
     "what was the house warned about last night" is worth keeping, and the
     rows are tiny.
   - Insert air quality samples; the unique index on (`Source`, `Timestamp`)
     makes re-fetching the same hour a no-op. Follow
     `ChannelHistoryWriter`'s handling of expected unique-key violations.
   - Each provider in its own try/catch; a provider set to `"none"` is skipped
     entirely.
2. `Services/Hazards/HazardService.cs` — DB-only read producing a
   provider-neutral list:
   ```csharp
   public enum HazardKind { Weather, AirQuality }

   public record HazardAlert(
       string Id, HazardKind Kind, string Severity, string Title,
       string? Detail, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);
   ```
   - Active weather alerts within the window become one `HazardAlert` each,
     most severe first.
   - Air quality becomes **at most one** synthetic alert, and only when the
     current or next-24h peak AQI is at or above `AirQualityAlertThresholdAqi`
     — title from the band ("Unhealthy for Sensitive Groups"), detail carrying
     the number and, when the peak is later, when it arrives.
3. Add `IReadOnlyList<HazardAlert> Alerts` to `DashboardData`, fan it into
   `DashboardService`, mirror it in `types.ts`, and add it to both mock data
   sources.
4. `apps/dashboard/src/components/AlertBanner.tsx` — provisional: renders
   nothing when the list is empty (the normal day, and the reason this can sit
   anywhere for now), otherwise one line per alert with a severity-derived
   color. Render it at the top of `App.tsx` with a comment pointing at C1.
5. `GET /api/alerts` — read-only, returns the same `HazardAlert` list. Add an
   "Active alerts" card to the bottom of the admin `SettingsPage`, under the
   new hazard settings, so an operator can confirm their latitude/longitude and
   provider choice actually produced something. It is config verification, which
   is why it belongs next to the config rather than on a page of its own.
6. Update `docs/dashboard-api-manifest.md` and add the hazard settings to
   `SettingsPage`'s `FIELDS`.

**Tests:** `HazardServiceTests` — below-threshold AQI produces no alert;
at-threshold produces one; a future peak is described as future; inactive and
expired weather alerts are excluded; ordering is most-severe-first.

**Done when:** `GET /api/dashboard` carries an `alerts` array that is empty on
a calm, clean-air day and populated when `api.weather.gov` has something for
the site's point.

---

# Track C — Finish

## Phase C1 — Design pass

**Not a code phase in the usual sense.** The operator is placing both elements
with Claude Design; A6 and B4 deliberately ship provisional, minimally-styled
components so the data is verifiable before any of this is decided.

What the design pass has to settle, and what implementing it will touch:

- Where the agenda and the alert banner sit in the kiosk's single centered
  column (`.hf-page` is `clamp(480px, 75vw, 960px)` — see
  `apps/dashboard/src/theme.css`), relative to the header, `.hf-zones`, and
  `.hf-routines`.
- Severity treatment: how an Extreme alert reads differently from a Minor one
  at across-the-room distance.
- All-day versus timed events; how a calendar's color is expressed (dot, bar,
  text); what "no events tomorrow" looks like.
- How both elements behave under the circadian theme
  (`lib/circadianTheme.ts`, `theme/tokens.ts`) — everything on this dashboard
  dims at night, and a red warning banner that ignores that will be the
  brightest thing in the house at 3am.
- New classes belong in `theme.css` alongside the existing `hf-*` family, not
  as inline styles.

**Done when:** `CalendarSection` and `AlertBanner` match the approved design,
the provisional-placement comments from A6/B4 are gone, and `make test-web`
passes.

---

## Phase C2 — Dissipate this plan

Per [`docs/plans/README.md`](README.md), a finished plan is deleted and what
survives moves into the permanent docs.

1. `docs/kiosk-architecture.md` — a "Calendar" section (the OAuth flow, where
   tokens live, what the sync job does, the Google Cloud setup an operator
   needs including the Production publishing requirement) and an "Outdoor
   hazards" section (the provider seam, the two default providers, the AQI
   threshold).
2. `docs/dashboard-api-manifest.md` — final state of the new endpoints and the
   two new `DashboardData` fields, if the per-phase updates left anything
   behind.
3. `README.md` — add the Google client id/secret to the settings an operator
   configures.
4. `docs/secrets-architecture.md` — the Google client secret and refresh tokens
   are `SiteSettings` rows, not SSM parameters. Say so explicitly under "What
   deliberately stays out", so the next reader doesn't go looking for a
   `/aerie/google/*` path that was never meant to exist.
5. Delete `docs/plans/kiosk.md` and its row from the plans README table.
