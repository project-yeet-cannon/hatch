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

- [x] Build the signed release APK:
  ```
  cd apps/kiosk && ./gradlew assembleRelease
  ```
  Output: `apps/kiosk/app/build/outputs/apk/release/app-release.apk`

## 2. Tablet provisioning (physical device)

- [ ] **Factory reset the tablet.** During setup, when it asks to sign in,
  choose "Skip" — do **not** add a Google account. (Device Owner
  provisioning fails if any account exists; you'd have to reset again.)
- [ ] Enable Developer Options: Settings → About tablet → tap "Build number"
  7 times.
- [ ] Enable USB debugging: Settings → System → Developer options → USB
  debugging.
- [ ] Connect the tablet to your Mac (USB, or `adb connect <tablet-ip>` over
  Wi-Fi) and confirm it's visible:
  ```
  adb devices
  ```
  Accept the "Allow USB debugging" prompt that appears on the tablet screen.
- [ ] Install the signed APK:
  ```
  adb install -r apps/kiosk/app/build/outputs/apk/release/app-release.apk
  ```
- [ ] Set the app as Device Owner (must happen **before** any account is
  added, and before you've done anything else with the tablet post-reset):
  ```
  adb shell dpm set-device-owner family.landis.aeriekiosk/.KioskDeviceAdminReceiver
  ```
  It should print `Success: Device owner set...`. If it instead complains
  about existing accounts or users, you'll need to factory reset again and
  skip account setup more carefully.
- [ ] Launch the app once (from the tablet, or
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
