# aerie

aviary home citadel

Aerie is built to be redeployed, not just deployed — every operator-specific
value is a parameter, and no secret material is committed. See
[docs/ethos.md](docs/ethos.md) before adding configuration.

## Aerie.API

`make run` to run it.

[http://localhost:5197/swagger](http://localhost:5197/swagger) for swagger.

## Dependencies

- Docker
- dotnet 10 sdk
- HomeAssistant instance

## Secrets

Local secrets file: `src/Aerie.Api/.env.json`
Flat KVP JSON file.

Required values:

- `ha_host`: host name/IP of HomeAssistant 
- `ha_port`: HA API port (usually 8123)
- `ha_token`: HA API token

## Kiosk tablet install

See [docs/kiosk-architecture.md](docs/kiosk-architecture.md) for how the kiosk app, its CI build, and QR provisioning fit together. To put a fresh tablet into service:

1. On the admin app's Settings page, set **Kiosk Wi-Fi SSID** (and password/security type, if needed) if not already configured.
2. Factory reset the tablet (or start from unboxed). On the Welcome/language-select screen of setup — before signing in to anything — tap the same spot on the screen 6 times to launch the QR provisioning scanner. (Exact trigger screen varies slightly by OEM/Android version.)
3. On another device, open the admin app's Provisioning page (`https://home.${DOMAIN}/apps/admin/provisioning`) and scan the QR code it displays. The tablet joins Wi-Fi, downloads the APK, verifies it against the signing checksum, installs it, and sets it as Device Owner automatically.
4. If the setup wizard never offers a QR scanner (some budget/non-GMS tablets omit it), fall back to the manual USB/adb path in [docs/kiosk-architecture.md](docs/kiosk-architecture.md#manual-fallback-usbadb).
5. Launch the app once (from the tablet, or via `adb shell am start -n family.landis.aeriekiosk/.MainActivity` over wireless debugging). It should load the dashboard fullscreen and lock itself in — no status bar, no way to swipe to recents/home.
6. Verify lockdown (no path back to stock Android via edge swipes or power/volume holds), boot persistence (reboots straight into the kiosk, no lock screen), and reconnect behavior (toggling Wi-Fi off/on shows a "Reconnecting…" overlay and the dashboard reloads once connectivity returns).
