# Kiosk app: signing & provisioning

Outstanding steps to get `apps/kiosk` onto the tablet as a locked-down Device
Owner kiosk. See the "Build & signing" and "Deployment & Device Owner
provisioning" sections of the original plan for background.

## 1. Signing keystore (on your Mac)

- [x] Generate a local keystore (one-time; keep it **outside** the repo):
  ```
  keytool -genkeypair -v \
    -keystore ~/keys/aerie-kiosk-release.jks \
    -alias aerie-kiosk \
    -keyalg RSA -keysize 2048 -validity 10000
  ```
  Pick a keystore password and key password when prompted — save them
  somewhere durable (password manager), they're needed for every future
  signed build.

- [x] Point Gradle at it without committing secrets — add to
  `apps/kiosk/local.properties` (already gitignored):
  ```
  RELEASE_STORE_FILE=/Users/nathan/keys/aerie-kiosk-release.jks
  RELEASE_STORE_PASSWORD=<your keystore password>
  RELEASE_KEY_ALIAS=aerie-kiosk
  RELEASE_KEY_PASSWORD=<your key password>
  ```

- [x] Once the keystore exists, wire a `signingConfig` into
  `apps/kiosk/app/build.gradle.kts` that reads those four properties, so
  `./gradlew assembleRelease` produces an installable signed APK instead of
  the current unsigned one.

## 2. Build, sign, and publish in GitHub Actions

Building and signing now happens in CI instead of on your Mac, and the
result is published straight to your compose stack — see
`build-and-push-kiosk-image` in `.github/workflows/publish.yml`, the
`files` service in `compose.prod.yml`, and `apps/kiosk/Dockerfile`.

- [x] Add a `detect-kiosk-changes` + `build-and-push-kiosk-image` job to
  `.github/workflows/publish.yml` that builds a signed release APK with
  `./gradlew assembleRelease` (using a `local.properties` written from GitHub
  secrets/variables, not the committed keystore) and bakes it into a tiny `nginx:alpine`
  image (`apps/kiosk/Dockerfile`) pushed to
  `ghcr.io/eouw0o83hf/aerie-kiosk-files`.
- [x] Add a `files` service to `compose.prod.yml` running that image,
  exposed at `https://files.${DOMAIN}` via the existing Caddy labels — no
  new CD job needed, since `cd.yml`'s existing `docker compose pull && up -d`
  already picks up the new image on every deploy.
- [x] One-time setup: add three repo secrets so CI can sign the APK without
  the keystore ever touching the repo, plus one repo variable for the key
  alias (not a secret, since it's just a label and it's useful to be able to
  read it back later):
  ```
  KIOSK_RELEASE_KEYSTORE_BASE64   # secret — base64 -i ~/keys/aerie-kiosk-release.jks
  KIOSK_RELEASE_STORE_PASSWORD    # secret
  KIOSK_RELEASE_KEY_ALIAS         # variable — aerie-kiosk
  KIOSK_RELEASE_KEY_PASSWORD      # secret
  ```
  ```
  gh secret set KIOSK_RELEASE_KEYSTORE_BASE64 --repo <owner>/<repo> --body "$(base64 -i ~/keys/aerie-kiosk-release.jks)"
  gh secret set KIOSK_RELEASE_STORE_PASSWORD --repo <owner>/<repo>
  gh variable set KIOSK_RELEASE_KEY_ALIAS --repo <owner>/<repo> --body "aerie-kiosk"
  gh secret set KIOSK_RELEASE_KEY_PASSWORD --repo <owner>/<repo>
  ```
- [x] Push a change under `apps/kiosk` (or re-run the workflow manually) and
  confirm `Build and publish containers` → `build-and-push-kiosk-image`
  succeeds, then confirm the `Deploy` workflow run after it picks up the new
  `files` service.
- [x] Confirm `https://files.${DOMAIN}/app-release.apk` downloads the signed
  APK from your LAN.

## 3. Tablet provisioning (QR code, no cable)

Android supports scanning a QR code during initial setup that joins Wi-Fi,
downloads the DPC APK, verifies it, installs it, and sets it as Device Owner
— all without adb or USB.

This is automated inline with the rest of the CI/CD flow: the DPC
signing-cert checksum is computed by CI when the APK is signed, and the QR
code itself is generated on demand by a Provisioning page in the admin app,
reading live Wi-Fi settings instead of being baked into a build artifact.

