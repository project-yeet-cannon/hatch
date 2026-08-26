# Kiosk App Architecture

## Summary

`apps/kiosk` is an Android app (`family.landis.aeriekiosk`) that turns a tablet into a locked-down, always-on display for the dashboard at `https://kiosk.${DOMAIN}`. It runs as Android **Device Owner** in **lock task (kiosk) mode**, so there's no status bar, no recents/home escape route, and no way back to stock Android without a factory reset. It survives reboots and network drops on its own.

Three things had to come together to make this work end-to-end without ever plugging the tablet into a computer:

- **Rendering**: the tablets run Android 9 with a WebView stuck on Chrome 74 (2019), too old to parse the dashboard's bundle. The app embeds [GeckoView](https://github.com/mozilla/geckoview) instead — Firefox's engine, bundled and updated independently of the OS.
- **Distribution**: CI builds and signs the release APK and publishes it as a static file over the same reverse proxy every other app uses, rather than as a manual local build.
- **Provisioning**: Android's QR-code "no-touch" provisioning flow installs the app, sets Wi-Fi, and grants Device Owner in one scan at first boot — no `adb`/USB required, unless the tablet's setup wizard doesn't offer a QR scanner.

The shell is only half of it. [What the wall shows](#what-the-wall-shows) covers the two feeds that reach the tablet from outside the house — the family calendar and outdoor hazards — including the setup an operator does once and the display rules that keep a lit display from becoming a lamp at 3am. [Gather on the wall](#gather-on-the-wall) is the one thing here that takes input rather than only showing it, and [Text entry on the wall](#text-entry-on-the-wall) is what the tablets turned out to do with a soft keyboard. [Cameras on the wall](#cameras-on-the-wall) is the one thing that puts itself on screen without anyone asking.

## Runtime (`apps/kiosk/app`)

Three components, no ViewModel/state layer — the app is a single full-screen browser pinned to one origin:

- **`MainActivity`** ([MainActivity.kt](../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/MainActivity.kt)) creates a `GeckoView`/`GeckoSession` pointed at `DASHBOARD_URL` (`https://kiosk.landis.family/`) and:
  - Hides system bars on create and whenever the window regains focus.
  - If the app is Device Owner (`DevicePolicyManager.isDeviceOwnerApp`), calls `setLockTaskFeatures(..., LOCK_TASK_FEATURE_NONE)` and `startLockTask()`. `NONE` is deliberate — the API's default feature set still permits pulling down the notification shade and reaching recents, both escape routes this app exists to close.
  - `KioskNavigationDelegate` denies any navigation whose host isn't `DASHBOARD_HOST`, so a stray link or redirect can never take the session off-origin.
  - `KioskProgressDelegate` + `scheduleRetry()` implement reconnect: on load failure, show a "Reconnecting…" overlay and retry with exponential backoff (2s → 30s cap), reset to the initial delay on next successful load.
  - `loadDashboard()` (cold start and reconnect) loads with `LOAD_FLAGS_BYPASS_CACHE`, and a 12-hour timer reloads the session the same way. Both are backstops under the dashboard's own refresh logic — see [Refresh lifecycle](#refresh-lifecycle).
- **`KioskDeviceAdminReceiver`** — an otherwise-empty `DeviceAdminReceiver` subclass; it exists purely as the component name that `dpm set-device-owner` / QR provisioning targets. `device_admin.xml` requests `force-lock` and `disable-keyguard-features`, the minimum policy set Device Owner setup requires.
- **`BootCompletedReceiver`** — relaunches `MainActivity` on `ACTION_BOOT_COMPLETED`, so a reboot or power cycle comes back straight into the kiosk with no lock screen or manual relaunch.
- **`UpdateManager`** ([UpdateManager.kt](../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/UpdateManager.kt)) — started from `MainActivity.onCreate`, polls `https://files.${DOMAIN}/version.json` and compares `versionCode` against `BuildConfig.VERSION_CODE`. The poll interval tapers off from launch (tracked via `SystemClock.elapsedRealtime()`, immune to wall-clock/NTP jumps): every 60s for the first 20 minutes, every 5 minutes from 20–60 minutes, then every 6 hours after that — fast enough to pick up a build within a minute or two of a focused dev/deploy spurt, without polling a home server every minute forever. On a newer version it downloads `app-release.apk` and commits it through `PackageInstaller`. There's no Play Store on these tablets and no one on-site to tap "install" — silent commit only works because Device Owner apps are exempt from the install-confirmation UI (see [Android's DPC docs](https://developer.android.com/work/dpc/build-dpc#silent_install)). Android's own installer already refuses a commit whose signing cert doesn't match the installed app's, so the update isn't checksummed client-side beyond that. `InstallResultReceiver` just logs the commit outcome; `PackageReplacedReceiver` relaunches `MainActivity` on `ACTION_MY_PACKAGE_REPLACED`, since a silent update replaces the process without restarting it, mirroring what `BootCompletedReceiver` does for reboots.
- **`DisplayController`** ([DisplayController.kt](../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/DisplayController.kt)) — moves the tablet's *backlight* along the same circadian curve the dashboard's palette already travels. CSS cannot reach the backlight, so before this the near-black 3am page was lit by a full-brightness lamp; the panel and the pixels now dim together. Sun events come from `GET /api/sun-events` (cached in `SharedPreferences`, refreshed every 6h) and the curve is interpolated on-device by [`CircadianBrightness.kt`](../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/CircadianBrightness.kt), a deliberate port of the dashboard's [`circadianTheme.ts`](../src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.ts) — **the keyframe table is load-bearing on both sides and has to move together.** Both hold the same fourteen keyframes, named identically and anchored to the same sun events; the palette table in [`tokens.ts`](../src/Aerie.Web/apps/dashboard/src/theme/tokens.ts) is the source for the two brightness columns, so a panel level is chosen next to the colors it lights. Brightness is set on the activity's own window rather than through `Settings.System`, so no permission is needed and a crash cannot strand the tablet at an unreadable global brightness. Cached events are rolled forward onto the current day before use, because a day-old sunset otherwise reads as night and would dim the wall at noon. It also owns the **idle dim**: two minutes after the last touch the backlight drops to the timeline's second column (`idleBrightness`) sampled from the same phase function, whose night keyframes are a true `0.0` — `BRIGHTNESS_OVERRIDE_OFF`, which is the panel's *lowest* backlight and not a powered-off display, so the screen stays touch-responsive. **The dim ramps over four seconds and the restore snaps**: a slow fade down is a room going quiet, a slow fade up is a tablet that looks crashed. Presence reaches it two ways, both from `MainActivity` and neither needing a bridge into the page — `dispatchTouchEvent`, which sees every touch on its way to GeckoView, and an `ime()` inset listener (API 30+) that holds the display lit while the soft keyboard is up, since IME touches go to the IME's own window and a run of Gather entries would otherwise read as an empty room. Per-room schedules and a real screen-off are [docs/plans/kiosk_brightness.md](plans/kiosk_brightness.md).
- **`KioskLogger`** ([KioskLogger.kt](../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/KioskLogger.kt)) — a small fire-and-forget POST to `https://kiosk.${DOMAIN}/api/ui-logs`, the same endpoint the dashboard web app's `clientLogger.ts` posts to (`Controllers/UiLogsController.cs` / `Models/UiLogs/UiLogEntry.cs`), tagged `app: "kiosk-android"` so these lines land in the same `aerie-logs` OpenSearch index as everything else. Called from `MainActivity` (app start/stop), `BootCompletedReceiver`/`PackageReplacedReceiver` (relaunch after reboot/update), and `UpdateManager`/`InstallResultReceiver` (update check result, download/install progress and outcome).

`minSdk = 28` is a hard floor, not a compatibility choice — `DevicePolicyManager#setLockTaskFeatures` (used above) only exists from API 28 on.

## Refresh lifecycle

The tablet loads `https://kiosk.${DOMAIN}/` once at boot and never navigates again — that is what a kiosk *is*, and it's also why a deploy was invisible to the tablets while every other browser on the LAN picked it up immediately. Four layers now cover it, ordered from the one that does the work to the one that only ever runs if the others are broken.

**1. Cache headers ([`Program.cs`](../src/Aerie.Api/Program.cs), the `/apps` static file middleware).** With no explicit `Cache-Control`, `StaticFileMiddleware` sends only `ETag`/`Last-Modified`, which leaves a browser free to apply *heuristic* freshness and reuse `index.html` without revalidating — so a stale document kept naming a stale bundle even across a reload. Everything under `assets/` is content-hashed by Vite and is served `immutable`; the documents that reference those hashes are served `no-cache`, meaning "revalidate before reuse" (not `no-store`), so an unchanged `index.html` still costs only a 304. This is a prerequisite for everything below, not an optimisation.

**2. Build-drift reload ([`useKioskLifecycle.ts`](../src/Aerie.Web/apps/dashboard/src/hooks/useKioskLifecycle.ts) → [`AppVersionController`](../src/Aerie.Api/Controllers/AppVersionController.cs)).** The dashboard polls `GET /api/app-version/dashboard` every 5 minutes and reloads when the answer stops matching the build it is running.

The token on both sides is the app's content-hashed asset filenames, deduplicated, ordinally sorted, joined with `+` (`index-BXhsXAyC.css+index-Bt0yvdv6.js`). Three properties are the reason it's derived that way rather than stamped in by CI:

- It's *content*-derived, so a backend-only deploy — the common case — leaves it byte-identical and the kiosks don't reload for a change they can't see.
- The client reads its half out of its own DOM, from the `<script src>`/`<link href>` tags it was **actually loaded from**. A client that instead learned its own version by asking a replica could, mid-rolling-deploy across the three `api` replicas, be loaded from the old image but told the new version — and would then never reload at all.
- It's readable, so a drift shows up in OpenSearch as two filenames rather than two opaque hashes.

[`AppVersionService.ExtractAssetFileNames`](../src/Aerie.Api/Services/AppVersionService.cs) and `versionFromAssetUrls` in [`appVersion.ts`](../src/Aerie.Web/apps/dashboard/src/lib/appVersion.ts) must agree on every input **in both directions** — a filename one side keeps and the other drops reads as permanent drift, i.e. a tablet that reloads forever and never converges. Their test suites are deliberately written as parallel fixtures for that reason. A `sessionStorage` guard caps it at one reload per target build regardless, and logs an error instead of looping.

**3. Idle reset (same hook).** People scroll or open a zone card and walk off, leaving the display parked. 30s after the last touch, the zone cards remount (which is what returns each uncontrolled `<details open>` to `defaultOpen`) and the page scrolls to top. Deliberately *not* a remount of the whole app: that would drop the loaded snapshot and flash the skeleton, and would discard `RoutinesSection`'s optimistic toggle state, visually reverting a tap the user just made. A pending drift reload is deferred to this same idle moment, so the page never blanks mid-interaction.

The reset is the first rung of an **idle ladder** ([`kioskIdleTimings.ts`](../src/Aerie.Web/apps/dashboard/src/lib/kioskIdleTimings.ts)) whose two timeouts are only correct relative to each other: reset at 30s, backlight dim at 2 minutes. The second rung belongs to the native shell — CSS cannot reach a backlight — so it is mirrored as a Kotlin constant in `DisplayController`, the same accepted duplication as `circadianTheme.ts` / `CircadianBrightness.kt` and with the same rule: change one, change the other. Phase 3 of [kiosk_brightness.md](plans/kiosk_brightness.md) collapses both into a server-side per-kiosk profile and the mirroring goes away.

Zoom is the one piece of state JS cannot restore — Gecko exposes no API for the visual viewport's scale — so `index.html` sets `user-scalable=no, maximum-scale=1` and `theme.css` sets `touch-action: pan-y`, and the gesture never applies in the first place. `pan-y` rather than `manipulation` because nothing in the layout scrolls horizontally.

**4. Shell backstop ([`MainActivity`](../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/MainActivity.kt)).** `loadDashboard()` loads with `LOAD_FLAGS_BYPASS_CACHE` on cold start and on reconnect, which is what makes a power cycle — the only recovery action available to someone standing at the tablet — a reliable fix even on a tablet that cached `index.html` before layer 1 existed. A 12-hour `geckoSession.reload(LOAD_FLAGS_BYPASS_CACHE)` puts a ceiling on staleness for the case layer 2 cannot cover by construction: a bundle broken badly enough that its own lifecycle code never runs.

**Both layers suspend together while something on screen cannot survive them.** Layers 2 and 3 are actively hostile to a text field, and [Gather](#gather-on-the-wall) puts one on the wall (a [motion-opened camera feed](#cameras-on-the-wall) and an [open panel](#panels-on-the-wall) take the same hold, for the same reason): an idle reset is a *keyed* remount, so a half-typed item would vanish without a trace, and a drift reload would take the whole page. `hold()`/`release()` on [`kioskLifecycle.ts`](../src/Aerie.Web/apps/dashboard/src/lib/kioskLifecycle.ts) pauses the reset and the reload as one flag, deliberately — a reload that fires mid-entry is the same bug as a reset that does. `release()` then counts as a *touch* rather than as idleness: someone is standing right there, so closing the overlay re-arms the 30s timer and a deploy that drifted while held lands on that timer instead of firing immediately.

That state machine is a plain factory rather than the body of the hook for one reason: a seam whose entire value is "the timer really did not fire" has to be unit-tested rather than eyeballed. Keeping it out of a `useEffect` closure lets vitest drive it with fake timers directly ([`kioskLifecycle.test.ts`](../src/Aerie.Web/apps/dashboard/src/lib/kioskLifecycle.test.ts)) instead of the dashboard taking on jsdom and a renderer to assert on a `setTimeout`; [`useKioskLifecycle.ts`](../src/Aerie.Web/apps/dashboard/src/hooks/useKioskLifecycle.ts) is then only the wiring. The tests are mutation-checked — dropping either `held` guard fails them.

### Rolling out a lifecycle change to already-deployed tablets

Tablets running a build from *before* layer 2 shipped have no drift check, so they can't be told to reload — the update has to arrive through a layer they already run. Touching anything under `apps/kiosk/` makes CI build and publish a new APK (`detect-kiosk-changes` path-filters on exactly that), and `UpdateManager`'s steady 6-hour poll then installs it silently and `PackageReplacedReceiver` relaunches into a fresh page load. Zero-touch, bounded by that poll interval. Power-cycling a tablet does the same thing immediately.

## Text entry on the wall

Nothing on the kiosk asked for a keyboard until [Gather](#gather-on-the-wall), and three risks were assumed on its behalf: that lock task would suppress the IME, that a non-GMS budget tablet might not ship a usable one, and that GeckoView might not deliver characters or an enter key to the page. **One observation on the hardware retired all three** — redeeming an invite code at `/auth` inside the kiosk's GeckoView, 2026-08-21, raised an ordinary tablet soft keyboard occupying the bottom ~20% of the screen, split left/right for thumb reach, and the form's `onSubmit` fired. So: IMEs are exempt from the lock-task allowlist in practice under `LOCK_TASK_FEATURE_NONE` + `startLockTask()`, the tablets have a usable IME, and Gecko's IME path reaches the page with only a no-op `ContentDelegate` registered.

The wall tablets are mounted **portrait**, and everything on them is designed for that. Landscape is neither designed for nor tested.

**What that observation does not prove, and why it still matters.** The auth page is a vertically centred card, so its input sits at roughly half height — comfortably clear of a keyboard occupying the bottom fifth. The overlap case was never exercised, and `MainActivity` declares no `android:windowSoftInputMode` and runs immersive, so whether the viewport resizes for the IME at all is unknown.

That makes it a **layout constraint rather than an open question**: keep every input *and every button* in the top half of the screen, and the answer stops mattering. Portrait makes that cheap rather than cramped. A button under the keyboard is the same bug as an input under it, which is why Gather's **Clear checked** sits in the same top band as its add field. `useViewportInset()` hedges the resize question without depending on the answer — if the visual viewport shrinks, a scrolling list pads by that much; if it never does, the value stays 0 and nothing changes.

Field conventions for anything else that puts an input here, half inherited from the auth form and half learned from Gather's:

- **`autoCorrect="off"` and `spellCheck={false}`**, with `autoCapitalize="words"`. Autocorrect mangling a brand name is worse than a lowercase one.
- **Never `disabled` on submit.** Disabling the field drops focus and takes the IME down with it, which turns a run of entries into a run of re-taps.
- **`enterKeyHint="send"`, not `"done"`.** `done` is the hint that tells an IME to dismiss itself after the action.
- **Typing counts as presence.** Soft keyboards do not reliably produce `keydown`, which is why `input` is in [`ACTIVITY_EVENTS`](../src/Aerie.Web/apps/dashboard/src/lib/kioskLifecycle.ts) itself rather than bolted on by the one component that has a text field — a display that resets while someone is typing is the failure this whole section exists to prevent. The native shell cannot see any of it: IME touches never reach the activity's `dispatchTouchEvent`, so `MainActivity` watches `ime()` inset visibility instead and holds the backlight lit for as long as the keyboard is up.

Three things are still unconfirmed on the hardware, all cheap to check the first time someone is standing at a tablet: that the list still scrolls with the keyboard up, that dismissing the keyboard leaves immersive mode intact (`hideSystemBars()` re-fires on `onWindowFocusChanged`), and that the split keyboard doesn't sit on top of anything that needs tapping.

## What the wall shows

`GET /api/dashboard` is a backend-for-frontend: one round trip returns zones, outside conditions, routines, cameras, panels, the calendar agenda, and outdoor hazards, and the page re-polls it every 60 seconds. Two of those seven come from outside the house — Google Calendar, and a weather/air-quality provider — and both follow the rule the climate sampling already followed: **jobs fetch, Postgres caches, the endpoint reads.** Nothing in a request path calls a third party.

That split is not an optimisation. A kiosk polls forever, from three API replicas, and a display that blanks because Google is slow is worse than one showing an agenda five minutes stale. A read path cannot fail on a dependency it never calls. The endpoint-level contract is in [dashboard-api-manifest.md](dashboard-api-manifest.md); what follows is why each half is shaped the way it is, and how it reads from across the room.

One rule spans both, and it is the one most easily lost in a restyle: **every color on this page is mixed into the current circadian token, never stated absolutely.** The dashboard travels a keyframed day — deep night, first light, sunrise, full daylight, golden hour, sunset, afterglow, night — generating every token from the light at that hour ([`circadianTheme.ts`](../src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.ts), [`tokens.ts`](../src/Aerie.Web/apps/dashboard/src/theme/tokens.ts)).

Two properties of that engine are worth knowing before restyling anything on top of it. **Contrast is an invariant rather than an intention**: ink, secondary text and every accent-as-text are pushed apart from the surface under them by a floor in OKLab lightness *after* interpolation, so no hour of any day can render text that cannot be read from across the room — the previous three-keyframe version tweened an inverted pair of palettes token by token and went to a flat gray for half an hour before sunset, which is what this replaced. And **the polarity inverts exactly twice a day**, at civil dawn and civil dusk, because no tween from dark-ink-on-light to light-ink-on-dark stays legible in the middle. Those two are covered by a ~1.6s dip to black (`useCircadianTheme.ts`, `.hf-veil`) rather than a cross-fade: a cut to dark and back reads as nightfall, where a dissolve between opposite palettes reads as a broken screen. A calendar color from Google, or a red that means "warning", is a *hue* — it is allowed to survive the night unchanged. Its *luminance* is not. Both components express this the same way, `color-mix(in srgb, <the color> N%, var(--card))`, which keeps the hue and lets the phase supply the brightness without either component knowing which phase it is in. A fixed red banner would otherwise be the brightest thing in the house at 3am.

## Family calendar

An admin connects any number of Google accounts; every calendar those accounts can see becomes individually toggleable; the included ones render as a today-and-tomorrow agenda. The window is `CalendarAgendaDays` (default 2), which is what keeps the panel a fixed, glanceable height rather than an inbox.

**The OAuth flow lives in Aerie**, not in Home Assistant. Aerie owns the client, stores the refresh tokens, and discovers the calendars, because the requirement was "any number of accounts, per-calendar toggles, all from the admin app" — routing through HA's integration would have moved "add an account" into HA's config-entry flow and left the toggles somewhere Aerie could not reach. The Google client ID and secret are operator-supplied `SiteSettings`, the same shape as `HomeAssistantToken` and `KioskWifiPassword`; nothing operator-specific enters the repo ([ethos.md](ethos.md)).

Three endpoints and a revoke are the whole of Google's API surface here, so [`GoogleOAuthService`](../src/Aerie.Api/Services/Calendar/GoogleOAuthService.cs) and [`GoogleCalendarClient`](../src/Aerie.Api/Services/Calendar/GoogleCalendarClient.cs) call them with a named `HttpClient` rather than taking `Google.Apis.Calendar.v3`. The SDK's value is its credential store, which would have needed a custom EF-backed `IDataStore` anyway — comparable code, much larger dependency tree.

Details that are load-bearing rather than incidental:

- **`access_type=offline` and `prompt=consent`.** Without the second, a re-consent returns no refresh token at all, and the failure surfaces weeks later as an account that cannot refresh.
- **PKCE state is a table, not a dictionary.** `OAuthStates` exists because three replicas sit behind one ingress: the callback can land on a different replica than the one that started the flow. Rows are single-use and swept opportunistically on the next `start`, which is less machinery than a job for the same effect.
- **The account email is read from the `id_token` without validating its signature.** The token came straight from Google's token endpoint over TLS in the same request — there is no untrusted hop to defend against. This is written in a comment at the call site too, because it is exactly the kind of thing a later reader "fixes".
- **The redirect URI is derived from the request** (`UseForwardedHeaders` is configured, so the scheme and host are the ones the browser saw), with `GoogleOAuthRedirectUri` as an exact-match override. The override exists because Google compares the URI byte-for-byte and a proxy can rewrite what the app believes its own host to be.
- **`Included` defaults to false.** Connecting an account must not dump a work calendar onto a kitchen wall; the admin opts each calendar in. Discovery re-runs freely because it never writes `Included`, `ColorOverride`, or `SortOrder` — those three are the admin's half of the row, and everything else is Google's.
- **`singleEvents=true` on the events call.** Without it the response is recurrence *rules*, not instances, and the expansion becomes Aerie's problem.
- **All-day events are date math, not timezone math.** Google sends `start.date`/`end.date` with an **exclusive** end. `LocalStartDate` comes from that date verbatim, `LocalEndDate` from the exclusive end minus a day, and only the derived UTC instants go through the site timezone. Resolving the local dates once at sync time — rather than at render, in the browser — is why an all-day event doesn't drift a day for half the year. It is covered by tests for the same reason.

[`SyncCalendarEvents`](../src/Aerie.Api/Jobs/SyncCalendarEvents.cs) runs every five minutes and caches only the agenda window, pruning anything that falls out of it, so an account that stops syncing ages off the wall instead of freezing last week onto it. Each account syncs inside its own try/catch: one broken account writes `LastSyncError` and does not stop the others, and an `invalid_grant` on refresh sets `NeedsReauth` and leaves the row in place so the admin page can offer **Reconnect** rather than have the account silently vanish.

**On the wall** ([`CalendarSection.tsx`](../src/Aerie.Web/apps/dashboard/src/components/CalendarSection.tsx)), the agenda sits below the zones and above the routines: the agenda is read and the routines are touched, so the reachable half of the screen stays the tappable one. Each event is a calendar block — a rounded card tinted with its calendar's color, a solid rail down its left edge, times in a narrow gutter beside it. Three things carry the time of day, all of them only on today:

1. **A now line** — an accent rule with a dot, between the last event that has started and the first that hasn't, falling to the bottom of the day once everything has. It uses `--warm` rather than a calendar's usual red, for the circadian reason above.
2. **The in-progress block takes a ring in its own calendar color**, so "what is happening right now" is findable without reading a single time.
3. **Anything already over is faded, not dropped.** Dropping makes the column jump as the day passes and erases the evidence that the morning was busy; fading keeps the day's shape while pushing it behind what is still ahead.

The agenda renders nothing at all when every day in the window is empty — an empty day is only worth saying when some other day isn't.

### Connecting an account

Setup is on the admin app's **Calendars** page, and the page says most of this next to the button. What an operator needs, once:

1. A Google Cloud project with the **Calendar API** enabled.
2. An OAuth client (Web application) whose authorized redirect URI is exactly `https://home.${DOMAIN}/api/calendar/oauth/callback`.
3. **Google client ID** and **Google client secret** set on the admin **Settings** page.
4. **The consent screen published to Production.** This is the one that bites: while the app sits in *Testing*, Google expires refresh tokens after **seven days**, which presents as the calendar silently going stale every week rather than as an error. Unverified is fine at family scale — Google allows it behind a warning screen, capped at 100 users.

Then **Connect a Google account** on the Calendars page, include the calendars that belong on the wall, and either wait five minutes or press **Sync events now**.

## Outdoor hazards

Weather watches, warnings and advisories, plus bad air, for today and tomorrow — surfaced only when something is actually active, which on most days is nothing.

Both halves sit behind a provider interface selected by a setting: `WeatherAlertProvider` (default `nws`) and `AirQualityProvider` (default `open-meteo`), either of which takes `none` to turn that half off. Both defaults are **keyless**, which is what suits a product that gets redeployed — an operator gets hazards with no account to open anywhere. The seam exists because [NWS is US-only](https://api.weather.gov): a non-US operator adds a class and flips a setting, with no schema, contract, or UI change. That is also why no NWS-shaped idea (`event`, `headline`, `messageType`) is allowed past [`NwsAlertProvider`](../src/Aerie.Api/Services/Hazards/NwsAlertProvider.cs) into the entity or the contract. A provider name nothing answers to disables that half with a logged warning rather than failing to boot — these settings are free text an admin types.

- **NWS requires a `User-Agent`** and returns 403 without one. It is built from `WeatherAlertContact` when the operator has set one, and falls back to an anonymous string plus a warning logged once, because NWS documents that anonymous traffic may be throttled.
- **Alerts are deactivated, not deleted**, when the provider stops returning them. An alert ends by disappearing from the feed, "what was the house warned about last night" is worth being able to answer, and the rows are tiny.
- **Air quality is stored as hourly samples, insert-only.** The unique index on (`Source`, `Timestamp`) makes re-fetching an hour already stored a no-op, and keeping the series means a chart later needs no second migration.
- **Two filters are applied on read rather than trusted to the sync**: rows are limited to the *currently configured* provider, so switching providers clears the wall on the next poll instead of stranding the old one's alerts active forever; and expiry is re-checked, so an alert ends on the minute it ends rather than at the next firing.
- **Stale is not clean.** A newest air quality sample older than three hours produces no alert rather than an optimistic one.

[`SyncOutdoorHazards`](../src/Aerie.Api/Jobs/SyncOutdoorHazards.cs) runs every fifteen minutes and covers both providers. One job, not two: their natural cadences differ, but a second job to save a keyless HTTP call every half hour is machinery for its own sake.

Air quality collapses into **at most one** alert, and only when the current reading or the coming day's peak reaches `AirQualityAlertThresholdAqi` (default `101`, the bottom of "Unhealthy for Sensitive Groups"). Below that, air quality is not news. Its title is the band name, its detail carries the number, and when a later hour is worse, the peak's timestamp rides along as `startsAt` — the client formats it, because the API returns quantities and the client derives labels.

Both halves are normalized onto one severity vocabulary — `Unknown` | `Minor` | `Moderate` | `Severe` | `Extreme` — so the kiosk styles severity once rather than per kind.

**On the wall** ([`AlertBanner.tsx`](../src/Aerie.Web/apps/dashboard/src/components/AlertBanner.tsx)), hazards sit at the very top of the column, above everything: a hazard is the one thing here that changes what you do on the way out the door. Severity is a **ladder of presence** — an Extreme alert takes a wider rail, a heavier tint and a larger title than an advisory — rather than a change of hue alone, because at across-the-room distance the difference between orange and amber does not survive the trip. None of the steps raise absolute brightness; every tint is still mixed into `--card`. The banner renders nothing when the list is empty, so a calm day costs the layout no space at all.

## Gather on the wall

Shared shopping lists, reachable from the kitchen and from a phone. The app itself — the data model, the merge semantics, the phone half — is [gather.md](gather.md); this is the kiosk-specific half of it.

**A tile that opens an overlay, not a route.** The dashboard is a single screen with no router, and its whole lifecycle is tuned to a display nobody types into. An overlay adds one suspension seam ([above](#refresh-lifecycle)) instead of making every existing lifecycle rule route-aware. The tile shows each list with what is still to get, sized to be read from the doorway — deciding to go look at the list is a decision you make across the room, and opening the overlay is what you do once you've made it. It renders nothing at all when there are no lists, so a household that hasn't made one gets no dead tile.

**The overlay carries its own, longer idle timeout — 90s.** Standing at the wall reading a list is a legitimate way to spend a minute, but the wall has to find its way home on its own: the timeout closes the overlay and hands control back to the normal 30s lifecycle. A tablet left parked on the grocery list is a regression in what the wall is for. It agrees with the dashboard on what counts as presence by importing the same `ACTIVITY_EVENTS`, plus `input` — two definitions of "someone is here" is a screen that stays alive under one and closes under the other.

**Clear checked is a second tap inside 4 seconds, not a dialog.** A destructive sweep on a wall anyone walks past needs a confirmation; a modal on a display with no keyboard focus model does not.

**Two list pickers, not one.** The dashboard tile is the usual way in. The overlay's own picker is reachable only via its back button, and only when there is more than one list to pick.

Touch targets are sized for a wall tablet read at arm's length rather than a phone at reading distance, and Gather is wired into the dashboard's mock and test data sources like everything else, so `?source=mock` and `?source=test` still render the whole screen.

## Cameras on the wall

A live camera feed, over the dashboard, either because something moved in front of a camera or because someone tapped its button. The whole path — the Home Assistant subscription, the motion stream, go2rtc, the relay — is [camera-devices-architecture.md](camera-devices-architecture.md); this is the kiosk-specific half.

**One modal, two ways in.** A camera that tripped and a camera someone asked for are the same screen; only what put it there differs, and the visible camera is **`manual ?? motion`** — someone standing at the tablet watching the driveway should not be shoved onto the back door because a branch moved. Motion resumes on close if it is still going.

**It follows the overlay idiom, not the admin app's.** `CameraFeedModal` is built on `GatherOverlay`'s shape — full-screen, one 56px touch target, mounted inside `.hf-page` so the circadian custom properties resolve. A centred dialog with a small ✕ is right for a desktop browser and wrong for a portrait tablet on a wall.

**Dismissal is per *event*, not per camera.** Closing has to stop being in effect when the motion ends, or the ✕ silently mutes that camera forever. The same ✕ does both halves — clears the manual selection *and* dismisses the shown device's motion event — because closing a hand-opened feed on a camera that is *also* in motion would otherwise leave the modal exactly where it was, a button that visibly does nothing.

**The camera buttons are their own row, directly below the routines.** Same tile geometry, shared through the routine tile's own selectors rather than two numbers that have to be kept equal; a separate row because tapping one does something categorically different from triggering a routine. An icon in a circle rather than a live thumbnail — a thumbnail wants a JPEG proxy and an RTSP connection per camera per refresh, against cameras that cap concurrent sub-stream clients in the single digits.

**Every enabled camera gets a button, configured or not**, and tapping an unconfigured one says so. A camera missing from the wall looks identical to one that was never imported, and nobody standing in the hall can tell those apart.

**Only a motion-opened feed holds the lifecycle.** A deploy reload firing while someone is watching who is at the door is the same bug as one firing mid-Gather-entry ([above](#refresh-lifecycle)). A hand-opened feed deliberately does *not* hold it, so the 30s idle reset closes it — and with it the relay's connection, and the camera's — rather than leaving the wall lit on a live stream all afternoon. Watching a feed is a hands-in-pockets activity, so if 30s proves short, the value to move to is `IDLE_DIM_AFTER_MS` (120s), the file's existing statement of "nobody is there".

**No player dependency.** The relay hands over fragmented MP4 and `MediaSource` takes it directly. That is also the one thing here still unproven on the hardware: the kiosk is a GeckoView, not Chrome, and `supportedCodecs()` reports the empty case as "this display can't play the camera feed" rather than failing obscurely — visible rather than silent, but it would mean rethinking the transport.

Cameras are wired into the dashboard's mock and test data sources like everything else, so `?source=mock` and `?source=test` still render the whole screen.

## Panels on the wall

A **Panel** is the tier above a routine: same tile geometry, same name/icon/color customization, but a tap opens a full-screen sub-UI holding several calls to action rather than firing one. The Climate panel is what it exists for — the air conditioner with on/off, ± 1°F and a setpoint readable across a room, plus the two fans. Its row sits between the camera buttons and the Gather tile, and renders nothing when no panel is included.

A panel holds two kinds of thing. A **Routine** item is an existing routine, behaving exactly as its own tile does. A **Control** is device-bound and typed — today `Switch` and `Thermostat`, each binding channels by *role* ([device-architecture.md](device-architecture.md)).

**A Control is not composed of Routines, and that is the load-bearing decision here.** A routine is a fixed-value, fire-and-forget bundle with no read path; a control has to report the current setpoint and mode and write a *parametric* value. Building one out of routines would need one routine per degree and still could not read anything back. So a control binds channels directly and shares routines' *write* path — [`ClimateCommandService`](../src/Aerie.Api/Services/ClimateControl/ClimateCommandService.cs), which ledgers intent, validates against the channel, checks overrides, dispatches and records the outcome — without sharing their storage. Every panel dispatch carries `CommandSource.Human` (a hand on the tablet *is* the override) and a reason naming both halves, `Panel 'Climate' — Air conditioner`, so the ledger says which surface a person touched rather than only that one did.

**The tile is on the dashboard snapshot; the state is not.** `GET /api/dashboard` is a 60s poll carrying every panel whether or not anyone opened one, which is both too stale for live control state and too expensive to gather unconditionally. So the snapshot carries `PanelSummary` — id, name, icon, color — and the overlay fetches `GET /api/panels/{id}/state` on open and every 5s while it stays open.

**Writes are item-scoped, not channel-scoped, on purpose.** The panel is the boundary: the kiosk can act on what an admin deliberately put on a panel and cannot address an arbitrary channel by id. That is a strictly smaller surface than `DevicesController`'s channel writes, which is what a wall tablet in a hallway should have. An item on another panel, or an item that isn't a control, is a 404 out of the same lookup rather than a rule each endpoint remembers.

**`POST …/power` takes `{ on: bool }` rather than toggling.** The wall's copy of the state can be a poll stale, and a toggle would then do the opposite of what the finger asked for. The absolute form leaves a stale client wrong about what it *displays*, never about what it *sends*.

**It follows the overlay idiom.** [`PanelOverlay`](../src/Aerie.Web/apps/dashboard/src/components/PanelOverlay.tsx) is built on [`GatherOverlay`](#gather-on-the-wall)'s shape — full-screen, one 56px close target, mounted inside `.hf-page` so the circadian custom properties resolve — with its own 60s idle close, agreeing with the dashboard on what counts as presence by importing the same `ACTIVITY_EVENTS`. Sixty rather than Gather's ninety: what it holds open is a 5s poll, not a list someone is standing there reading. An open panel holds the dashboard lifecycle ([above](#refresh-lifecycle)) for Gather's reason — a drift reload or an idle remount mid-interaction would take the overlay out from under a finger.

**Optimistic state has both halves, and each is there for its own failure.** [`usePanelState`](../src/Aerie.Web/apps/dashboard/src/hooks/usePanelState.ts) holds an override per item that clears the moment a snapshot agrees with it (`RoutinesSection`'s rule) and expires on its own after 30s if no snapshot ever does (`GatherOverlay`'s). Without the expiry, a command the device refused — or one Home Assistant accepted and never actuated — would leave the wall claiming a state the house is not in until someone closed the overlay.

**The settle timer lives in the hook, not in the thermostat card.** Hold-to-repeat and the coalesced write read as one mechanism and are two. `ThermostatControl` owns the repeat (400ms before the first, then every 150ms, on pointer capture with `lostpointercapture` as the release backstop) and calls `setSetpoint` on every step. The hook applies each step to its optimistic overlay *immediately* and debounces only the dispatch by 700ms — so the number on the wall never waits on a timer, the 5s poll cannot yank a mid-hold value backwards, and two thermostats on one panel settle independently. Holding "+" from 68 to 78 is one decision and lands as one ledgered `SetTemperature`, not ten. Closing the overlay mid-settle flushes the write rather than dropping it, which is what someone who taps ⊕ and walks away means.

**Both ends of the range are enforced twice, and the client half snaps the way the server does.** ⊖/⊕ step from the *minimum* by `stepF`, exactly as `PanelsController` does server-side, so every value the buttons can reach is one the server accepts unchanged — a client stepping from the *current* value would drift off the grid the moment a device reported something between steps. A ⊕ disabled at the maximum is also visible, where a silently refused tap is not.

**`isOn: null` draws as neither button selected, not as Off.** The state is null both for a control with no on/off bound and for one whose channel has never reported, and a confident "Off" for a device nobody has heard from is the expensive lie — it is the one that makes someone stop looking for the problem. The mock source carries a fan that has never reported precisely because a two-button switch is easy to get wrong there. (The flip side: the kiosk cannot currently tell a setpoint-only thermostat from a silent one, so its On/Off pill renders unconditionally and a tap on the former gets the server's reason in the card's error line. Distinguishing them is a DTO field away if such a device ever turns up in the house.)

**A routine inside a panel is the same code as a routine on the dashboard, split in two.** [`useRoutineTaps`](../src/Aerie.Web/apps/dashboard/src/hooks/useRoutineTaps.ts) owns what a tap does — the one-at-a-time in-flight guard, the optimistic toggle, the failure — and [`RoutineTile`](../src/Aerie.Web/apps/dashboard/src/components/RoutineTile.tsx) owns how it looks. A shared component alone would have forced the overlay to adopt the dashboard's tile *geometry* to get its *behavior*. The guard holds a list rather than one routine because a run of taps down a row is a person being impatient, not four decisions, and that had to survive the split.

Panels are wired into the dashboard's mock and test data sources like everything else, so `?source=mock` and `?source=test` still render the whole screen.

## Photos on the wall

The family photo library, cycling directly below the room cards — the first thing on the column that is there to be *looked at* rather than read. It renders nothing until somebody includes an album on the admin Photos page, so a house that never set Immich up has no hole in its dashboard. Design and reasoning for the module behind it are in [plans/immich.md](plans/immich.md#v2--immich-albums-on-the-kiosk); the endpoints are in [dashboard-api-manifest.md](dashboard-api-manifest.md#photos).

**It is a proxy, and that is the whole security design.** The tablet never learns where Immich is and never holds a key to it. It asks Aerie for a manifest of asset ids and then puts those ids into `<img src>` against `/api/photos/assets/{id}/image` — the same origin it is already signed in to, with the same cookie. So the photo frame needs no second auth, no second origin and no CORS story, and the library's API key stays a server-side secret. The API serves an id only if an album someone included holds it; without that allow-list the route is a hole through to every photo in the house for anything that reaches the kiosk's origin.

**This is the one wall feature that reaches a third party inside a request**, and it is deliberate. Immich owns the library and has its own database; copying it into Postgres to keep the read-path rule would mean a second copy of every photo's metadata and a job to keep it honest. `PhotoLibrary` caches the answer in memory instead — the Immich fetch behind a 15-minute TTL, the album selection re-read from Postgres every 10 seconds — and a failed rebuild keeps the last good deck. A frame showing quarter-hour-old photos is not a bug; a frame going black because Immich restarted is. `GET /api/dashboard`'s own rule is untouched: the carousel is its own data path, for the reason Gather's tile is, so a dashboard poll that fails does not take the photos down with it.

**Its own path, its own cadence.** The manifest is a shuffled sample of the selection, shuffled *server-side per request* so two tablets in two rooms are not on the same photo, and refetched every 30 minutes — much slower than the dashboard's minute, because refetching often would mean cutting away from a photo somebody was looking at to start a different shuffle.

**Contain, over a blurred copy of itself.** A family library is full of portraits, and `cover` would crop the heads off half of them. The leftover space is filled with a blurred, scaled copy of the same photo — the same `src`, so it is a cache hit rather than a second fetch — which keeps a fixed-height box on the column without cropping anybody and without the layout jumping every time a portrait follows a landscape.

**It names no color the circadian phase did not supply**, so the frame dims with the wall and the twice-daily veil passes over it like everything else. That is the rule from [What the wall shows](#what-the-wall-shows) applied to the one component with a strong argument for opting out — a photo frame is the thing most likely to be built as a bright rectangle, and a bright rectangle in a dark hallway at 3am is exactly what [kiosk_brightness.md](plans/kiosk_brightness.md) exists to prevent. The caption sits over its own gradient scrim rather than relying on a text color, because legible-on-a-beach and legible-in-a-dark-living-room is not a contrast a single ink token can hold.

**Two failure modes are load-bearing rather than incidental.** A hidden tab keeps no timer, so a tablet that slept for six hours does not wake and rush a thousand photos catching up. And a photo whose bytes never arrive — deleted in Immich since the deck was drawn, or fetched while the API was down — leaves the deck, failing at most once per deck: without that, the `onError` handler advancing to the next photo is a request loop running as fast as the browser can make requests.

Photos are wired into the dashboard's mock and test data sources like everything else. The mock draws its own photos as SVG data URIs in three aspect ratios, because a carousel only ever verified against 3:2 landscapes is not verified; the test source carries an image that will not load, because that is a state a wall carousel spends real time in.

## Build & signing

- A release keystore is generated once, locally, and kept **outside the repo** (`~/keys/aerie-kiosk-release.jks`). `apps/kiosk/local.properties` (gitignored) points Gradle at it via four properties (`RELEASE_STORE_FILE`, `RELEASE_STORE_PASSWORD`, `RELEASE_KEY_ALIAS`, `RELEASE_KEY_PASSWORD`), read in [`app/build.gradle.kts`](../apps/kiosk/app/build.gradle.kts) to conditionally define a `release` `signingConfig` — debug builds and CI checkouts without the keystore still work, they just produce an unsigned APK.
- The same four values live as GitHub repo secrets/variables (`KIOSK_RELEASE_KEYSTORE_BASE64`, `KIOSK_RELEASE_STORE_PASSWORD`, `KIOSK_RELEASE_KEY_ALIAS` (variable, not secret), `KIOSK_RELEASE_KEY_PASSWORD`), so CI can reconstruct `local.properties` from a base64-encoded keystore without the file ever touching the repo.
- `compileSdk` is pinned to 34 and GeckoView to `133.0.20241209150345` (Dec 2024) — see the comments in `build.gradle.kts` — because newer GeckoView releases bump transitive `androidx.core`/`media3` deps past what this project's AGP/compileSdk combination supports. Bump both together, not independently.
- ABI filters are limited to `armeabi-v7a`/`arm64-v8a` — the tablets are budget ARM devices, so bundling x86/x86_64 (as the bare `geckoview-omni` artifact does by default) is dead weight.

## CI/CD: build → sign → publish

Everything after "push a keystore" is automated in [`.github/workflows/publish.yml`](../.github/workflows/publish.yml):

1. **`detect-kiosk-changes`** — path-filters on `apps/kiosk` via `git diff` against the previous commit, so the (slow) Android build only runs when kiosk files actually changed.
2. **`build-and-push-kiosk-image`** (gated on the above):
   - Writes `local.properties` from the GitHub secrets, computes `versionCode`/`versionName` from `git rev-list --count HEAD`/`git rev-parse --short HEAD` (monotonic across commits to `main`, which is what `UpdateManager` on-device needs), and runs `./gradlew assembleRelease -PkioskVersionCode=... -PkioskVersionName=...` to produce a signed APK with that version baked into `BuildConfig`.
   - Writes those same two values to `apps/kiosk/version.json` (`{"versionCode": ..., "versionName": "..."}`), published alongside the APK for `UpdateManager` to poll.
   - Computes the **signing-cert checksum** — not a whole-APK hash — while the keystore is still on disk:
     ```
     keytool -export -alias "$RELEASE_KEY_ALIAS" -keystore "$KEYSTORE_PATH" -storepass "$RELEASE_STORE_PASSWORD" -rfc \
       | openssl x509 -outform DER \
       | openssl dgst -sha256 -binary | openssl base64 | tr '+/' '-_' | tr -d '=' \
       > apps/kiosk/signature-checksum.txt
     ```
     This is the value Android's QR provisioning flow uses (`PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM`) to verify the downloaded APK before installing it — it's stable across rebuilds since it hashes the signing cert, not the APK bytes.
   - Deletes the keystore from the runner (`if: always()`).
   - Builds a tiny `nginx:alpine` image ([`apps/kiosk/Dockerfile`](../apps/kiosk/Dockerfile)) that just serves the APK, the checksum file, and `version.json` as static assets, and pushes it to `ghcr.io/eouw0o83hf/aerie-kiosk-files`.
3. **Deploy**: the `files` Deployment ([`files-deployment.yaml`](../charts/aerie/templates/files-deployment.yaml)) runs that image and is exposed at `https://files.${DOMAIN}` by the `files` Ingress, the same way every other hostname is routed. No dedicated CD job is needed — Flux's image automation notices the new tag and commits it, the same as any other first-party image.

## Provisioning (QR, no cable)

Android's "no-touch" provisioning lets a QR code scanned during initial device setup join Wi-Fi, download and verify a DPC APK, install it, and set it as Device Owner in one step. That QR payload is generated **live** by the admin app rather than baked into a build artifact, so changing Wi-Fi credentials never requires a new release:

- **Wi-Fi credentials as `SiteSettings`**: `KioskWifiSsid` / `KioskWifiPassword` / `KioskWifiSecurityType` (`SiteSettingKeys` in [`DeviceMapping.cs`](../src/Aerie.Api/Ef/DeviceMapping.cs)) are edited on the admin Settings page like any other setting. `KioskWifiPassword` gets the same obfuscate-at-rest / redact-on-`GET` treatment as `HomeAssistantToken` (`SettingsController.cs`) — obfuscation, not encryption, sufficient since it's not exposed generically.
- **`GET /api/kiosk/provisioning-info`** ([`KioskProvisioningController.cs`](../src/Aerie.Api/Controllers/KioskProvisioningController.cs)) assembles everything the QR payload needs: the signing checksum (fetched from the `files` container over the internal `edge` Docker network via a named `KioskFiles` `HttpClient`, `Program.cs`), the APK URL, the fixed device-admin component name, and the Wi-Fi/timezone settings — with the password **deobfuscated** back to plaintext, since the QR payload needs the real value. It is one of the paths deliberately kept on the wall's allow-list ([`auth-architecture.md`](auth-architecture.md#the-allow-list-is-load-bearing)) so tablet provisioning stays friction-free: a tablet at first boot holds no grant, and nothing behind this endpoint is more sensitive than an APK URL and the Wi-Fi credentials the tablet is about to join with anyway.
- **Admin Provisioning page** ([`ProvisioningPage.tsx`](../src/Aerie.Web/apps/admin/src/pages/ProvisioningPage.tsx)) fetches that endpoint, and — if Wi-Fi is configured — builds the Android provisioning extras object and renders it as a QR code on-canvas via the pure-JS `qrcode` package (no network calls of its own). If `wifiSsid` is empty, it prompts to configure Settings first instead of rendering a broken QR. A "Copy JSON" button covers the manual/`adb` fallback path.
- The extras payload:
  ```json
  {
    "android.app.extra.PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME": "family.landis.aeriekiosk/.KioskDeviceAdminReceiver",
    "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION": "https://files.<DOMAIN>/app-release.apk",
    "android.app.extra.PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM": "<from signature-checksum.txt>",
    "android.app.extra.PROVISIONING_WIFI_SSID": "...",
    "android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE": "...",
    "android.app.extra.PROVISIONING_WIFI_PASSWORD": "...",
    "android.app.extra.PROVISIONING_LOCALE": "en_US",
    "android.app.extra.PROVISIONING_TIME_ZONE": "...",
    "android.app.extra.PROVISIONING_SKIP_ENCRYPTION": true,
    "android.app.extra.PROVISIONING_LEAVE_ALL_SYSTEM_APPS_ENABLED": true
  }
  ```
  The Wi-Fi security/password keys are omitted entirely (not sent blank) for an open network — Android's provisioning contract requires their absence, not an empty string.

See the README for the step-by-step device install process built on top of this.

## Manual fallback (USB/adb)

Some budget/non-GMS-certified tablets never offer a QR scanner during setup. In that case, before any account is added on the device:

```
adb devices
adb install -r app-release.apk
adb shell dpm set-device-owner family.landis.aeriekiosk/.KioskDeviceAdminReceiver
```

If `dpm` complains about existing accounts, factory reset and retry — Device Owner can only be granted on a device with zero accounts configured.

## Hard Device Factory Reset

The Lenovo Tab devices:
- Hold both volume buttons down
- Push and hold power button until Lenovo logo appears
- Keep volume buttons held down until recovery mode boots
