# Camera Devices

## User Feature Request
I want to be able to add camera devices to Aerie. v1 goals:

- discover and import a camera device from HomeAssistant into Aerie
- wire up events in some way such that motion detection on the camera triggers an event in HomeAssistant, which triggers an event in Aerie. This gives us power to kick off workflows when motion is detected
- as a follow-on action, when motion is detected on a camera, the video feed from that camera is automatically and immediately brought up in the kiosk app in a modal. the video feed closes when motion ends, or a user can close the modal by tapping an X
- write unit tests for changes

we are going to do a lean build - getting the events and video flowing through the system is the priority, we will make it nice afterwards. don't hardcode anything, just don't add fluff. ask me when there are opportunities to take a leaner approach.

## Decisions

- HA→Aerie motion events: new HA **WebSocket** client subscribing to `state_changed` (not a webhook, not fast-polling). HADotNet has no WS client, so this is new infrastructure.
- Aerie→kiosk push: **Server-Sent Events** (built into ASP.NET Core, `EventSource` in the browser, no new dependency either side).
- Video feed: a **real live stream** proxied through Aerie.Api (HLS or go2rtc relay), not snapshot-polling. HADotNet only gives still images (`CameraProxyClient`) today, so the stream-fetching side is also new.
- Workflow scope: **hardcode** motion → kiosk-modal as the only reaction for v1, but put it behind a single dispatch seam (`IMotionEventDispatcher`) so a real trigger/action registry can replace it later without a rewrite.

## Hardware Selection

No cameras owned yet as of 2026-08-09. This analysis drives the purchase, and several findings feed back into the plan below (see the flagged items in Phases 2, 7 and 8).

### Chosen brand: Reolink PoE (wired only)

Picked for event latency, not image quality. Reolink is a platinum-tier **Works with Home Assistant** integration (certified April 2025), fully local, and delivers motion binary sensors over **TCP push** — sub-second, rather than the integration's 5-second fast-polling fallback. Phases 4–6 are a latency chain; push at the front is what makes the kiosk modal feel live. It also produces exactly the entity shape `DiscoveryService`/`CameraChannelBuilder` already assume: one `camera.*` plus a sibling `binary_sensor.*_motion` under one HA device.

Candidate models (all PoE, IP67, ~$80–200 street):

| Model | Notes |
| --- | --- |
| RLC-811A | 4K bullet, 5x optical zoom, spotlight. Default pick — zoom means mounting doesn't have to be perfect. |
| RLC-1224A | 12MP dome, 700lm spotlight, wide fixed lens. Broad-area coverage (driveway, back yard). |
| RLC-823A | 4K PTZ, 5x zoom. Only if pan/tilt is wanted; not needed for v1. |
| RLC-510A / 520A | 5MP budget bullet/dome. Fine for a low-value angle. |

Buy PoE. **Not** WiFi, and **not** battery — battery models (Argus, Doorbell Battery) are documented as staying awake while the stream is viewed, which is fatal for a design that auto-opens a live feed on every motion event. LTE models (Go Plus, TrackMix LTE) are outright incompatible with the HA integration. Skip the NVR — it adds nothing here and complicates entity grouping.

### Shortcomings and how they hit this plan

1. **Five `camera.*` entities per device.** Reolink emits Fluent, Balanced, Clear, Snapshots Fluent, Snapshots Clear; only Fluent (low-res sub-stream) is enabled by default. `InferKind` takes `FirstOrDefault` over an **ordinal-sorted** entity list, so today it lands on `_fluent` by accident (the others have no state while disabled). Enabling "Clear" for 4K makes `_balanced`/`_clear` sort ahead and silently changes the anchor. → Phase 2 item.
2. **H.265 main stream won't play in a browser.** On the 4K/12MP models the main ("Clear") stream is H.265; the sub-stream ("Fluent") is H.264. The kiosk modal wants the sub-stream anyway — lower latency, no transcode, and it removes any pressure to transcode 4K on the Windows prod box.
3. **The `_motion` sensor is noisy** (trees, rain, headlights, shadows). v1 opens a modal on every transition, which gets annoying fast. Reolink also exposes `binary_sensor.*_person` / `_vehicle` / `_pet` from on-camera AI, with far fewer false positives. → Phase 2 item.
4. **Avoid dual-lens models** (Duo 3, TrackMix): multiple lenses/channels under one HA device, and `CameraChannelBuilder` takes `FirstOrDefault` for both feed and motion, so half the camera would import silently. Single-lens keeps the one-device-one-feed assumption true.

### Alternatives rejected