- [x] **CI: compute & publish the signing-cert checksum.** In
  `build-and-push-kiosk-image` (`.github/workflows/publish.yml`), add a step
  after "Build signed release APK" and before "Remove keystore" (keystore
  must still be on disk) that runs:
  ```
  keytool -export -alias "$RELEASE_KEY_ALIAS" -keystore "$KEYSTORE_PATH" \
      -storepass "$RELEASE_STORE_PASSWORD" -rfc \
    | openssl x509 -outform DER \
    | openssl dgst -sha256 -binary | openssl base64 | tr '+/' '-_' | tr -d '=' \
    > apps/kiosk/signature-checksum.txt
  ```
  reusing the `KIOSK_RELEASE_KEY_ALIAS` / `KIOSK_RELEASE_STORE_PASSWORD`
  values already loaded as env in that job. Then add
  `COPY apps/kiosk/signature-checksum.txt /usr/share/nginx/html/signature-checksum.txt`
  to `apps/kiosk/Dockerfile`, next to the APK `COPY`. This publishes
  `PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM` (hash of the signing cert —
  stable across rebuilds, not a whole-APK hash).
  - Verify: push a no-op change under `apps/kiosk` (or dispatch the workflow
    manually), confirm the image builds/pushes, then after the next deploy
    `curl https://files.$DOMAIN/signature-checksum.txt` and compare it
    against a one-off local
    `keytool -export -alias aerie-kiosk -keystore ~/keys/aerie-kiosk-release.jks -rfc | openssl x509 -outform DER | openssl dgst -sha256 -binary | openssl base64 | tr '+/' '-_' | tr -d '='`
    run — they must match (same signing key).

- [ ] **Wi-Fi credentials as `SiteSettings`.** Add `KioskWifiSsid`,
  `KioskWifiPassword`, `KioskWifiSecurityType` to `SiteSettingKeys` in
  `src/Aerie.Api/Ef/DeviceMapping.cs`, next to the existing `HomeAssistant*`
  keys. In `src/Aerie.Api/Controllers/SettingsController.cs`, extend the
  obfuscate-on-write check (currently `key == SiteSettingKeys.HomeAssistantToken`)
  and the `Redact` helper to also cover `KioskWifiPassword`, so it's
  obfuscated at rest and redacted on generic `GET /api/settings` — identical
  treatment to `HomeAssistantToken`. In
  `src/Aerie.Web/apps/admin/src/pages/SettingsPage.tsx`, append three entries
  to the `FIELDS` array: SSID (text), Wi-Fi security type (text; help text
  noting `WPA`/`WEP`/blank for open), Wi-Fi password (`type: 'password'`,
  same "stored obfuscated, leave blank to keep current value" help text as
  the HA token). No other admin changes needed — the page is already generic
  over `FIELDS`.
  - Verify: `npm run build` in `apps/admin`; run the API locally, confirm
    the three new fields appear on the Settings page, save values, and
    confirm `GET /api/settings` redacts the password field like
    `HomeAssistantToken` does.

