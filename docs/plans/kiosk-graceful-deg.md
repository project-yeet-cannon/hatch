# Graceful degradation for the kiosk UX stack

**Status:** Not started. Seven phases. Phase 0 is free and de-risks Phase 3 —
run it first. Phases 1–2 are the native shell, 3–5 the web app, 6 the API's
half of staleness, 7 the sweep. Phases 1/2 and 3/4/5 are independent of each
other and can land in either order; Phase 5 needs Phase 6's field.

A wall tablet has no keyboard, no console, and nobody standing at it. Every
failure it can have is a failure someone discovers hours later, by walking past
a white rectangle. The dashboard is already careful about *data* it doesn't
have — a zone with no reading renders `—`, an empty agenda renders nothing, a
house with no Immich has no hole where the photos would be. What it is not
careful about is *itself*: the layers between the tablet's power button and a
rendered snapshot each fail into a blank screen, and none of them says so.

This plan closes that. The organising idea is that **every layer must be able
to draw a clock**. The time of day is the one thing the wall can always be
right about — it needs no network, no API, and no bundle — so it is the floor
that every error state stands on, and a tablet showing the time and an
explanation is a tablet that is still doing part of its job.

## What is actually broken

Reading the tree turned up more failure modes than the three in the original
note, and moved one of them.

**The update check is not what hangs the boot.**
[`UpdateManager.start()`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/UpdateManager.kt)
posts its first check 60 seconds after launch, and every check runs on its own
`Thread` with 10s connect/read timeouts. An unreachable `files.<DOMAIN>` cannot
block `onCreate`, cannot block the UI thread, and cannot delay the first page
load. Whatever produces the white screen at boot, it is not this. Phase 2 still
hardens the update path — a transient failure at boot currently waits out the
*next* ladder interval, which is six hours once the tablet has been up an
hour — but it is a correctness fix, not the white-screen fix.

**The four things that can actually paint white**, in the order they happen on
a cold boot:

1. **The font stylesheet is render-blocking and off-network.** Both
   [`index.html`](../../src/Aerie.Web/apps/dashboard/index.html) and the portal
   [`index.html`](../../src/Aerie.Web/index.html) pull Manrope from
   `fonts.googleapis.com` through a bare `<link rel="stylesheet">`. That is a
   render-blocking subresource on a third-party host: with the WAN down but the
   LAN fine — the exact shape of a modem reboot or an ISP outage — the browser
   holds first paint until that socket gives up. Nothing else in the house
   depends on the internet to draw its first frame; this does.
2. **`<div id="root">` is empty and stays empty.** If the module script 404s
   (a deploy that moved the hashed bundle out from under a cached document),
   fails its MIME check, or throws during evaluation, React never mounts and
   there is nothing in the document to see. The
   [`ErrorBoundary`](../../src/Aerie.Web/apps/dashboard/src/components/ErrorBoundary.tsx)
   only helps *after* mounting; it cannot catch a bundle that never ran.
3. **A GeckoView content-process crash is unhandled.** `MainActivity` registers
   `object : GeckoSession.ContentDelegate {}` — an empty delegate, present only
   to work around bug 1758212 — so `onCrash()` and `onKill()` are the default
   no-ops. A crashed content process leaves the `GeckoView` blank *forever*:
   `onLoadError` does not fire, `onPageStop` does not fire, `scheduleRetry()` is
   never called, and the "Reconnecting…" overlay never appears. This is the one
   failure mode with no recovery path at all short of a power cycle.
4. **An HTTP error is a successful load.** `onLoadError` fires for DNS, TLS and
   connection failures. It does *not* fire for a 502 from the ingress or a 500
   from the API — those are ordinary responses with an error body, so
   `onPageStop(success = true)` runs, `currentRetryDelayMs` resets, and the
   overlay is *hidden* over whatever the proxy served. The shell believes it is
   connected.

**And three things that fail quietly rather than blankly:**

