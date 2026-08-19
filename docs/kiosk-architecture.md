# Kiosk App Architecture

## Summary

`apps/kiosk` is an Android app (`family.landis.aeriekiosk`) that turns a tablet into a locked-down, always-on display for the dashboard at `https://kiosk.${DOMAIN}`. It runs as Android **Device Owner** in **lock task (kiosk) mode**, so there's no status bar, no recents/home escape route, and no way back to stock Android without a factory reset. It survives reboots and network drops on its own.

Three things had to come together to make this work end-to-end without ever plugging the tablet into a computer:

- **Rendering**: the tablets run Android 9 with a WebView stuck on Chrome 74 (2019), too old to parse the dashboard's bundle. The app embeds [GeckoView](https://github.com/mozilla/geckoview) instead — Firefox's engine, bundled and updated independently of the OS.
- **Distribution**: CI builds and signs the release APK and publishes it as a static file over the same reverse proxy every other app uses, rather than as a manual local build.
- **Provisioning**: Android's QR-code "no-touch" provisioning flow installs the app, sets Wi-Fi, and grants Device Owner in one scan at first boot — no `adb`/USB required, unless the tablet's setup wizard doesn't offer a QR scanner.

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

Zoom is the one piece of state JS cannot restore — Gecko exposes no API for the visual viewport's scale — so `index.html` sets `user-scalable=no, maximum-scale=1` and `theme.css` sets `touch-action: pan-y`, and the gesture never applies in the first place. `pan-y` rather than `manipulation` because nothing in the layout scrolls horizontally.

**4. Shell backstop ([`MainActivity`](../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/MainActivity.kt)).** `loadDashboard()` loads with `LOAD_FLAGS_BYPASS_CACHE` on cold start and on reconnect, which is what makes a power cycle — the only recovery action available to someone standing at the tablet — a reliable fix even on a tablet that cached `index.html` before layer 1 existed. A 12-hour `geckoSession.reload(LOAD_FLAGS_BYPASS_CACHE)` puts a ceiling on staleness for the case layer 2 cannot cover by construction: a bundle broken badly enough that its own lifecycle code never runs.

### Rolling out a lifecycle change to already-deployed tablets

Tablets running a build from *before* layer 2 shipped have no drift check, so they can't be told to reload — the update has to arrive through a layer they already run. Touching anything under `apps/kiosk/` makes CI build and publish a new APK (`detect-kiosk-changes` path-filters on exactly that), and `UpdateManager`'s steady 6-hour poll then installs it silently and `PackageReplacedReceiver` relaunches into a fresh page load. Zero-touch, bounded by that poll interval. Power-cycling a tablet does the same thing immediately.

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
3. **Deploy**: the `files` service in [`compose.prod.yml`](../compose.prod.yml) runs that image and is exposed at `https://files.${DOMAIN}` via the same Caddy-label convention as every other service (see [reverse-proxy-architecture.md](reverse-proxy-architecture.md)). No dedicated CD job is needed — `cd.yml`'s existing `docker compose pull && up -d` picks up the new image on every deploy, the same as any other service.

## Provisioning (QR, no cable)

Android's "no-touch" provisioning lets a QR code scanned during initial device setup join Wi-Fi, download and verify a DPC APK, install it, and set it as Device Owner in one step. That QR payload is generated **live** by the admin app rather than baked into a build artifact, so changing Wi-Fi credentials never requires a new release:

- **Wi-Fi credentials as `SiteSettings`**: `KioskWifiSsid` / `KioskWifiPassword` / `KioskWifiSecurityType` (`SiteSettingKeys` in [`DeviceMapping.cs`](../src/Aerie.Api/Ef/DeviceMapping.cs)) are edited on the admin Settings page like any other setting. `KioskWifiPassword` gets the same obfuscate-at-rest / redact-on-`GET` treatment as `HomeAssistantToken` (`SettingsController.cs`) — obfuscation, not encryption, sufficient since it's not exposed generically.
- **`GET /api/kiosk/provisioning-info`** ([`KioskProvisioningController.cs`](../src/Aerie.Api/Controllers/KioskProvisioningController.cs)) assembles everything the QR payload needs: the signing checksum (fetched from the `files` container over the internal `edge` Docker network via a named `KioskFiles` `HttpClient`, `Program.cs`), the APK URL, the fixed device-admin component name, and the Wi-Fi/timezone settings — with the password **deobfuscated** back to plaintext, since the QR payload needs the real value. No auth: same same-origin, no-CORS precedent as `UiLogsController`, and nothing here is more sensitive than what `HomeAssistantConnectionManager` already handles.
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
