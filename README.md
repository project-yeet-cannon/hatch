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
[docs/auth-architecture.md](docs/auth-architecture.md) for the design and what
is deliberately deferred.

To enroll a device:

1. On the admin app's **Sessions** page
   (`https://home.${DOMAIN}/apps/admin/sessions`), click **Generate invite**.
   Label it with the device, not the person — "kitchen tablet" is what you will
   be reading in the list a year from now when deciding what to revoke. If the
   device belongs to somebody in particular, pick them from the dropdown beside
   the label; the link is then already in place when the row appears.
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

Whose device each one is can also be set from that page afterwards, from the
**Person** column. People themselves are managed on the admin app's **People**
page — a name, a photo, and an admin flag. A person is
not an account: there is still nothing to sign in as. What it buys you is a
human name on a session row and on every log line that device ships, which is
what makes "who opened the dashboard at 6am" a question with an answer. Deleting
a person never revokes their devices; they simply become unclaimed.

**One app depends on that link.** Quill, the notes app in the family shell
([docs/quill.md](docs/quill.md)), holds notes that belong to a person rather
than to the house — so it does not appear at all on a device that is not linked
to one. If somebody says the notes app is missing, that is where to look: set
the **Person** on their session, and it is there on the next launch. It is the
first place a person changes what an app shows, and it will not be the last —
sharing a note with somebody is the follow-on this points at.