5. **A poll that fails after the first success is invisible.**
   [`App.tsx`](../../src/Aerie.Web/apps/dashboard/src/App.tsx) sets `error`, but
   renders it only in the `data === null` branch. Once a snapshot has landed,
   every subsequent failure for the rest of the tablet's uptime changes nothing
   on screen. The wall shows an hour-old house with full confidence.
6. **A dead sensor reads as a live one.**
   [`ZoneService.BuildZoneClimate`](../../src/Aerie.Api/Services/Dashboard/ZoneService.cs)
   sets `currentTempF` to `rows[^1].Value` — the newest measurement *anywhere in
   the 9-hour history window*. A sensor that stopped reporting at noon still
   produces a confident number at 5pm, and `ZoneClimate` carries no timestamp
   for it, so no client could tell the difference even if it wanted to. This is
   why the staleness work needs an API change and not just a component change.
7. **The header date comes from the snapshot, the clock from the device.**
   `formatMonthDay(data?.generatedAt ?? now.toISOString(), …)`. A snapshot that
   went stale before midnight and a clock that did not leaves the wall reading
   "Thursday 3rd" above "12:20 AM" on Friday.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Where the error screen lives | **Both layers** — static fallback inside `#root`, and a real screen in `MainActivity` | They cover disjoint failures. The in-page one cannot run if the document never arrived; the native one cannot see a bundle that broke after a successful load. Either alone leaves a white screen on the other's case |
| What the clock reads from | The device, always | It is the only source that survives every failure this plan is about. `generatedAt` stops driving the header date entirely (finding 7) |
| Timezone when unknown | `Intl.DateTimeFormat().resolvedOptions().timeZone`, falling back to `America/New_York` | Matches [`config.ts`](../../src/Aerie.Web/apps/dashboard/src/config.ts)'s existing `DEFAULT_TIME_ZONE`. Kotlin's half uses `ZoneId.systemDefault()` with the same fallback |
| How errors reach the red dot | A ring buffer inside `clientLogger`, subscribed to | Every failure path in the app already calls `clientLogger.error` — 17 call sites across hooks, components and the boundary. A buffer over that seam is a complete error feed for the price of one module change, and adds no new instrumentation to maintain |
| Dot lifecycle | Appears on any error; opacity steps down over consecutive clean polls; unmounts at nine | A binary dot blinks on a flapping API and forgets a problem that was real five minutes ago. Decay keeps the recent past legible without becoming permanent furniture |
| Dot placement | Fixed, top-left, above the date block | The right half of the header is a 108px clock, the loudest element on the page; a warning indicator should not compete with it |
| Dot color | `color-mix(in srgb, #d14343 N%, var(--card))` | The [`AlertBanner`](../../src/Aerie.Web/apps/dashboard/src/components/AlertBanner.tsx) precedent, and for its reason: a fixed red dot would be the brightest thing in the house at 3am. The hue is allowed to survive the night; the luminance is not |
| Staleness bands | fresh < 10min · stale 10–20min · no data > 20min | `SampleChannels` runs every minute but reads HA's recorder, so the cadence is the *device's*. Ten minutes is past any plausible device interval without being so tight that a slow thermostat reads as broken |
| Shell's view of origin health | An explicit probe after each load, not an attempt to see inside Gecko | A WebExtension port or a JS bridge is a large mechanism for one bit of information. An `HttpURLConnection` against `/api/app-version/dashboard` after `onPageStop` answers finding 4 with no bridge and no coupling to the page |
| Manrope | Self-hosted, `font-display: swap` | The wall must not need the internet to draw its first frame. Also removes a third-party request from every page load in the house |

## Phase 0 — Ask the logs which white screen this is

**The data may already exist.** `index.html` posts `kiosk index.html parse
started` before the module script is even fetched, and
[`main.tsx`](../../src/Aerie.Web/apps/dashboard/src/main.tsx) posts `kiosk
main.tsx module evaluated` as its first line. The gap between those two, per
`sessionId`, is exactly the signal that separates finding 1/2 from finding 3/4:

