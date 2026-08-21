# aerie

aviary home citadel

Aerie is built to be redeployed, not just deployed — every operator-specific
value is a parameter, and no secret material is committed. See
[docs/ethos.md](docs/ethos.md) before adding configuration.

## Deployment

Two delivery paths, running in opposite directions: the legacy Windows host is
pushed to by GitHub Actions, and the k3s cluster pulls from this repo via Flux.
See [docs/delivery-architecture.md](docs/delivery-architecture.md) for what
happens when you push, what a push *doesn't* change, and how to watch a cluster
change land.

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

## Enrolling a device

Every app on `home.${DOMAIN}` and `kiosk.${DOMAIN}` sits behind one wall. A
device is enrolled once, by the operator, and never asks again — there are no
user accounts, no passwords, and nothing for a family member to remember. See
[docs/plans/auth.md](docs/plans/auth.md) for the design and what is deliberately
deferred.

To enroll a device:

1. On the admin app's **Sessions** page
   (`https://home.${DOMAIN}/apps/admin/sessions`), click **Generate invite**.
   Label it with the device, not the person — "kitchen tablet" is what you will
   be reading in the list a year from now when deciding what to revoke.
2. The invite is shown two ways, and either is enough: a QR code, and an
   eight-character code like `AERIE-K3M9-P2QT`. Scan it from a phone; type it on
   a tablet's soft keyboard. The code has no character you can mistake for
   another — no I, L, O or U — and `I`/`L` typed for `1` and `O` for `0` are
   folded on the way in.
3. On the device being enrolled, open `https://home.${DOMAIN}/auth` and enter
   the code. It is good for **15 minutes and one use**.
4. The device lands wherever it was heading, and stays signed in. The grant is
   long-lived and renews itself while the device is in use.

Revoke from the same page: delete the row. The device is refused on its next
request.

Three things stay reachable without a grant, on purpose:
`files.${DOMAIN}` (the kiosk APK and its checksum), the media library that Sonos
speakers fetch from directly, and the health endpoints Kubernetes probes. The
full list, and why each entry is on it, is in
[docs/plans/auth.md](docs/plans/auth.md#the-allow-list-is-load-bearing).

One consequence worth knowing before someone reports it as a bug: **a printed
storage-bin QR label scanned by a phone that isn't enrolled now lands on
sign-in** rather than on the bin. Redeeming a code carries the phone through to
the bin it scanned, so it is one extra step rather than a dead end — but a guest
holding a labeled bin can no longer scan it.

### If nobody can get in

The admin app that mints invites is itself behind the wall, so a house with no
enrolled device has no way back in through the front door. Two recoveries, in
order of preference:

- **The bootstrap invite.** On any deploy where no device is enrolled and no
  invite is live, the migration job mints one and logs it at Warning with a
  banner. It is valid for an hour and usable once. The Job is kept until the
  next deploy replaces it, so its log is still there afterwards:

  ```
  kubectl -n aerie logs job/api-migrate | grep -A4 'No enrolled devices'
  ```

  This is the only place a code is ever written to a log, and it is reachable
  only when the install has no way in at all.

- **The whole wall, off.** Set the `AUTH_MODE` repository variable to `none`,
  re-run Provision 4, and let Flux reconcile. That is the complete rollback —
  the Traefik middleware stops being rendered, the annotations come off `home`
  and `kiosk`, and the pod stops enforcing on its own — and it returns the
  deployment to exactly its pre-auth behavior. The three values are `none`,
  `canary` and `full`; `canary` is the rung between the other two, the wall in
  front of `/apps/docs` alone, useful for rehearsing a change to the gate
  against something nothing in the house depends on.

  They are deliberately not `off`/`on`. The value is substituted textually into
  a manifest that is then parsed as YAML 1.1, where `off` and `on` are booleans
  — no amount of quoting survives the `kustomize build` in between, so the
  vocabulary avoids the collision instead.

  A tablet that lost its cookie is a physical visit either way; `AUTH_MODE=off`
  gets the house back, not the tablet's enrollment.

## Kiosk tablet install

See [docs/kiosk-architecture.md](docs/kiosk-architecture.md) for how the kiosk app, its CI build, and QR provisioning fit together. To put a fresh tablet into service:

1. On the admin app's Settings page, set **Kiosk Wi-Fi SSID** (and password/security type, if needed) if not already configured.
2. Factory reset the tablet (or start from unboxed). On the Welcome/language-select screen of setup — before signing in to anything — tap the same spot on the screen 6 times to launch the QR provisioning scanner. (Exact trigger screen varies slightly by OEM/Android version.)
3. On another device, open the admin app's Provisioning page (`https://home.${DOMAIN}/apps/admin/provisioning`) and scan the QR code it displays. The tablet joins Wi-Fi, downloads the APK, verifies it against the signing checksum, installs it, and sets it as Device Owner automatically.
4. If the setup wizard never offers a QR scanner (some budget/non-GMS tablets omit it), fall back to the manual USB/adb path in [docs/kiosk-architecture.md](docs/kiosk-architecture.md#manual-fallback-usbadb).
5. Launch the app once (from the tablet, or via `adb shell am start -n family.landis.aeriekiosk/.MainActivity` over wireless debugging). It should load the dashboard fullscreen and lock itself in — no status bar, no way to swipe to recents/home.
6. Verify lockdown (no path back to stock Android via edge swipes or power/volume holds), boot persistence (reboots straight into the kiosk, no lock screen), and reconnect behavior (toggling Wi-Fi off/on shows a "Reconnecting…" overlay and the dashboard reloads once connectivity returns).