- **UniFi Protect** — best-in-class when working, but has repeatedly broken HA motion events on firmware updates (core issues in Dec 2025 and Feb 2026). Wrong risk profile for a system whose whole value is event-driven, and needs a Protect console.
- **Amcrest / Dahua** — good ONVIF hardware and the community Dahua integration passes motion events well, but it's HACS, not core. Don't want the event path depending on a third-party integration.
- **Axis** — core integration, extremely reliable, ONVIF-native, but ~4–6x the price per camera.

### Impact on Phase 7 (video)

Phase 7 currently assumes the stream is fetched *through* HA. That's the hard path: HA bundles go2rtc (since 2024.11) but binds its API to port **11984** and doesn't expose it by default — the docs only describe opening it via `debug_ui`, explicitly flagged as debug-only.

Cleaner with Reolink: **HA for events, camera-direct for video.** Reolink publishes stable RTSP URLs (`rtsp://user:pass@<ip>:554/h264Preview_01_sub` for the H.264 sub-stream), and a **standalone go2rtc** sidecar on the Windows box converts RTSP → HLS/MSE/WebRTC over plain HTTP that `CameraController` proxies. HA can be pointed at that same instance (`go2rtc: url: http://...:1984`), so both systems share one stream source instead of two. `CameraFeed`'s `HaEntityId` stays the identity/discovery key — no schema change — while the bytes come from a documented, stable URL rather than HA internals. This likely also removes the `hls.js` dependency flagged in Phase 8.

## Implementation Plan

Follows the existing `Device`/`DeviceChannel` model (`src/Aerie.Api/Ef/DeviceMapping.cs`) and discovery pipeline (`src/Aerie.Api/Services/DeviceMapping/DiscoveryService.cs`) that Mysa/power-switches used — see `docs/device-architecture.md` for the phase-doc convention this mirrors.

### [x] Phase 1 — Domain model

- [x] Add `Camera` to `DeviceKind` (`Ef/DeviceMapping.cs:33`)
- [x] Add `CameraFeed` and `MotionState` to `DeviceChannelMetric` (`Ef/DeviceMapping.cs:61`) — `CameraFeed` channel's `HaEntityId` is the `camera.*` entity; `MotionState` channel's `HaEntityId` is the sibling `binary_sensor.*_motion` entity, `HaAttribute` null, bare on/off state (fits `EfStateChange`/`ChannelValueExtractor` as-is, no extractor changes needed)
- [x] Confirm no EF migration is actually required (new enum members, same underlying int column, no CHECK constraint) — run `dotnet ef migrations add CameraDevices` and check the generated migration is empty/only whitespace; delete it if so

### [x] Phase 2 — Discovery/import

- [x] Add a `camera.*` branch to `DiscoveryService.BuildSuggestion` (`Services/DeviceMapping/DiscoveryService.cs:58`), before the sensor fallback
- [x] Add a `CameraChannels(cameraEntityId, entityIds)` helper mirroring `ThermostatChannels`/`SwitchChannels`: always emits the `CameraFeed` channel, plus a `MotionState` channel if a sibling `binary_sensor.*_motion` entity is present in the group — landed as `CameraChannelBuilder.cs`, matching `LightChannelBuilder`'s standalone-class shape
- [x] Extract `BuildSuggestion`'s kind-inference chain into a pure function of `IReadOnlyList<string> entityIds` (it isn't today) so the new branch is unit-testable — `DiscoveryService` currently has zero test coverage because `TemplateClient` is sealed; this sidesteps that without adding a wrapper interface — landed as `DiscoveryService.InferKind` + `KindMatch`
- [x] Unit test: camera+motion grouping produces the right `DeviceKind`/channels; camera-without-motion-sibling still imports with just `CameraFeed`
- [x] **Pin the camera-entity anchor deterministically** instead of relying on ordinal `FirstOrDefault` (`DiscoveryService.InferKind`). Reolink emits five `camera.*` entities per device (`_fluent`/`_balanced`/`_clear`/`_snapshots_*`); today the right one is picked only because the rest are disabled and stateless. Prefer the sub-stream (`_fluent`) explicitly — it's H.264, where `_clear` is H.265 and won't play in a browser — and fall back to ordinal-first for non-Reolink cameras. See Hardware Selection §1/§2.
- [x] **Prefer `binary_sensor.*_person` over `*_motion`** for the `MotionState` channel in `CameraChannelBuilder.Build`, falling back to `_motion` when no AI sensor exists. Reolink's plain motion sensor fires on trees/rain/headlights, and v1 opens a modal on every transition. See Hardware Selection §3.
- [x] Unit test both of the above: multi-`camera.*` group picks the sub-stream anchor; `_person`-present group picks it over `_motion`; `_motion`-only group still works