- **Neither line** for a session that should exist → the document never
  arrived, or the content process died before script ran (findings 3, 4).
- **Parse-started with no module-evaluated** → the document arrived and the
  bundle did not run (finding 2), or first paint was blocked long enough that
  the tablet was observed white while it waited (finding 1).
- **Both, then nothing** → a render-time throw, which the `ErrorBoundary`
  should already be reporting.

- [x] Query `aerie-logs` in OpenSearch for `app: dashboard` sessions over the
      last 30 days, grouped by `sessionId`, and classify them by which of the
      two lines is present.
- [x] Cross-reference `app: kiosk-android` lines by `deviceId` for the same
      window.
- [x] Write the answer into this section.

### What the logs said (queried 2026-08-28, over `aerie-logs-2026.08.20`…`.08.29`)

| Line | Sessions |
|---|---|
| `kiosk index.html parse started` | 87 |
| `kiosk main.tsx module evaluated` | 85 |
| `kiosk root render invoked` | 85 |
| `App mounted, starting dashboard data source` | 85 |
| `Initial dashboard data loaded` | 80 |

**Eight sessions produced exactly one log line, ever.** The document parsed, the
inline beacon fired, and nothing else was ever heard from that page — no module
evaluation, no mount, no poll, no error. That is finding 2, observed, eight
times in nine days. Not a hypothesis.

**They arrive in bursts.** Four within 83 seconds on 08-21 (20:19:20, 20:19:20,
20:20:42, 20:20:43) and four within six minutes on 08-25 (23:26:46, 23:32:28,
23:32:29, 23:32:30). A single tablet failing once does not produce four
sessions; a tablet *retrying* does. The spacing is roughly the shell's
2s → 30s ladder and the page's own drift reload, each retry dying at the same
point — which is why the wall stays white rather than recovering.

**And the 08-25 burst has a cause sitting right next to it.** Filtering
`Dashboard data load failed` to after the 08-22 auth fix leaves 14 real
failures, and three of them are in the 08-25T23 hour, the same hour as four of
the eight dead sessions:

| Reason | Count (since 08-23) |
|---|---|
| `Dashboard API request failed: 500` | 7 |
| `Dashboard API request failed: 502` | 3 |
| `NetworkError when attempting to fetch resource.` | 3 |
| `Failed to fetch` | 1 |

So the sequence is: **the API goes unhealthy → the page reloads (drift check or
shell retry) → the reload lands on a document/bundle mismatch → white → retry →
white.** Findings 2 and 5 are not two problems; they are the two halves of one
incident, and the fallback screen in Phase 3 is what would have turned it into
a clock and a sentence instead of a white rectangle.

Three more things fell out of the query that were not in the original list:

- **The 678 total `Dashboard data load failed` lines are overwhelmingly the
  08-21 401 storm** (397 of a 400-doc sample), which
  [`signIn.ts`](../../src/Aerie.Web/apps/dashboard/src/lib/signIn.ts) already
  fixed. The 14 that remain are 500s, 502s and network errors — every one of
  them invisible on the wall, per finding 5. This is the number that justifies
  the health dot: not "the API might fail" but "the API failed 14 times this
  week and nobody watching the wall could have known."
- **A failed log POST erases the boot window.** Session `d7560a19` logged
  `parse started`, then jumped straight to `Dashboard data refreshed` — the
  *refresh* wording proves the initial load succeeded, so all four boot lines
  existed in the app and were lost in transit. They share one batch, and
  `send()` catches and drops, so a single POST landing on a terminating replica
  takes the most diagnostically valuable four lines of the page's life with it.
  Added to Phase 7.
- **The shell barely restarts.** Four `Kiosk app started` lines against 87
  document parses, so essentially every white screen was an in-page reload, not
  an app relaunch. `Display: no cached sun events; backlight unmanaged until
  first fetch` appears once, confirming that path is reachable.

**Ordering, decided:** Phase 3 first — it is the one that would have covered the
observed incident. Phase 4 second, since the 14 silent failures are the
next-most-visible gap. Phase 6 then 5, then 1–2 on their own APK rollout, then 7.