Three things stay reachable without a grant, on purpose:
`files.${DOMAIN}` (the kiosk APK and its checksum), the media library that Sonos
speakers fetch from directly, and the health endpoints Kubernetes probes. The
full list, and why each entry is on it, is in
[docs/auth-architecture.md](docs/auth-architecture.md#the-allow-list-is-load-bearing).

One consequence worth knowing before someone reports it as a bug: **a printed
storage-bin QR label scanned by a phone that isn't enrolled now lands on
sign-in** rather than on the bin. Redeeming a code carries the phone through to
the bin it scanned, so it is one extra step rather than a dead end — but a guest
holding a labeled bin can no longer scan it.

### Making someone an administrator

Off by default, and it stays off until you ask for it. Set the `ADMIN_MODE`
repository variable to `enforced`, re-run Provision 4, and let Flux reconcile.
Once it is on:

- The **admin app 404s** on any device that is not linked to a person carrying
  the admin flag. Not a "forbidden" page — a plain 404, indistinguishable from
  an install built without it.
- The **operator verbs behind it** answer `403`. Roughly forty of them: creating
  and editing zones, devices, panels, routines, people and settings, minting and
  revoking sessions. Everything the family actually uses is untouched — turning
  lights on, nudging a thermostat, running a routine, the shopping lists, the
  notes, the storage bins, the dashboard.

**Do it in this order**, because the switch does not do it for you:

1. On the **People** page, tick **admin** for at least one person.
2. On the **Sessions** page, make sure that person is set as the **Person** on
   a device they actually hold.
3. *Then* set `ADMIN_MODE=enforced`.

Backwards, and the household is locked out of the admin app — including the
button that mints invites, so no new device can be enrolled either. It is
recoverable, but only by setting `ADMIN_MODE` back to `none` and reconciling.

The values are `none` and `enforced`, and deliberately not `off`/`on`, for the
same YAML-boolean reason given below for `AUTH_MODE`. It also does nothing at
all while `AUTH_MODE=none`: with no wall there is no identity on a request to
read, so enforcement stays dormant. `Test-AppTier.ps1` fails that combination
loudly rather than letting it look like it worked.

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

  A tablet that lost its cookie is a physical visit either way; `AUTH_MODE=none`
  gets the house back, not the tablet's enrollment.

- **The admin flag alone, off.** If everyone can reach the house but nobody can
  reach the admin app, this is the smaller version of the same problem: set
  `ADMIN_MODE` back to `none`, re-run Provision 4, reconcile. Every device stays
  enrolled and the wall stays up. Reach for this before turning the wall off.

## Tracking work

**Hatch** is the house project tracker, at `hatch.${DOMAIN}`: a kanban board,
issues with keys like `AER-12`, columns you name yourself, and an audit trail
under all of it. It is the operator's, not the household's — the bundle 404s and
its API 403s for anyone who is not an administrator — so it is only reachable
once the admin flag above is set on somebody's device. The design is in
[docs/hatch.md](docs/hatch.md).

The board is usable the moment the app is: five columns and six agent playbooks
are seeded by the migrations, so there is nothing to fill in before filing the
first issue. Two things are worth doing once:

1. On the **Projects** page, create a project. The key is the prefix on every
   issue in it (`AER`, `OPS`) and it is what you will type into a chat window,
   so keep it short.
2. If Claude is going to work tickets, mint a key on the admin app's **API
   keys** page, scoped to `hatch`, and give it to
   [`scripts/hatch.sh`](scripts/hatch.sh):

   ```
   ./scripts/hatch.sh config      # asks for the origin and the key
   ./scripts/hatch.sh next        # the top of the todo column
   ./scripts/hatch.sh work        # one increment, unattended
   ```

   The secret is shown exactly once, at mint. It is written to `scripts/.env`
   (mode 600, git-ignored) and it never belongs in a tracked file — Aerie ships
   to other operators, and a key in the artifact is one operator's credential
   inherited by everyone who clones it. The `aerie_ak_` prefix is there so one
   that slips into a diff is recognisable on sight.

The key reaches `/api/hatch/*` and nothing else. Everything else an operator
does — minting more keys, revoking sessions, editing the house, and editing the
playbooks that tell an agent what to do — refuses it, on purpose. A `403` means
the key works and the route is not one a key may take.

## Connecting a family calendar

The kiosk shows a today-and-tomorrow agenda from any number of Google accounts,
with each calendar behind those accounts toggled on or off individually. Aerie
owns the OAuth client, so this is operator setup done once, not a per-family-
member sign-in. The design is in
[docs/kiosk-architecture.md](docs/kiosk-architecture.md#family-calendar).

1. In a Google Cloud project, enable the **Calendar API** and create an OAuth
   client of type *Web application* whose authorized redirect URI is exactly
   `https://home.${DOMAIN}/api/calendar/oauth/callback`.
2. Publish the consent screen to **Production**. In *Testing*, Google expires
   refresh tokens after seven days — which shows up as the calendar quietly
   going stale every week rather than as an error. Unverified is fine at family
   scale; Google allows it behind a warning screen, capped at 100 users.
3. On the admin app's Settings page, set **Google client ID** and **Google
   client secret**. The secret is stored obfuscated and redacted on read, the
   same as the Home Assistant token and the kiosk Wi-Fi password. Set **Google
   OAuth redirect URI** only if a proxy makes Aerie derive a URI that doesn't
   match what you registered — it is an exact-match override, normally left
   blank.
4. On the admin app's **Calendars** page, click **Connect a Google account** and
   complete Google's consent screen. Repeat for as many accounts as you like.
5. Include the calendars that belong on the wall. Nothing is included by
   default — connecting an account should not put a work calendar in the
   kitchen. Events appear within five minutes, or immediately via **Sync events now**.

Outdoor hazards need no setup at all: weather alerts and air quality both
default to keyless providers. What they do need is the site's latitude and
longitude on the Settings page, and the **Active alerts** card at the bottom of
that page is there to confirm the configuration produced something. An empty
card on a calm, clean-air day is a working configuration, not a broken one.

## Adding a camera

A camera imported from Home Assistant shows up on the wall as a button, and puts
itself on screen when something moves in front of it. The design is in
[docs/camera-devices-architecture.md](docs/camera-devices-architecture.md).

1. Wire the camera in and adopt it in Home Assistant. **PoE, single-lens.**
   Battery models stay awake for as long as a stream is being watched, which is
   fatal for a display that opens a feed on every motion event, and a dual-lens
   camera imports as half a camera.
2. On the admin app's **Discovery** page, import it. It should suggest
   **Camera**; the channels it proposes are the camera feed plus a motion
   sensor, preferring the on-camera AI `_person` sensor over plain `_motion`
   where the camera publishes one — the plain sensor fires on trees, rain and
   headlights, and every transition opens a modal in the kitchen.
3. On the **Devices** page, fill in **Camera connection**: username, password,
   and the RTSP stream path. The host is usually already there — Home Assistant
   reports the camera's own address and keeps it current across a DHCP move, so
   the field is an override that is normally left blank. The default stream path
   is Reolink's H.264 sub-stream, which is deliberately the low-resolution one:
   a wall tablet should not be decoding 5MP, and the main stream on some models
   is H.265, which no browser here plays.
4. Click **Live view** on the device row. That is the whole check — a picture
   means the address, the credential and the pod-to-camera network path are all
   good. Expect the first frame to take a moment: the camera only emits a
   keyframe every few seconds and nothing can be shown until one arrives.
5. Walk in front of it and confirm the kiosk opens the feed by itself.

If the live view says the camera has no address, step 3 is unfinished. If it
says the feed is unavailable, dispatch the **Verify: Cameras** workflow — it
answers the cluster half (is go2rtc healthy, can a pod actually reach the
camera's RTSP port) without printing any camera's password into a run log.

The camera's password lives in Aerie's database, protected the same way the
Home Assistant token is, and never enters git or the cluster. Adding a camera
touches nothing outside the admin UI.

## Making games with a child

The family shell's **Game** app (`https://home.${DOMAIN}/apps/family/game`) lets
a child build a game by typing what should happen — "i am a red ball", "i want
to roll down a hill" — with Claude rewriting the running game each time. The
design, including why the generated code runs in a sandboxed frame with no
access to the house, is in [docs/game.md](docs/game.md).

One piece of setup, done once:

1. Get an Anthropic API key (`https://console.anthropic.com`). It is billed to
   whoever runs this install, which is why Aerie does not ship one.
2. On the admin app's Settings page, set **Anthropic API key**. It is stored
   obfuscated and redacted on read, the same as the Home Assistant token.

Until that is set, the Game app says so rather than offering a text box. For
local development the key can go in `src/Aerie.Api/.env.json` as
`anthropic_api_key` instead.

Two things worth knowing before handing over the tablet:

- **Turns cost money and take time.** "⚡ Quick" is the default and right for
  nearly every request; "🧠 Careful" is a slower, pricier model for the ask that
  keeps coming back wrong. The history panel (🕘) shows what each turn took and
  how many tokens it used.
- **Nothing is lost.** Every version is kept, ↩ goes back one step, and a game
  that crashes repairs itself twice before putting the last working version
  back on its own.

## Kiosk tablet install

See [docs/kiosk-architecture.md](docs/kiosk-architecture.md) for how the kiosk app, its CI build, and QR provisioning fit together. To put a fresh tablet into service:

1. On the admin app's Settings page, set **Kiosk Wi-Fi SSID** (and password/security type, if needed) if not already configured.
2. Factory reset the tablet (or start from unboxed). On the Welcome/language-select screen of setup — before signing in to anything — tap the same spot on the screen 6 times to launch the QR provisioning scanner. (Exact trigger screen varies slightly by OEM/Android version.)
3. On another device, open the admin app's Provisioning page (`https://home.${DOMAIN}/apps/admin/provisioning`) and scan the QR code it displays. The tablet joins Wi-Fi, downloads the APK, verifies it against the signing checksum, installs it, and sets it as Device Owner automatically.
4. If the setup wizard never offers a QR scanner (some budget/non-GMS tablets omit it), fall back to the manual USB/adb path in [docs/kiosk-architecture.md](docs/kiosk-architecture.md#manual-fallback-usbadb).
5. Launch the app once (from the tablet, or via `adb shell am start -n family.landis.aeriekiosk/.MainActivity` over wireless debugging). It should load the dashboard fullscreen and lock itself in — no status bar, no way to swipe to recents/home.
6. Verify lockdown (no path back to stock Android via edge swipes or power/volume holds), boot persistence (reboots straight into the kiosk, no lock screen), and reconnect behavior (toggling Wi-Fi off/on shows a "Reconnecting…" overlay and the dashboard reloads once connectivity returns).