### [x] Phase 3 — Admin UI

- [x] Add `'Camera'` to the `DeviceKind` union and `'CameraFeed'`/`'MotionState'` to `DeviceChannelMetric` in `apps/admin/src/types.ts`
- [x] Add a `Camera` option to the device-kind `<select>` in both `DevicesPage.tsx` and `DiscoveryPage.tsx` (the latter was never updated for `SmartSwitch` — don't repeat that gap here)

### [x] Phase 4 — HA WebSocket event listener

- [~] ~~Prototype the HA WS auth + subscribe handshake by hand against the real instance~~ — **skipped at the user's call (2026-08-23).** HA's host/port/token live in prod `SiteSettings` and the local `.env.json` is empty, so this machine can't reach the instance; the code was written against HA's documented WebSocket API instead (`auth_required` → `auth` → `auth_ok`/`auth_invalid` → `subscribe_events` → `result` → `event`), which has been stable for years. Verification moves to first run against the live instance — the listener logs `Subscribed to Home Assistant state_changed events at {host}:{port}` on a good handshake and names the specific failure otherwise.
- [x] Add a `BackgroundService` (`Services/DeviceMapping/HomeAssistantEventListener.cs`) using raw `System.Net.WebSockets.ClientWebSocket` (HADotNet has no WS client) against `ws://{host}:{port}/api/websocket`, built from `IHomeAssistantConnectionManager`'s host/port/token — the manager grew `ResolveAsync` returning a `HomeAssistantConnection` record, so the WS listener and HADotNet's `ClientFactory` read one source of truth instead of two copies of the SiteSettings-plus-deobfuscate logic
- **Answered — every replica maintains its own connection.** `api` runs at `replicaCount: 3`. A kiosk's Phase 6 `EventSource` lands on whichever replica Traefik picked, so a replica that isn't listening is a kiosk that never sees motion. Electing one leader (via Quartz's clustering, say) would therefore force a cross-replica fan-out — Postgres `LISTEN`/`NOTIFY` or similar — to exist before Phase 6 could work at all. Three idle WebSocket subscriptions against a single HA instance cost nothing, so per-replica is both leaner and more direct. Same shape as `HomeAssistantClientFactoryGate`'s "once per replica, not once per deploy".
- [x] Implement the auth handshake + `subscribe_events` for `state_changed`
- [x] On each event, match `entity_id` against `MotionState` channels and extract old→new state. **Not** a DB query per event — HA sends every entity's `state_changed` down this socket. The map (`HaEntityId` → device ids, `Metric == MotionState` and the device enabled) is cached for 60s and re-read on every reconnect, and is consulted only for frames that already parsed as an on/off transition. It maps to *devices*, plural: nothing stops two devices being hand-mapped to one sensor, and picking one arbitrarily would drop the other's modal.
- [x] Implement reconnect-with-backoff — 2s doubling to 60s, reset only after a session that reached an active subscription (otherwise a revoked token becomes a full-speed reconnect loop). `auth_invalid` is logged at Error, since reconnecting cannot fix it. Unconfigured HA is not a failure: it retries every 60s and logs once.
- [x] **Also added, not in the original plan:** WebSocket keepalive (`KeepAliveInterval` 30s / `KeepAliveTimeout` 15s). Backoff only helps once a socket reports that it broke; a connection killed without a FIN — HA's host losing power, Wi-Fi dropping mid-frame — leaves `ReceiveAsync` blocked forever and the listener silently deaf. The ping timeout is what converts that into a reconnect.
- [x] Register via `builder.Services.AddHostedService<HomeAssistantEventListener>()` in `Program.cs` (correctly inert in the migrate Job, which returns before the host runs)
- [x] Unit test: `HomeAssistantEventParser` is the pure function — raw frame → `MotionTransition?` — with 24 tests in `HomeAssistantEventParserTests`. The socket/reconnect plumbing stays untested, same precedent as `HomeAssistantStateReader`/`HomeAssistantCommandService`.
- **Motion is exactly the state `on`.** `off`, `unavailable`, `unknown` and a missing state all read as no-motion, so a sensor that drops off the network mid-detection (reports `unavailable`, never reports `off`) closes the Phase 6 modal instead of pinning it open until the camera returns. Attribute-only `state_changed` frames — same state, new `last_seen` — are not transitions, so they can't re-open a modal the user just dismissed.
- **Phase 4 terminates in a log line.** The resolved transition is logged as `Motion {started|ended} on device {DeviceId}`; Phase 5 replaces that with `IMotionEventDispatcher.SetMotionState`.

### [] Phase 5 — Motion dispatch (the hardcoded-for-v1 seam)

- [ ] Add `IMotionEventDispatcher` (`Services/DeviceMapping/MotionEventDispatcher.cs`) holding per-device motion-active state in memory (`ConcurrentDictionary<Guid, bool>`) and raising a C# event on change
- [ ] `HomeAssistantEventListener` calls `SetMotionState(deviceId, isActive)` on each transition
- [ ] Register `IMotionEventDispatcher` as a singleton in `Program.cs`
- [ ] Unit test: state transitions and change-event firing, no HA/DB dependency

### [] Phase 6 — Aerie → kiosk push (SSE)

- [ ] Add a controller endpoint `GET /api/motion-events/stream` (`text/event-stream`), subscribing to `IMotionEventDispatcher`'s change event for the connection's lifetime and writing an SSE `data:` line per change
- [ ] Unsubscribe cleanly on `HttpContext.RequestAborted`
- [ ] Manual smoke test with `curl -N` against the running dev API to confirm events flow before wiring up the frontend

### [] Phase 7 — Video stream proxy

- [ ] Confirm what the actual cameras/HA setup support (HA `stream` integration HLS vs. go2rtc WebRTC) by prototyping directly against one real camera entity — **flag back if the real capabilities push this toward a heavier lift than expected**, since this is genuinely new territory for the repo
- [ ] **Evaluate the camera-direct route recommended in Hardware Selection §"Impact on Phase 7" before building the HA-proxy version.** Verify the Reolink sub-stream RTSP URL plays directly (`ffplay rtsp://user:pass@<ip>:554/h264Preview_01_sub`), then stand up a **standalone go2rtc** on the Windows prod box and confirm it serves that stream over plain HTTP. HA's bundled go2rtc binds 11984 and isn't exposed by default, so proxying through HA is the harder path.
- [ ] If the sidecar route holds up: point HA's own integration at the same instance (`go2rtc: url:`) so both systems share one stream source, and decide where the RTSP URL/credentials live (they are *not* the `CameraFeed` `HaEntityId` — that stays the identity key, so this needs a home: config, a new channel, or device metadata). **Flag the choice back before implementing.**
- [ ] Add a `CameraController` endpoint (e.g. `GET /api/devices/{id}/channels/{channelId}/camera/stream`) resolving the `CameraFeed` channel's `HaEntityId`, fetching the stream (from the go2rtc sidecar if the above holds, otherwise HA-side playlist/segments or WebRTC negotiation via `IHomeAssistantConnectionManager`'s host/port/token), and proxying it back
- [ ] Confirm go2rtc + any ffmpeg dependency actually runs as a service on Windows prod (see `project_windows_prod_servers`) — not just on the dev Mac
- [ ] If proxying HLS, extract playlist-URL-rewriting (so segment URLs route back through this endpoint) into a pure, unit-testable function, same convention as `ChannelValueExtractor`
- [ ] Unit test: the playlist-rewriting function
- [ ] Manual test: confirm the proxied stream actually plays (e.g. `ffplay` or a bare `<video>` tag) against a real camera before wiring the kiosk modal to it

### [] Phase 8 — Kiosk dashboard modal (frontend)

- [ ] Port the `Modal.tsx` pattern (`apps/admin/src/components/Modal.tsx`) into a new `apps/dashboard/src/components/CameraFeedModal.tsx`, plus matching `.modal-overlay`/`.modal-panel` CSS in dashboard's `theme.css` (doesn't exist there yet — dashboard has no modal today)
- [ ] If the stream is HLS, add `hls.js` as a dashboard dependency (native `<video>` doesn't support HLS outside Safari) — **flag this new frontend dependency back given the lean-build ask**. Likely avoidable: if Phase 7 lands on the go2rtc sidecar, its MSE/WebRTC output plays in a plain `<video>` with a small inline shim and no npm dependency. Re-check once Phase 7 is settled.
- [ ] In `App.tsx`, subscribe to `/api/motion-events/stream` via `EventSource` on mount; track which camera device (if any) currently has active motion
- [ ] Render `CameraFeedModal` when a camera's motion is active, sourcing video from the Phase 7 proxy endpoint; close on motion-inactive or the modal's X button
- [ ] Build + lint the dashboard app; in-browser verification of the live feed/modal is on you as usual, not claimed here as tested

### [] Phase 9 — Docs

- [ ] Add `docs/camera-devices-architecture.md` mirroring `device-architecture.md`'s phased structure, covering the schema additions, the WS listener, the dispatch seam, the SSE stream, and the video-proxy mechanism actually chosen in Phase 7
