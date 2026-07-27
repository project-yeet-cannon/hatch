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
- [ ] One-time setup: add three repo secrets so CI can sign the APK without
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
- [ ] Push a change under `apps/kiosk` (or re-run the workflow manually) and
  confirm `Build and publish containers` → `build-and-push-kiosk-image`
  succeeds, then confirm the `Deploy` workflow run after it picks up the new
  `files` service.
- [ ] Confirm `https://files.${DOMAIN}/app-release.apk` downloads the signed
  APK from your LAN.

## 3. Tablet provisioning (QR code, no cable)

Android supports scanning a QR code during initial setup that joins Wi-Fi,
downloads the DPC APK, verifies it, installs it, and sets it as Device Owner
— all without adb or USB.

- [ ] Compute the APK's checksum (SHA-256, base64url-encoded, no padding)
  from the copy the tablet will actually fetch:
  ```
  curl -fsSL https://files.${DOMAIN}/app-release.apk -o /tmp/app-release.apk
  openssl dgst -sha256 -binary /tmp/app-release.apk \
    | openssl base64 | tr '+/' '-_' | tr -d '='
  ```
- [ ] Build the provisioning JSON (fill in your checksum, Wi-Fi creds,
  timezone). Save as e.g. `provisioning.json` — **do not commit this file**,
  it contains your Wi-Fi password:
  ```json
  {
    "android.app.extra.PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME": "family.landis.aeriekiosk/.KioskDeviceAdminReceiver",
    "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_DOWNLOAD_LOCATION": "https://files.landis.family/app-release.apk",
    "android.app.extra.PROVISIONING_DEVICE_ADMIN_PACKAGE_CHECKSUM": "<checksum from above>",
    "android.app.extra.PROVISIONING_WIFI_SSID": "<your SSID>",
    "android.app.extra.PROVISIONING_WIFI_PASSWORD": "<your Wi-Fi password>",
    "android.app.extra.PROVISIONING_WIFI_SECURITY_TYPE": "WPA",
    "android.app.extra.PROVISIONING_LOCALE": "en_US",
    "android.app.extra.PROVISIONING_TIME_ZONE": "America/New_York",
    "android.app.extra.PROVISIONING_SKIP_ENCRYPTION": true,
    "android.app.extra.PROVISIONING_LEAVE_ALL_SYSTEM_APPS_ENABLED": true
  }
  ```
  `PROVISIONING_WIFI_SECURITY_TYPE` is `"WPA"` for WPA/WPA2-PSK networks,
  `"WEP"` for WEP, or omit the key entirely for an open network.

  If `PACKAGE_CHECKSUM` (hash of the whole APK file) gets rejected on-device,
  fall back to `android.app.extra.PROVISIONING_DEVICE_ADMIN_SIGNATURE_CHECKSUM`
  (hash of the signing cert instead — survives future rebuilds without
  changing):
  ```
  keytool -export -alias aerie-kiosk -keystore ~/keys/aerie-kiosk-release.jks -rfc \
    | openssl x509 -outform DER \
    | openssl dgst -sha256 -binary | openssl base64 | tr '+/' '-_' | tr -d '='
  ```
- [ ] Generate the QR code **locally** (never paste the JSON into a
  web-based QR generator — it contains your Wi-Fi password):
  ```
  brew install qrencode   # one-time
  jq -c . provisioning.json | qrencode -o kiosk-provision-qr.png -l L -s 10
  ```
- [ ] **Factory reset the tablet.** On the Welcome/language-select screen of
  setup (before signing in to anything), tap the same spot on the screen 6
  times to launch the QR provisioning scanner. (Exact trigger screen varies
  slightly by OEM/Android version.)
- [ ] Scan `kiosk-provision-qr.png` (display it on your Mac or phone). The
  tablet should join Wi-Fi, download the APK, verify the checksum, install
  it, and set it as Device Owner automatically.
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
