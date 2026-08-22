# Kiosk Display Control

**Status:** Phases 1 and 2 implemented in the tree, both pending the hardware
checks below. Phases 3–4 unstarted. Four phases, ordered by dependency for once
— each one is usable on its own, but Phase 3 is what unlocks the per-room
variation that motivated the whole thing, and Phase 4 is only worth building on
top of Phase 3.

A wall tablet is a light fixture that happens to show information. Right now
Aerie controls the information and not the fixture, and the gap between those
two shows up at 3am as a glowing rectangle in a dark hallway.

## The gap

[`circadianTheme.ts`](../../src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.ts)
already dims the dashboard from a daylight palette through amber to near-black
across a night, and
[kiosk-architecture.md](../kiosk-architecture.md#what-the-wall-shows) treats
that as the rule no restyle is allowed to break. It is the right half of the
problem, thoroughly solved — and it is *only* the color half. **CSS cannot
reach the backlight.** A near-black page on a lit LCD is still a lamp; the
palette work makes it a dimmer lamp, not a dark one.

Meanwhile
[`MainActivity.onCreate`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/MainActivity.kt)
sets `FLAG_KEEP_SCREEN_ON` and never touches brightness again. That single flag
is what makes the tablet a 24/7 fixture: it suppresses the system's own screen
timeout, and nothing replaces it. The panel sits at whatever level the tablet
was left at, forever, in every room, on every schedule.

So there are three separate missing pieces, and they are worth naming
separately because they fail differently:

1. **No backlight control at all.** Nothing in the app has ever set a
   brightness value.
2. **No presence signal.** The tablet knows it was touched. It does not know
   anyone is standing in front of it.
3. **No per-kiosk identity.** Every tablet loads the same bare
   `https://kiosk.${DOMAIN}/` and nothing distinguishes the bedroom-hallway one
   from the kitchen one. Until that changes, "the hallway should be darker than
   the kitchen" is not expressible.

(3) is the actual blocker. (1) is nearly free.

## What Android will actually give us

The original question in this file was how much control we have. The answer is
"more than enough," and it's worth writing down which mechanism buys what,
because the tempting one is not the right one.

| Want | Mechanism | Cost |
|---|---|---|
| Dim/brighten this app's screen | `WindowManager.LayoutParams.screenBrightness`, a float `0.0`–`1.0` on our own window | **No permission at all.** Applies while our window is frontmost, which on a kiosk is always |
| Notice a touch before the page does | `Activity.dispatchTouchEvent()` | Free, and needs no bridge into GeckoView |
| Ambient light level | `SensorManager` / `Sensor.TYPE_LIGHT` | No permission. **Presence on hardware unconfirmed** |
| Set *system* brightness globally | `DevicePolicyManager.setSystemSetting` (API 28 — exactly our `minSdk`) writes `SCREEN_BRIGHTNESS`, `SCREEN_BRIGHTNESS_MODE`, `SCREEN_OFF_TIMEOUT` silently as Device Owner | Device Owner only, which we already are. Not needed — window-level is strictly better scoped |
| Turn the display genuinely off | `dpm.lockNow()`, with `setKeyguardDisabled(admin, true)` so it wakes straight back into the kiosk | Device Owner. **Costs touch-to-wake** — see below |
| Wake a genuinely-off display | `PowerManager.SCREEN_BRIGHT_WAKE_LOCK or ACQUIRE_CAUSES_WAKEUP` | Needs `WAKE_LOCK` in the manifest (a normal install-time permission, currently absent). Deprecated since API 17, still functional |

The trap is `lockNow()`. "Shut the display off entirely between 2 and 5" reads
like it wants a real screen-off, but **a display that is truly off does not wake
on touch** — the touchscreen is powered down with it, so the only ways back are
the power button or a programmatic wake lock. On a wall-mounted tablet the
power button is not the interaction you want at 3am.

**Default to fake-off**: `screenBrightness = 0.0` plus a full-black view. On an
LCD, at night, that is visually indistinguishable from off, and touch-to-wake
keeps working for free. Real screen-off stays available as a per-profile
setting, for a kiosk whose panel makes it worth it (an OLED, where black pixels
are genuinely dark and the backlight isn't the story) or one that has a presence
sensor to wake it without a finger.

**Phase 2 shipped only the first half of that** — `0.0`, no black view — on
purpose. `0.0` is `BRIGHTNESS_OVERRIDE_OFF`, documented as the panel's *lowest*
backlight rather than a powered-off display, so the screen stays lit-but-minimal
and stays touch-responsive on its own; the black view exists to hide whatever
the panel still leaks at that minimum, and nobody has yet stood in the hallway to
see whether it leaks anything worth hiding. A few nights of watching answers
that, and answers the clamp question below with it. Build the overlay if and
when the answer is "yes".

Doze is a non-issue as long as the tablets are wall-mounted on chargers — a
plugged-in device does not enter Doze, so a dark screen won't stall
[`UpdateManager`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/UpdateManager.kt)'s
poll. Worth re-checking if a kiosk ever runs on battery.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Where brightness is applied | Window-level `screenBrightness`, not the system setting | Same effect, no Device Owner dependency, and it cannot strand a tablet at an unreadable global brightness if the app crashes — the value dies with the window |
| Who decides policy | The server. One profile per kiosk, fetched by the tablet | The alternative is a constant in an APK that takes a full build-sign-publish-poll cycle to change. Getting a dimming curve right is iterative by nature |
| How the shell and the page agree | They don't talk. Both fetch the same profile and observe touch independently | GeckoView has no `addJavascriptInterface`; a shell↔page bridge means bundling a WebExtension with native messaging. Both halves already poll HTTP, and `dispatchTouchEvent` gives the shell its own view of touch. The bridge buys nothing it doesn't already have |
| Where the curve is interpolated | On the tablet, from sun events + profile, on its own clock | Exactly what the web app already does with `sunEvents` on `DashboardData`. A server that hands over a single current brightness would step the curve at the poll interval and freeze it entirely during a network blip |
| Duplicating `getCircadianPhase` in Kotlin | Accepted, deliberately | ~30 lines against a WebExtension bridge. The seam is `SunEvents`, which is already a stable server-side contract, and both copies get unit tests. Called out here so the next person changing the transition constants knows there are two |
| How presence reaches Aerie | Home Assistant **pushes** on state change (automation → `rest_command`) | [`SampleChannels`](../../src/Aerie.Api/Jobs/SampleChannels.cs) polls at 1-minute intervals — right for temperature, useless for motion. Polling HA fast enough for presence means polling it ~30× more often for one binary |
| Where presence is stored | In memory, per zone, last-changed timestamp. Not ledgered | Every other reading in Aerie is history worth keeping. A presence sensor tripping forty times an evening is not — it's ephemeral state whose only consumer is "right now." A Postgres row per trip buys nothing and costs writes forever |
| Unregistered tablets | Self-register on first poll, get a default profile, appear in admin as unassigned | The provisioning story is already "no cable, one QR scan." Making an operator hand-enter an `ANDROID_ID` to finish setup would be the only manual step left in it |
| What the idle floor is | A second three-keyframe curve, not one number | `IdleBrightness` as a scalar is either too dark at noon or too bright at midnight. Sampling the same phase function means "dimmer than now" is always relative to now, and it costs three profile columns instead of one |
| How the panel knows someone is typing | `ime()` inset visibility, held like the page's `hold()` | IME touches go to the IME's own window and never reach `dispatchTouchEvent`, so the shell's only presence signal goes silent during exactly the interaction that most needs the light on. Two independent holds rather than a bridge, for the same reason the two halves don't talk anywhere else |
| Blackout window, comfort floors, all times | Profile values, never constants | [`ethos.md`](../ethos.md) — "2am to 5am" is true of exactly one household |

## Identity: the thing that has to exist first

The tablets already have a stable id and already send it.
[`KioskLogger`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/KioskLogger.kt)
reads `Settings.Secure.ANDROID_ID` and stamps it on every log line as
`deviceId`. Nothing consumes it as an identity — it's a log field. That's the
whole gap: the id exists, it's stable across reboots and updates, and it is not
registered anywhere.

Two new tables:

**`KioskDevices`** — `Id`, `AndroidId` (unique), `Name`, `ZoneId` →
[`Zones`](../../src/Aerie.Api/Ef/DeviceMapping.cs), `DisplayProfileId`,
`LastSeenAt`. A row appears the first time a tablet polls; an operator names it
and assigns a zone in the admin app.

**`KioskDisplayProfiles`** — a named curve, shared by any number of devices, so
"every hallway" is one edit:

- `DayBrightness` / `AmberBrightness` / `NightBrightness` — the three keyframes,
  mirroring `CircadianTokenSets`' `day`/`amber`/`night` exactly, so the backlight
  interpolates on the same phase function and through the same midpoint as the
  palette. The panel and the pixels move together or the effect falls apart.
- `IdleDimAfterSeconds`, and `IdleDay`/`IdleAmber`/`IdleNightBrightness` — the
  "nobody's here" step. Three keyframes rather than one number, for the reason
  in the decisions table: Phase 2 built it that way and a scalar cannot express
  it.
- `StandbyAfterSeconds` — when the page swaps to the standby view. Phase 2's
  `IdleResetSeconds` belongs here too; the ladder is only correct read as a
  whole, so it should arrive as a whole.
- `BlackoutStart`, `BlackoutEnd` (nullable — null means never),
  `BlackoutMode` (`Dim` | `ScreenOff`), `BlackoutBrightness`.
- `WakeOnPresence` — whether a zone presence signal lifts the display.

The kitchen-vs-hallway case from the original sketch is then two profiles: one
with `NightBrightness` low but non-zero and no blackout, one with a blackout
window and a near-zero night floor.

## The endpoint

`GET /api/kiosk/display-profile?deviceId={androidId}` returns the profile, the
day's sun events, and current zone presence in one document — the same
backend-for-frontend shape `/api/dashboard` uses, for the same reason: a tablet
should need one round trip to know everything about how to light itself.

It needs adding to AuthGate's allow-list alongside
[`KioskProvisioningController`](../../src/Aerie.Api/Controllers/KioskProvisioningController.cs),
and for the same reason — the shell holds no browser session. Nothing in the
response is more sensitive than a room name and a brightness number.

Two poll rates, because the two halves of the response change at wildly
different rates:

- **Profile + sun events**: slow (~15 min), tapering like `UpdateManager`'s.
  Nothing here changes faster than an operator edits it.
- **Presence**: fast (~3 s), on a separate minimal endpoint returning one
  zone's state and its last-changed timestamp.

3 seconds is deliberately unambitious. Presence only ever has to beat *walking
up and touching the screen*, and touch is already handled locally with zero
latency by `dispatchTouchEvent`. The job of presence is to have the display
already lit as someone enters the room — a second or two of lead is the entire
feature. SSE would shave that and can be added later without changing anything
else; it is not worth inventing realtime infrastructure this repo doesn't
otherwise have in order to get it on day one.

## Presence

There is no presence sensor in a tablet. The options, in the order they were
ruled out:

- **Camera-based motion detection** — heavy, and it points a camera at a hallway
  to answer a question about a backlight. No.
- **Ambient light sensor** — worth wiring for daytime brightness regardless, but
  "the room lights came on" is a weak proxy for a person, and useless in
  daylight. A supplement, not the mechanism.
- **An external sensor, through Home Assistant** — the one that fits. Aerie
  already speaks HA, `DeviceChannelMetric` already includes `MotionState`, and
  [`CameraChannelBuilder`](../../src/Aerie.Api/Services/DeviceMapping/CameraChannelBuilder.cs)
  already discovers `binary_sensor.*_motion` siblings during Discovery. Devices
  already carry a `ZoneId`, and a kiosk will too. The socket is built; nothing
  is plugged into it.

**Prefer mmWave presence sensors over PIR.** PIR reports *motion* and goes
quiet the moment you stand still — which on a wall display is precisely when
you're using it. A display that dims in your face while you read it is worse
than one that never dimmed. mmWave reports occupancy, which is the question
actually being asked.

The push path, per sensor, is an HA automation firing a `rest_command` at
`POST /api/kiosk/presence` with `{ zoneId, state, at }` on state change. Aerie
holds it in a keyed in-memory store; the fast poll reads it. A presence signal
older than a configurable staleness window decays to "unknown", which the
profile treats as "clear" — a sensor that dies or an HA that goes away must fail
toward the schedule, never toward a display stuck bright all night.

## Phases

### [x] Phase 1 — Backlight follows the sun

No identity, no new tables, hardcoded curve. **One server change after all**,
which this plan originally said it wouldn't need: the shell calls
`/api/sun-events` over plain `HttpURLConnection`, which shares no cookie jar
with the GeckoView the page runs in, so the page's grant cannot cover it and
the path needs an AuthGate allow-list entry. Reasoned about in
[`auth-architecture.md`](../auth-architecture.md#the-allow-list-is-load-bearing)
with the rest of the list.

- [x] Ported `getCircadianPhase` + a scalar lerp to Kotlin
      ([`CircadianBrightness.kt`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/CircadianBrightness.kt)),
      with unit tests mirroring
      [`circadianTheme.test.ts`](../../src/Aerie.Web/apps/dashboard/src/lib/circadianTheme.test.ts)
      case for case.
- [x] Fetch `GET /api/sun-events` on start and every 6h; cache the last good
      response in `SharedPreferences` so a reboot on a dead network holds the
      curve rather than dropping it
      ([`DisplayController.kt`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/DisplayController.kt)).
- [x] Drive `window.attributes.screenBrightness` from the phase on a 60s tick.
- [x] **Roll cached events forward onto today, and discard them past a week.**
      Not in the original sketch and not optional: a day-old sunset is more
      than a day behind `now`, so the phase function reads the far side of the
      evening transition and returns Night — a tablet rebooting at noon on a
      dead network would dim itself to the night floor in full daylight. Past
      the roll limit the override is handed back to the system rather than
      frozen at its last value.
- [ ] **Confirm on hardware** that the panel's minimum usable brightness is
      above `0.0` and where — the clamp point decides what "night" can mean,
      and whether Phase 4's real screen-off is load-bearing or a nicety. The
      `night = 0.05` default is a guess until someone stands in the hallway.
      Phase 2's idle floor of `0.0` at night is the cheapest way to answer it:
      an untouched tablet drives the panel to its clamp every night, so the
      question stops being "what does 0 do" and becomes "what did it look
      like.

Kills the 3am lamp on its own. Everything after this makes it adjustable.

### [x] Phase 2 — Idle dim and standby

- [x] Native idle timer off `dispatchTouchEvent`; dim to an idle floor, restore
      instantly on touch. Ramp the dim (4s, 10 steps a second), snap the restore
      — a slow brighten reads as an unresponsive screen. The idle floor is a
      second `BrightnessCurve` rather than one number
      ([`DisplayController.kt`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/DisplayController.kt)),
      sampled on the same phase function as the active one, so "dimmer than
      now" tracks the day instead of being too dark at noon and too bright at
      midnight. Its night keyframe is `0.0`; see the fake-off note above for
      why the black overlay that usually accompanies that is deliberately absent.
- [x] Standby view in the dashboard on a longer timeout: time, date, indoor
      temp, at across-the-room scale
      ([`StandbyView.tsx`](../../src/Aerie.Web/apps/dashboard/src/components/StandbyView.tsx)).
      It covers the dashboard rather than replacing it — the snapshot stays
      mounted and polling, so lifting standby shows live data rather than a
      skeleton, and a fixed overlay moves no scroll position, which would
      otherwise arrive back at the lifecycle as activity and lift the standby it
      just entered. Indoor temp is the first zone with a reading, which is the
      operator's own ordering (`ZoneService` sorts by `SortOrder`) rather than a
      new setting invented for one view.
- [x] Both timeouts come from one place —
      [`kioskIdleTimings.ts`](../../src/Aerie.Web/apps/dashboard/src/lib/kioskIdleTimings.ts),
      holding the whole ladder (reset 30s, dim 2m, standby 5m) with the
      reasoning for why each is only correct relative to the others. **One
      caveat, stated rather than papered over:** the shell cannot import a TS
      module, so the dim rung is a mirrored Kotlin constant on the same terms as
      `CircadianBrightness.kt`'s transition constants. Phase 3 is what makes it
      literally one place, by making both sides fetch it.
- [x] Respect [`kioskLifecycle`](../../src/Aerie.Web/apps/dashboard/src/lib/kioskLifecycle.ts)'s
      `hold()` — standby must not swallow a half-typed Gather entry, which is
      the same bug the existing idle reset already guards against. `input` is
      now in `ACTIVITY_EVENTS` itself rather than bolted on by `GatherOverlay`,
      for the reason
      [kiosk-architecture.md](../kiosk-architecture.md#text-entry-on-the-wall)
      gives: soft keyboards don't reliably fire `keydown`.
- [x] **The panel needs its own version of `hold()`, which wasn't in the
      original sketch.** IME touches go to the IME's window, never to the
      activity's `dispatchTouchEvent`, so a long run of typing looks to the
      shell exactly like an empty room and would dim the backlight mid-entry.
      `MainActivity` watches `ime()` inset visibility (API 30+, where that
      signal is dependable) and holds the display lit while the keyboard is up.
- [ ] **Confirm on hardware**: that the IME hold actually fires under immersive
      mode with GeckoView focused — it degrades to touch-only if it doesn't, so
      the failure is a dim mid-typing rather than anything broken — and that two
      minutes is the right dim timeout for someone reading the wall with their
      hands in their pockets.

### [] Phase 3 — Registry, profiles, per-room curves

- [ ] `KioskDevices` + `KioskDisplayProfiles` tables and migration.
- [ ] `GET /api/kiosk/display-profile`, self-registering, AuthGate allow-listed.
- [ ] Shell polls it; the hardcoded Phase 1 and 2 constants become its fallback
      for a tablet that has never reached the server.
- [ ] Dashboard fetches the same profile for its standby timings. This is what
      retires `kioskIdleTimings.ts`'s mirrored Kotlin constant — until then the
      idle ladder is one file plus one copy, which Phase 2 flagged rather than
      pretended away.
- [ ] Admin UI: name a kiosk, assign a zone, pick a profile; edit profiles.
- [ ] Blackout window, `Dim` mode.

This is the phase that answers the original ask. The bedroom hallway and the
kitchen stop being the same tablet.

### [] Phase 4 — Presence

- [ ] `POST /api/kiosk/presence` + in-memory per-zone store with staleness decay.
- [ ] Fast presence poll in the shell; lift the display on presence, and hold
      it lifted while presence persists.
- [ ] `WakeOnPresence` on the profile; `ScreenOff` blackout mode becomes
      selectable once there's something that can wake it.
- [ ] Document the HA automation shape so an operator can wire their own sensor
      without reading the controller.

## Open questions, all cheap to answer at the tablet

Following [kiosk-architecture.md](../kiosk-architecture.md)'s habit of listing
what's unconfirmed rather than assuming it:

- Does the hardware have a `TYPE_LIGHT` sensor? One `SensorManager` query
  settles whether ambient light is available as an input at all.
- Where does `screenBrightness` clamp at the low end, and is the minimum dark
  enough to live with in an unlit hallway? This decides whether `Dim` blackout
  mode is sufficient, whether the black overlay is worth building, and whether
  Phase 4's real screen-off is load-bearing. Phase 2's `0.0` idle floor at night
  puts this in front of anyone walking past at 3am, which is the only way it was
  ever going to get answered.
- Does `screenBrightness` survive the IME being raised, and the immersive-mode
  re-entry on `onWindowFocusChanged`?
- Are the tablets permanently on chargers in every mount? The Doze reasoning
  above depends on it.