- [ ] **`Aerie.Api`: same-origin provisioning-info endpoint.** Add
  `- DOMAIN=${DOMAIN}` to the `api` service's `environment:` list in
  `compose.prod.yml` (the value is already supplied by `cd.yml` at deploy
  time; this hands it to the running process too, not just Caddy's label
  interpolation). In `Program.cs`, register a named HTTP client pointed at
  the `files` container over the internal `edge` Docker network (`api` and
  `files` are already on that network together, same as how `db` is reached
  by service name):
  ```csharp
  builder.Services.AddHttpClient("KioskFiles", c => c.BaseAddress = new Uri("http://files/"));
  ```
  Add `Controllers/KioskProvisioningController.cs` exposing
  `GET /api/kiosk/provisioning-info`, no auth needed (nothing here is more
  sensitive than what `HomeAssistantConnectionManager` already handles
  internally; mirrors `UiLogsController`'s same-origin, no-CORS precedent).
  Reads `KioskWifiSsid` / `KioskWifiSecurityType` / `KioskWifiPassword`
  (deobfuscated via `SecretObfuscator.Deobfuscate`, same pattern as
  `HomeAssistantConnectionManager.ApplyAsync`) and `TimeZone` directly off
  `AerieContext.SiteSettings`, and the checksum from
  `http://files/signature-checksum.txt`, returning:
  ```json
  {
    "signatureChecksum": "<from http://files/signature-checksum.txt>",
    "apkDownloadUrl": "https://files.<DOMAIN>/app-release.apk",
    "deviceAdminComponentName": "family.landis.aeriekiosk/.KioskDeviceAdminReceiver",
    "wifiSsid": "<KioskWifiSsid setting>",
    "wifiPassword": "<KioskWifiPassword setting, deobfuscated>",
    "wifiSecurityType": "<KioskWifiSecurityType setting>",
    "timeZone": "<TimeZone setting>"
  }
  ```
  `apkDownloadUrl` built from `IConfiguration["DOMAIN"]`; component name is a
  constant matching `applicationId`/`namespace` in
  `apps/kiosk/app/build.gradle.kts` and `KioskDeviceAdminReceiver.kt`. If
  `KioskWifiSsid` isn't set yet, return the fields as empty strings rather
  than erroring — the admin page (next item) handles that state.
  - Verify: run the API locally (or temporarily point the HttpClient at
    `https://files.$DOMAIN` for a manual check), save Wi-Fi settings from the
    previous item, hit `/api/kiosk/provisioning-info`, confirm the JSON
    shape, that the password comes back in plaintext (not the obfuscated DB
    value), and that the checksum matches the first item's output.

- [ ] **Admin app: Provisioning page.** Add `qrcode` (pure-JS, renders
  straight to a `<canvas>`, no network calls) as a dependency of
  `src/Aerie.Web/apps/admin`. Add `getKioskProvisioningInfo()` to
  `src/api/client.ts` (same `fetchJson` pattern as the other calls) and a
  matching type to `src/types.ts`. Add `src/pages/ProvisioningPage.tsx`,
  wired into `App.tsx`'s nav and `Routes` at `/provisioning`, following the
  existing pages' layout conventions. On mount, fetch the provisioning info;
  if `wifiSsid` comes back empty, show a prompt/link to configure it on
  Settings first instead of rendering a QR. Otherwise assemble the
  provisioning JSON object (same shape as the old hand-built
  `provisioning.json` above, e.g.
  `android.app.extra.PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME`,
  `..._SIGNATURE_CHECKSUM`, `..._WIFI_SSID`, etc. — see
  `docs/` or prior git history for the full extras list if needed) from the
  response and render it via
  `QRCode.toCanvas(canvasRef, JSON.stringify(payload), ...)` into an
  on-screen canvas, plus a small display of which SSID/APK it's encoding so
  it's obvious at a glance if Settings need updating first. Add a "Copy
  JSON" convenience button for the USB/adb fallback path.
  - Verify: `npm run dev` in `apps/admin`, open `/provisioning`, confirm it
    loads the Wi-Fi info saved in the previous item and renders a live QR;
    `npm run lint && npm run build` clean. UI/browser verification of the
    actual tablet scan is left to you, not automated here.

- [ ] **Retire this note.** Once the above is deployed and confirmed working
  end-to-end, delete this note — the checklist above fully replaces the old
  curl/openssl/`jq`/`qrencode` workflow it superseded.

- [ ] **Factory reset the tablet.** On the Welcome/language-select screen of
  setup (before signing in to anything), tap the same spot on the screen 6
  times to launch the QR provisioning scanner. (Exact trigger screen varies
  slightly by OEM/Android version.)
- [ ] Scan the QR shown on the admin Provisioning page
  (`home.$DOMAIN/apps/admin/provisioning`, display it on your Mac or phone).
  The tablet should join Wi-Fi, download the APK, verify the checksum,
  install it, and set it as Device Owner automatically.
- [ ] If the setup wizard never offers a QR scanner at all (some
  budget/non-GMS-certified tablets omit it), fall back to the manual
  USB/adb method:
  ```
  adb devices                                                  # confirm device visible
  adb install -r /tmp/app-release.apk
  adb shell dpm set-device-owner family.landis.aeriekiosk/.KioskDeviceAdminReceiver
  ```
  This must happen before any account is added — if `dpm` complains about
  existing accounts, factory reset again and retry.
- [ ] Launch the app once (from the tablet, or connect wireless adb —
  Settings → Developer options → Wireless debugging — and run
  `adb shell am start -n family.landis.aeriekiosk/.MainActivity`). It should
  load the dashboard fullscreen and lock itself in — no status bar, no way
  to swipe to recents/home.
- [ ] **Verify lockdown**: try swiping from every edge, holding the
  power/volume buttons, etc. — confirm there's no path back to stock
  Android.
- [ ] **Verify boot persistence**: reboot the tablet (`adb reboot` or power
  cycle) and confirm it comes back up straight into the kiosk with no lock
  screen.
- [ ] **Verify reconnect behavior**: toggle Wi-Fi off/on and confirm the
  "Reconnecting…" overlay appears and the dashboard reloads automatically
  once connectivity returns.