## Phase 1 — The shell always draws something

Everything here is in
[`MainActivity.kt`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/MainActivity.kt).
The "Reconnecting…" `TextView` becomes a proper error view; the two blind spots
that route around it get closed.

- [x] **Handle the content crash.** Replace the empty `ContentDelegate` with
      one implementing `onCrash(session)` and `onKill(session)`: log through
      `KioskLogger.error` with the crash kind, call `session.close()` /
      `session.open(runtime)` to rebuild the content process, re-attach it to
      the `GeckoView`, show the error view, and go through `scheduleRetry()`
      rather than reloading immediately — a crash loop must back off like
      anything else. Keep the object non-empty regardless, per bug 1758212.
- [x] **Probe origin health after each load.** On `onPageStop(success = true)`,
      fire one background `HttpURLConnection` HEAD at
      `<DASHBOARD_URL>api/app-version/dashboard`. A 2xx confirms the load and
      keeps today's behaviour (hide overlay, reset backoff). A 4xx/5xx or a
      throw means the document that just "loaded" is an error page: show the
      error view with the status, and `scheduleRetry()`. While the error view is
      up, re-probe on the same backoff and reload on the first 2xx.
- [x] **Build the error view.** A `FrameLayout` replacing `reconnectingOverlay`,
      painted the deep-night surface rather than pure black, holding:
      - the date and the time, from `ZoneId.systemDefault()` with an
        `America/New_York` fallback, ticking on a 15s `Handler`;
      - one sentence naming the *category*, from a `when` over
        `WebRequestError.code` (`ERROR_CATEGORY_NETWORK` → "The house network
        can't be reached", `_SECURITY` → "The connection wasn't trusted",
        `_URI`/`_CONTENT` → "Aerie answered with something unreadable"), plus
        the HTTP-status and content-crash cases from above;
      - a **Reload** button calling `loadDashboard()` (which already carries
        `LOAD_FLAGS_BYPASS_CACHE` — this is the only reload in the system that
        can truly bypass the cache, which is why the button belongs here);
      - a small footer line: `versionName`/`versionCode` and the last four of
        the `deviceId`, so a photo of the screen is a diagnosis.
      Every string into `strings.xml`; the file currently holds two.
- [x] Keep the retry ladder as it is — 2s → 30s — but log each transition
      through `KioskLogger.warn` with the delay, so a tablet stuck retrying is
      visible in OpenSearch rather than only on the wall.
- [x] Unit-tested as pure functions in `LoadFailures.kt`, pulled out of the
      activity to make that possible — 11 tests alongside
      [`CircadianBrightnessTest`](../../apps/kiosk/app/src/test/java/family/landis/aeriekiosk/CircadianBrightnessTest.kt)'s
      20.

## Phase 2 — The update check never interrupts, and recovers faster

Confirming what is already true, and fixing the one case where it isn't.

- [x] **Assert the invariant in code, not just in prose.** Add a comment at
      `checkForUpdate` stating that nothing on this path may touch the UI
      thread or the error view, and keep the `Thread` boundary as the first
      statement in the method.
- [x] **Give failures their own backoff.** Today a failed check simply waits for
      the next ladder interval, which is six hours once the tablet has been up
      an hour — so a router reboot during a deploy can cost most of a day. Add a
      separate failure schedule (30s → 30min, doubling) that runs *alongside*
      the ladder without disturbing it, and resets on the first success. The
      ladder stays the description of "how often do we expect a new build"; the
      backoff is "how fast do we recover from not being able to ask".
- [x] **Never download on an unread version.** `fetchLatestVersionCode()`
      returning `null` already skips the download; make that explicit rather
      than incidental, and count it as a failure for the backoff above.
- [x] **Log the backoff state** (`consecutiveFailures`, `nextDelayMs`) on each
      failed check, so a tablet that cannot reach `files.<DOMAIN>` is visible
      before someone notices it is three versions behind.
- [x] Unit-test the interval selection — ladder and backoff together — against a
      fake clock.

## Phase 3 — The page cannot go white ✅

- [x] **Self-host Manrope.** Subset the five weights the pages actually use into
      `apps/dashboard/public/fonts/`, declare them with `@font-face` and
      `font-display: swap` in `theme.css`, and delete the `fonts.googleapis.com`
      `<link>` from both `index.html` files. `swap` means a broken font file
      costs a fallback face, never a blank frame.
- [x] **Ship a fallback screen inside `#root`.** Static markup in
      `apps/dashboard/index.html`, which no build step touches (already the
      documented convention for that file's inline script). It renders:
      - date and time, from `new Date()` through
        `Intl.DateTimeFormat().resolvedOptions().timeZone ?? 'America/New_York'`,
        ticking on a 15s `setInterval`;
      - the message "Loading the dashboard…";
      - a **Reload** button navigating to `location.pathname + '?r=' + Date.now()`,
        so a tablet holding a stale document gets around it even if the
        `no-cache` revalidation somehow doesn't.
      `createRoot(...).render()` replaces all of it on mount, so the cost on a
      healthy load is one paint of markup nobody sees.
- [x] **Add a mount watchdog.** In the same inline script, a `setTimeout` at 10s
      that — if a `window.__aerieMounted` flag set by `main.tsx` is still
      unset — swaps the message to "The dashboard didn't finish loading" and
      logs `kiosk mount watchdog fired` through the same `fetch` the parse line
      uses. That log line is the durable version of finding 2.
- [x] Style the fallback with the deep-night palette inline, not with tokens —
      it must render with zero CSS files loaded.
- [x] **Fix the header date** (finding 7): `formatMonthDay`/`formatWeekday` take
      `now`, never `generatedAt`. The snapshot's age is Phase 4's business, not
      the calendar's.

## Phase 4 — The red dot

- [x] **Turn `clientLogger` into an error feed.** Add a bounded ring buffer
      (64 entries, dropping oldest) holding every `warn` and `error` it
      enqueues, plus `subscribe(fn)` / `getEntries()`. No call site changes:
      the [17 existing `clientLogger.error` sites](../../src/Aerie.Web/apps/dashboard/src/lib/clientLogger.ts)
      across the hooks and components become the feed for free. The window
      `error` (capture-phase, so resource failures are already included) and
      `unhandledrejection` listeners are in that count.
- [x] **Capture console errors.** Wrap `console.error` at module init, enqueueing
      through the same path and then delegating to the original. Guard against
      re-entry so a logger failure cannot recurse.
- [x] **Capture network errors.** Wrap `window.fetch` at module init: log any
      non-`ok` response (method, path, status) and any thrown request. Exclude
      `/api/ui-logs` itself, or a logging outage becomes a logging storm.
      Dedupe by `method + path + status` within a short window so a 60s poll
      failing for an hour is one entry with a count, not sixty entries.
- [x] **A pure health-signal module** — `lib/healthSignal.ts`, next to
      [`kioskLifecycle.ts`](../../src/Aerie.Web/apps/dashboard/src/lib/kioskLifecycle.ts)
      and unit-tested for the same reason: what it does is a state machine over
      time, and "the dot really did go away" has to be asserted rather than
      eyeballed. It takes error events and poll outcomes and produces a level:
      `100%` on any error, stepping to `60%` / `30%` / gone at three, six and
      nine consecutive successful dashboard polls, resetting to `100%` on the
      next error.
- [x] **`HealthDot`** — a `position: fixed` dot in the top-left of `.hf-page`
      (inside it, so the circadian custom properties resolve), colored
      `color-mix(in srgb, #d14343 <level>%, var(--card))`, with a generous
      invisible tap target around a small visible dot. `aria-label` naming the
      count. Renders nothing at level zero.
- [x] **`HealthModal`** — the same overlay idiom as
      [`GatherOverlay`](../../src/Aerie.Web/apps/dashboard/src/components/GatherOverlay.tsx)
      (fixed, inside `.hf-page`), listing the buffer newest-first: time, level,
      message, and the metadata that matters per kind (status and path for a
      network entry, source/line for a console one). A **Reload** button at the
      bottom, and a footer line with the loaded app version from
      [`appVersion.ts`](../../src/Aerie.Web/apps/dashboard/src/lib/appVersion.ts)
      and the `deviceId`.
- [x] **Hold the lifecycle while it is open**, through the existing `hold` flag
      on [`useKioskLifecycle`](../../src/Aerie.Web/apps/dashboard/src/hooks/useKioskLifecycle.ts)
      — reading an error list is exactly the case where an idle reset or a
      deploy reload takes the evidence away mid-read.
- [~] **Deviation:** the Reload button sits in a footer, not the top half. That
      constraint exists because an IME owns the bottom fifth of the screen and
      the viewport may not resize for it — and this modal has no text input, so
      no keyboard ever rises over it. Against that, putting "Reload the
      dashboard" next to the ✕ in the top bar puts two adjacent controls with
      very different consequences under the same thumb. Separated, deliberately.
      If an input is ever added here, this has to move.

## Phase 5 — Climate bars tell the truth about their age

Needs Phase 6's field. Client-side only once it has it.

- [x] **`lib/staleness.ts`** — a pure `classifyReading(asOf, generatedAt)`
      returning `'fresh' | 'stale' | 'none'`, with the two thresholds exported
      as named constants and documented as a pair, the way
      [`kioskIdleTimings.ts`](../../src/Aerie.Web/apps/dashboard/src/lib/kioskIdleTimings.ts)
      documents its ladder. Unit-tested at and around both boundaries, and for
      a null `asOf`.
- [x] **`ZoneCard`, `fresh`** — unchanged.
- [x] **`ZoneCard`, `stale`** — the temperature and the sparkline drop to
      `--muted`, and the summary row gains a tappable affordance that reveals
      "as of 3:42 PM" in the expanded body. The number stays: a
      fifteen-minute-old reading is still the best answer anyone has.
- [x] **`ZoneCard`, `none`** — the swatch goes `--muted`, the temperature reads
      `—`, the badge reads "no data", and the chart renders as a flat muted
      band rather than a stale line. `deriveZonePresentation` already produces
      exactly this shape for `currentTempF === null`; extend its guard to cover
      an expired `asOf` so there is one code path, not two.
- [x] **`OutsideCard`** gets the same three states from the same function — the
      outside temperature comes down the same road and goes stale the same way.
- [x] **Snapshot age is its own signal**, and it turned out to be load-bearing
      rather than a nicety: measuring against `generatedAt` verbatim would have
      frozen every reading's age the moment the API died, because
      `generatedAt - currentAsOf` is a constant once both stop moving. A wall
      whose API failed an hour ago would have reported every bar as fresh —
      the exact confident-but-wrong screen this phase exists to prevent, in the
      code meant to prevent it. `App` now derives `nowOnServerClock`: the
      snapshot's clock advanced by how long the page has held it, re-derived on
      the 15s tick, so each bar walks fresh → stale → no data on its own while
      the poll keeps failing. Both terms of the elapsed subtraction come from
      the device clock and both timestamps in the comparison from the server's,
      so neither clock's drift leaks into the other.
- [~] **Deviation:** it is *not* also fed to the health dot as an error. A
      snapshot older than two poll intervals means the polls are failing, and a
      failing poll already logs `Dashboard data load failed` every 60s — a
      second error for the same condition is one entry per minute of pure
      duplication in a 64-slot buffer.

## Phase 6 — The API says when a reading was taken

Small, and everything in Phase 5 depends on it.

- [x] Add `CurrentAsOf` (`DateTimeOffset?`) to `ZoneClimate` in
      `Models/Dashboard`, set from `rows[^1].Timestamp` in
      [`ZoneService.BuildZoneClimate`](../../src/Aerie.Api/Services/Dashboard/ZoneService.cs)
      — the same row `currentTempF` comes from — and **null when the value fell
      back to a history bucket**, because a bucket's timestamp is a bucket
      boundary and not a reading.
- [x] Add the equivalent to `OutsideClimate` in
      [`WeatherService`](../../src/Aerie.Api/Services/Dashboard/WeatherService.cs).
- [x] Mirror both onto the TypeScript
      [`types.ts`](../../src/Aerie.Web/apps/dashboard/src/types.ts) contract,
      optional-null with a comment saying what null means — it is a different
      statement from "no reading", and the distinction is the whole point.
- [x] Update the mock and test data sources so `?source=mock` can exercise all
      three staleness states; a state that can only be reached by unplugging a
      sensor is a state nobody will look at twice.
- [x] Extend the `ZoneService` tests: a fresh row, a row at the far edge of the
      window, and a zone whose only value came from a bucket.
- [x] Note the field in [dashboard-api-manifest.md](../dashboard-api-manifest.md).

## Phase 7 — The sweep

The failure modes that are not blank screens but are the same bug.

- [ ] **Audit every data path for its degraded state.** `useGatherLists`,
      `usePhotoCarousel`, `usePanelState`, `useCameraStream`, `useRoutineTaps`
      and `useMotionEvents` all log an error today; what each *shows* varies.
      One pass, one rule: every one either renders a muted "unavailable" in the
      space it owns, or renders nothing at all — and never a half-drawn control
      that does nothing when tapped. All of them feed the dot for free once
      Phase 4 lands.
- [ ] **Sanity-check the device clock.** The clock is the floor this whole plan
      stands on, and a tablet that has been offline long enough can drift. On
      each successful poll, compare `Date.now()` against the response's `Date`
      header; past a couple of minutes' skew, log it and show the offset in the
      health modal. Do not silently correct the displayed time — a wall that
      disagrees with the phone in your hand is a bug worth seeing.
- [ ] **`handledUnauthorized` returns a promise that never settles**
      ([`apiDataSource.ts`](../../src/Aerie.Web/apps/dashboard/src/api/apiDataSource.ts)),
      deliberately, so the caller stops rendering while the browser navigates.
      Confirm the 60s poll interval doesn't stack pending promises behind it
      during a slow navigation, and cancel the interval on the 401 path if it
      does.
- [ ] **`KioskLogger` spawns a raw `Thread` per line.** Harmless at today's
      volume; a wall in a bad state is exactly when volume goes up. Give it a
      single-thread executor and a bounded queue that drops rather than grows.
- [ ] **`DisplayController`'s sun-events fetch** falls back to its
      `SharedPreferences` cache. Confirm what a tablet with *no* cached events
      and no network does on first boot — a curve with no anchors must degrade
      to a fixed mid brightness, not to zero.
- [ ] **The portal page** ([`src/Aerie.Web/index.html`](../../src/Aerie.Web/index.html))
      gets the font fix from Phase 3. It already degrades correctly for
      unreachable subdomains, which is the pattern the rest of this plan is
      copying.

## Verification

Automated, per phase: `make test-web` for the dashboard, `make test-api` for
the `ZoneService` and DTO changes, and Gradle unit tests for the Kotlin pure
functions extracted in Phases 1–2. Build the app through `make`, not `dotnet`
directly.

On the hardware, which is the operator's half — each of these is a failure this
plan claims to have fixed, and each is producible without breaking anything:

- **Bundle 404** → rename the hashed asset on the server. Expect the in-page
  fallback with a live clock, not white.
- **Origin 502** → stop the API pods, leave the ingress up. Expect the native
  error screen naming the status, not a proxy error page under a hidden overlay.
- **WAN down, LAN up** → block egress at the router. Expect first paint at the
  usual speed, in a fallback face.
- **Sensor stops reporting** → disable one HA entity. Expect the bar to dim at
  ten minutes and read "no data" at twenty.
- **Content crash** → `chrome://crashcontent` is not reachable under the
  navigation delegate's host check, so force it by memory pressure or accept
  this one as covered by code review and the logs.
