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
2. **H.265 main stream won't play in a browser.** On the 4K/12MP models the main ("Clear") stream is H.265; the sub-stream ("Fluent") is H.264. The kiosk modal wants the sub-stream anyway — lower latency, no transcode, and it removes any pressure to transcode 4K on a cluster node.
3. **The `_motion` sensor is noisy** (trees, rain, headlights, shadows). v1 opens a modal on every transition, which gets annoying fast. Reolink also exposes `binary_sensor.*_person` / `_vehicle` / `_pet` from on-camera AI, with far fewer false positives. → Phase 2 item.
4. **Avoid dual-lens models** (Duo 3, TrackMix): multiple lenses/channels under one HA device, and `CameraChannelBuilder` takes `FirstOrDefault` for both feed and motion, so half the camera would import silently. Single-lens keeps the one-device-one-feed assumption true.

### Alternatives rejected

- **UniFi Protect** — best-in-class when working, but has repeatedly broken HA motion events on firmware updates (core issues in Dec 2025 and Feb 2026). Wrong risk profile for a system whose whole value is event-driven, and needs a Protect console.
- **Amcrest / Dahua** — good ONVIF hardware and the community Dahua integration passes motion events well, but it's HACS, not core. Don't want the event path depending on a third-party integration.
- **Axis** — core integration, extremely reliable, ONVIF-native, but ~4–6x the price per camera.

### Impact on Phase 7 (video)

Phase 7 currently assumes the stream is fetched *through* HA. That's the hard path: HA bundles go2rtc (since 2024.11) but binds its API to port **11984** and doesn't expose it by default — the docs only describe opening it via `debug_ui`, explicitly flagged as debug-only.

Cleaner with Reolink: **HA for events, camera-direct for video.** Reolink publishes stable RTSP URLs (`rtsp://user:pass@<ip>:554/h264Preview_01_sub` for the H.264 sub-stream), and a **standalone go2rtc** converts RTSP → HLS/MSE/WebRTC over plain HTTP that `CameraController` proxies. (Written when prod was a single Windows host, which is where "sidecar on the Windows box" came from; Phase 7 landed it as a Deployment in the k3s cluster instead.) HA can be pointed at that same instance (`go2rtc: url: http://...:1984`), so both systems share one stream source instead of two. `CameraFeed`'s `HaEntityId` stays the identity/discovery key — no schema change — while the bytes come from a documented, stable URL rather than HA internals. This likely also removes the `hls.js` dependency flagged in Phase 8.

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

### [x] Phase 5 — Motion dispatch (the hardcoded-for-v1 seam)

- [x] Add `IMotionEventDispatcher` (`Services/DeviceMapping/MotionEventDispatcher.cs`) holding per-device motion-active state in memory and raising a C# event on change — landed as a `HashSet<Guid>` of *active* devices under a `Lock` rather than the planned `ConcurrentDictionary<Guid, bool>`: membership is the state, so a no-op set is detected by the set operation itself, and the lock makes mutate-and-raise one step. Split apart, two transitions on one device can be applied in order and announced out of order, which leaves a kiosk showing a modal for motion that already ended. The lock is never contended today — one receive loop is the only caller.
- [x] `HomeAssistantEventListener` calls `SetMotionState(deviceId, isActive)` on each transition (the Phase 4 log line stays; it's the only trace of a motion event that survives a restart)
- [x] Register `IMotionEventDispatcher` as a singleton in `Program.cs`
- [x] Unit test: state transitions and change-event firing, no HA/DB dependency — `MotionEventDispatcherTests`, 11 tests
- [x] **Also added, not in the original plan:** `ActiveDeviceIds` snapshot on the interface. Without it the stored state has no reader, and a kiosk connecting mid-motion would show nothing until the *next* transition. Phase 6 subscribes first and reads the snapshot second — since a change carries absolute state rather than a toggle, that ordering can duplicate a change but can never miss one.
- [x] **Also added, not in the original plan:** per-subscriber exception isolation in the raise loop. Every subscriber is a kiosk connection, and one that died between the raise and its own cleanup would otherwise blind every other kiosk on the replica for that change.
- [x] **Also added, not in the original plan:** `HomeAssistantEventListener.ClearMotionState` drops every device to no-motion when the socket goes, in a `finally` around the receive loop. Same reasoning as the parser's treatment of `unavailable` — once we can't see, we stop claiming there is motion. Without it, a device left active when the connection broke is stuck: the `off` that ended it arrives during the outage, so the next real `on` is no change from the stale state, nothing is announced, and the modal is pinned open for good.

### [x] Phase 6 — Aerie → kiosk push (SSE)

- [x] Add a controller endpoint `GET /api/motion-events/stream` (`text/event-stream`), subscribing to `IMotionEventDispatcher`'s change event for the connection's lifetime and writing an SSE `data:` line per change — `MotionEventsController`, with the sequence itself in `MotionEventStream` so the ordering rules are testable without an HTTP connection. Framing is .NET 10's built-in `TypedResults.ServerSentEvents`, so no hand-rolled `data:`/flush code; each change goes out as `{"deviceId":"…","isActive":true|false}`
- [x] Unsubscribe cleanly on `HttpContext.RequestAborted` — a `finally` around the whole sequence, so it also covers the enumerator being disposed for any other reason
- [x] Manual smoke test with `curl -N` against the running dev API to confirm events flow — `text/event-stream`, `Cache-Control: no-cache,no-store`, chunked, the opening frame at 0.00s and the next heartbeat at 20.00s, and no exception logged when curl walks away. **Motion `data:` frames themselves were not exercised live**: this machine can't reach HA (host/port/token live in prod `SiteSettings`), so nothing can drive a transition end-to-end here. The frame body is pinned by a unit test on the serializer the SSE result actually uses instead.
- **Handlers hand off to a channel rather than writing the response.** The dispatcher raises on the WebSocket listener's receive loop, and that loop is shared by every device. If the response write happened in the handler, one kiosk on a stalled connection would stop motion for the whole replica; the channel keeps the handler to a `TryWrite`. `AllowSynchronousContinuations` stays off for the same reason — a synchronous continuation would drag the response write back onto that thread.
- **Also added, not in the original plan:** a 20s heartbeat (its own `event: heartbeat`, which a browser `EventSource` ignores by default). A kiosk that loses power leaves a request that is never aborted and a handler that is never unsubscribed — nothing notices until a write is attempted and fails, and motion can be quiet for days. Same reasoning as the WebSocket keepalive in Phase 4, and it doubles as protection against a proxy closing an idle stream.
- **Also added, not in the original plan:** the opening frame is a heartbeat. An SSE response writes no headers until its first frame, so a kiosk connecting on a quiet night would otherwise wait up to 20s for `EventSource.onopen` — indistinguishable from a broken API. Confirmed by the smoke test: headers at 0.00s.
- **Snapshot before that heartbeat, both on the first `MoveNextAsync`.** Reading `ActiveDeviceIds` in the same resumption as the subscribe is what keeps the Phase 5 ordering guarantee tight; yielding first and reading later would open a window where a device could appear in the snapshot *and* have its change queued from before the read.

### [x] Phase 7 — Video stream proxy

Landed on the camera-direct route Hardware Selection recommended, and it held
up: **HA for events, go2rtc for video.** What could be settled without a camera
turned out to be almost all of it — go2rtc itself is testable with a synthetic
stream, so the protocol, the relay and the deployment are verified rather than
assumed. The RTSP hop from camera to go2rtc is the one link no amount of local
testing reaches; that and everything downstream of it is Phase 10.

- [~] ~~Confirm what the actual cameras/HA setup support by prototyping against one real camera entity~~ — **deferred to Phase 10** (no camera on the LAN yet). The question it was asked to answer — HLS through HA vs. go2rtc — was settled without it, by the reasoning in Hardware Selection plus a local go2rtc: HA's bundled go2rtc binds 11984 and is documented as debug-only, while a standalone one is a documented API.
- [x] **Evaluated the camera-direct route before building anything HA-side**, and took it. `ffplay` against a camera moves to Phase 10, but go2rtc's own half was verified in full by running `alexxit/go2rtc:1.9.14` under Docker against a synthetic source (`ffmpeg:virtual?video=testsrc`) — which turns out to be a complete stand-in for a camera as far as everything in this repo is concerned.
- [x] **Decided where the RTSP URL and credentials live: in go2rtc, and nowhere else.** This was flagged for a decision and the answer is the leanest of the three offered — not config, not a new channel, not device metadata. go2rtc's own streams file names each stream after the `camera.*` entity id it corresponds to, so the `CameraFeed` channel's `HaEntityId` is *already* the mapping and nothing new is stored. What that buys is the part worth having: no RTSP URL and no camera password ever enters Aerie's database, its API, or its git history. The streams file arrives as the `go2rtc-streams` Secret from the parameter store (`docs/secrets-architecture.md`), mounted `optional` so the cluster is healthy with zero cameras configured.
- [x] Added the endpoint — `GET /api/devices/{deviceId}/camera/stream`, a **WebSocket relay** (`CameraController` + `CameraStreamRelay`). By device id rather than the planned `.../channels/{channelId}/...`: a device has exactly one `CameraFeed` channel, and the kiosk gets a bare device id from the SSE stream, so resolving the channel server-side saves the client a round trip at the one moment latency is the whole point.
- [x] **Transport is MSE over WebSocket, not HLS** — flagged back and chosen. The relay is byte-transparent and knows nothing of go2rtc's protocol, which is what keeps a second implementation of it out of the path of every frame. Sub-second latency, and it is what removes the `hls.js` dependency Phase 8 flagged.
- [~] ~~Confirm go2rtc + ffmpeg runs as a service on Windows prod~~ — **superseded.** Prod has been a k3s cluster since the swarm plan's Phase 7 cutover; `docs/delivery-architecture.md` says outright that any surviving reference to the Windows Compose host is stale. go2rtc is a Deployment/Service/ConfigMap in `charts/aerie` (single replica, `Recreate`, because every replica is an independent RTSP *client* and Reolink caps concurrent sub-stream connections in the single digits).
- [~] ~~Extract playlist-URL-rewriting into a pure, unit-testable function~~ / ~~unit test it~~ — **not applicable, and replaced.** There is no playlist; MSE has no manifest to rewrite. The pure, testable piece is `CameraStreamTarget` (entity id + base address → go2rtc URL) with **18 tests**, including the case that motivated writing it by hand: `Uri`'s own relative resolution silently drops the last path segment of a base with no trailing slash, so a go2rtc mounted under `/go2rtc` would have been proxied from the root.
- [x] Manual test: **the proxied stream plays, verified end-to-end through the running API** — a real `Camera` device in the local dev DB, `CameraController` relaying to a local go2rtc, a WebSocket client on the other end. The control exchange and 147 binary frames / 234 KB of fMP4 arrived intact in six seconds, message boundaries preserved, with clean open/close log lines and no unhandled exceptions. Against a *real camera* rather than a synthetic source: Phase 10.

**Verified about go2rtc 1.9.14 by running it, not by reading its docs** — each of these is load-bearing somewhere in the chart or the client:

- Repeated `-config` arguments **merge**, and a path that does not exist is skipped silently. This is the whole reason the streams Secret can be `optional` and the cluster can be healthy with no cameras.
- `rtsp: listen: ""` breaks **every** `ffmpeg:` source with `streams: exec: rtsp module disabled` — ffmpeg pushes back into go2rtc over its own RTSP listener. So RTSP binds loopback rather than being disabled, which keeps a future transcode (an H.265 main stream) possible while exposing nothing.
- It runs non-root (uid 65532) with a read-only root filesystem, needing only a writable `/tmp`.
- A stream may be named for an HA entity id, dots included — which is what makes `HaEntityId` usable as the stream name.
- `/api/streams` answers 200 with `{}` when nothing is configured, so it can be the readiness probe without a down camera taking the pod with it.
- The MSE reply carries the codec the **stream** actually has, not the one requested: asking for `avc1.640028` returned `avc1.640029`. Handing `addSourceBuffer` the requested string instead would reject every fragment that followed.

### [x] Phase 8 — Kiosk dashboard modal (frontend)

- [~] ~~Port the `Modal.tsx` pattern from `apps/admin`~~ — **deviated on purpose.** The dashboard already has an overlay idiom of its own (`GatherOverlay`, `.hf-gather-overlay`): full-screen, fixed, mounted inside `.hf-page` so the circadian custom properties resolve, with one 56px touch target. That is the right shape for a portrait tablet on a wall; admin's centred dialog with a small ✕ is the right shape for a desktop browser. `CameraFeedModal` follows the local one.
- [x] **No `hls.js`, and no new frontend dependency at all** — the flagged concern is resolved rather than accepted. Phase 7's MSE-over-WebSocket transport hands over fragmented MP4, which `MediaSource` takes directly.
- [x] `App.tsx` subscribes to `/api/motion-events/stream` via `EventSource` on mount (`useMotionEvents`), and tracks which camera has active motion.
- [x] `CameraFeedModal` renders when a camera's motion is active, sourced from the Phase 7 relay; closes on motion-inactive or the ✕.
- [x] Build + lint + tests pass. In-browser verification is the user's, as always, and is not claimed here.

**Also added, not in the original plan** — each of these is a failure the lean version would have shipped with:

- **Dismissal is per *event*, not per camera** (`lib/motionEvents.ts`, a pure reducer, 13 tests). Closing the modal has to stop being in effect when the motion ends, or the ✕ silently mutes that camera forever. The same reducer makes a repeated `isActive: true` frame a no-op — the SSE contract explicitly permits one, and without this it would reopen a modal the user had just closed.
- **SourceBuffer trimming and drift correction** (`lib/cameraStream.ts`, 23 tests). A `MediaSource` has a finite quota, so a modal open for the length of a long motion event ends in `QuotaExceededError` and a dead feed; and a kiosk tab that was backgrounded between events comes back seconds behind, showing footage of what already happened. Both are silent without the fix, and both are certain to happen on a wall display.
- **The stream dropping clears all motion**, mirroring what `HomeAssistantEventListener` does when it loses HA — once we cannot see, we stop claiming there is something to look at. Otherwise the end-of-motion frame arrives during the outage and the modal is pinned open for good.
- **The camera overlay holds the kiosk lifecycle**, alongside Gather. A deploy reload firing while someone is watching who is at the door is the same bug as one firing mid-Gather-entry.
- **The camera name is fetched separately and never awaited.** The video socket opens on mount regardless, so a slow lookup delays a label, never the picture.

### [] Phase 9 — First-camera bring-up

Everything here needs a camera on the LAN, which is the only reason it isn't
done. Phases 7 and 8 are built and verified against a synthetic go2rtc stream;
what remains is the RTSP hop from camera to go2rtc, and the handful of
predictions this plan made about what a Reolink actually publishes to Home
Assistant. Each item below is written to name **what it would falsify** if it
came out the other way, so a surprise points at the code it invalidates rather
than just failing.

Ordered so the cheapest checks come first and each one narrows what a later
failure could mean.

**The camera itself** — nothing else can be diagnosed until this is known good. Done 2026-08-23 with `ffmpeg` from the dev Mac (no `ffplay` on this machine; decoding to `-f null` proves the same things a window would).

- [x] `rtsp://<user>:<pass>@<ip>:554/h264Preview_01_sub` plays — **H.264 High, 640x480, 10 fps**, plus an AAC 16 kHz mono track. The URL shape Hardware Selection assumed is exactly right, credentials and all, so the streams file is the only place it appears.
- [x] The main stream is **H.264 High, 2560x1920, 25 fps — not H.265**, so Hardware Selection §2's prediction is wrong for this model (the path is `h264Preview_01_main`, which says so in its own name). As the item anticipated, this changes nothing: the sub-stream is still the kiosk's stream for latency and for not decoding 5MP on a wall tablet. What it does change is that the H.265 argument is no longer load-bearing anywhere — the transcode the loopback RTSP listener was kept alive for is not needed by *this* camera, only by a future one.
- [x] Sub-stream resolution and frame rate noted, and `go2rtc.resources` in `charts/aerie/values.yaml` is now a measurement with the numbers written into the comment.
- [x] **The dominant latency term is the camera's keyframe interval, and it is upstream of everything Aerie does.** The sub-stream emits an IDR every 40 frames — at 10 fps that is exactly 4.0 s, measured over 20 s of `showinfo` output. No viewer can be shown anything until one arrives, so opening a feed costs a uniform 0–4 s wait, ~2 s on average, before the first frame renders. Measured through go2rtc against this camera: first video fragment 3.82 s, 2.37 s, 1.29 s on three attempts — the spread is the GOP phase, not jitter in the path. Two levers, both on the camera rather than in this repo: shorten the I-frame interval in the Reolink web UI, or raise the sub-stream frame rate (the GOP is 40 *frames*, so 20 fps halves the wait). Worth doing before judging the kiosk feel.
- [x] **Keeping a stream warm is not the fix.** A second viewer joining a stream go2rtc was already pulling still waited 1.29 s for its own first fragment: go2rtc does not replay a cached keyframe to a new consumer. So the warm-stream mitigation the latency item below proposes buys only the RTSP connect (a few hundred ms), not the keyframe wait. That leaves the camera's GOP as the only thing worth tuning.

**HA entity shape** — Phase 2 encoded three predictions about Reolink; this is where they are checked. Checked 2026-08-23 against the first camera on the LAN (HA device `6bec71b762f35cdf12246606b5a3ecdb`), from its published entity list alone — everything below needed the list, not the camera.

- [~] ~~Confirm five `camera.*` entities appear under one device~~ — **one**, `camera.innit_fluent`. The other four profiles ship disabled, and a disabled entity has no state object, so `GroupingTemplate`'s `{% for s in states %}` never sees them: discovery is choosing from a single candidate, not from five. The `_fluent` preference in `InferKind` is therefore inert today and stays as insurance for the day someone enables "Clear" in HA, which is exactly the case it was written for. That the *stream behind* `_fluent` is the H.264 sub-stream is a claim about the camera, not the entity list, and is still open above.
- [x] Confirm `binary_sensor.*_person` exists alongside `*_motion` — both present, and `CameraChannelBuilder` picks `_person`. The AI sensors are `_person`/`_vehicle`/`_animal`; Hardware Selection §3 guessed `_pet` for the third, which is wrong and costs nothing, since only `_person` and `_motion` are named in the preference list.
- [x] **Falsified, and fixed: the group imported as a `SmartSwitch`, not a `Camera`.** A Reolink's HA device carries six `switch.*` entities — record, record audio, infrared lights, FTP upload, email on event, push notifications — and `InferKind` checked `switch.*` before `camera.*`, so the anchor came out as `switch.innit_email_on_event` and no `CameraFeed` channel was ever built. Nothing downstream of Phase 2 could run, and the failure is silent: the device imports, it just imports as the wrong kind. This is the same trap the `media_player` branch already had a comment about (a Sonos and its `switch.*` tuning siblings) — cameras were simply not thought of as a device that carries switches. The camera branch now sits ahead of both `switch.*` and `light.*`; `light.*` matters for the same reason on the spotlight models (RLC-811A, RLC-1224A), which publish `light.*_floodlight`. Precedence is now climate > media_player > camera > switch > light, pinned by tests over this camera's verbatim entity list.
- [x] Run discovery, import the camera, and confirm the device lands as `Camera` with a `CameraFeed` channel and a `MotionState` channel pointing at `binary_sensor.innit_person`. Predicted by unit test; not yet run against the live instance, which is the only thing that also exercises `GroupingTemplate` and the admin import flow.

**go2rtc streams** — the one design assumption in Phase 7 that a camera can overturn.

- [x] **Stream naming settled, and verified against the camera** — the entry is `camera.innit_fluent: rtsp://<user>:<pass>@<ip>:554/h264Preview_01_sub`, written by hand, and the assumption holds by construction as intended. Verified 2026-08-23 by running `alexxit/go2rtc:1.9.14` locally with this chart's own args and securityContext, pointed at the camera: `/api/streams` lists the stream under its entity-id name, `/api/frame.jpeg?src=camera.innit_fluent` returns a 640x480 JPEG of what the camera is looking at, and the kiosk's own control exchange — the one in `lib/cameraStream.ts`, spoken verbatim by a probe script — is answered `{"type":"mse","value":"video/mp4; codecs=\"avc1.640029\""}` followed by fMP4. Two things worth recording from that exchange: the returned codec **is** one of `CANDIDATE_CODECS`, so the browser filter cannot empty the offer for this camera; and because the client offers video codecs only, go2rtc drops the camera's AAC track rather than muxing it — the kiosk feed is silent, which for a wall display is the wanted behaviour rather than a gap. HA's go2rtc integration is not pointed at this instance, so nothing auto-registers a competing name. `CameraStreamTarget` assumes **the go2rtc stream name equals the `CameraFeed` channel's `HaEntityId`**. Writing the streams file by hand makes that true by construction. If instead HA's own go2rtc integration is pointed at this instance and auto-registers streams under different names, either add an alias stream in the config or revisit that assumption — it is the single point where the two systems' vocabularies have to agree.
- [x] Add a `cameras/go2rtc-streams` entry to `scripts/secrets/parameters.json` and regenerate: `pwsh ./scripts/secrets/New-ExternalSecrets.ps1`. Done, along with the `GO2RTC_STREAMS` mapping in `provision-2-seed-secrets.yml` that the seed run needs and the row in `docs/secrets-architecture.md`. It must be `required: true` — `parameters.json` documents that an optional parameter may not carry a `kubernetes` block, because an ExternalSecret pointing at something never seeded sits in `SecretSyncError` and takes its Kustomization's Ready gate down with it. That rule is exactly why this step waits for a camera: until there is a stream to name, there is no value to seed.
- [x] The `secretKey` must be **`streams.yaml`**, not a kebab-case name. `go2rtc-deployment.yaml` mounts the Secret as a *directory* at `/streams`, so the key becomes the filename that `-config /streams/streams.yaml` reads. The generator already allowed the dot (its `SecretKey` charset is Kubernetes', not the kebab-case one the parameter *keys* use), so this cost nothing. What it did cost: this is the first parameter whose `description` runs to more than one line, and `New-ExternalSecrets.ps1` was prefixing only the first line with `# ` — the rest landed in the manifest as bare YAML and the file no longer parsed. Fixed at the source with a `Format-Comment` helper that prefixes every line, so a multi-line description is now a thing the map is allowed to contain rather than a way to render an invalid document.
- [ ] Seed `GO2RTC_STREAMS` as a GitHub **secret** (it contains camera passwords, so not a variable) and run the seed workflow.
- [ ] `kubectl rollout restart deploy/go2rtc -n aerie` after the Secret lands. go2rtc reads its config once at startup and never watches the files; the deployment's `checksum/config` annotation covers the ConfigMap only, because Helm cannot see a Secret's contents to hash them.
- [~] Confirm pod-network egress reaches the camera's RTSP port from a cluster node. **This is the item that could force a design change** — if the CNI or a firewall rule blocks pod → LAN, go2rtc has to move off-cluster or onto host networking, and it is the only untested link that would. **Now a dispatch rather than a hand-run `kubectl`:** `scripts/k3s/Test-Cameras.ps1`, workflow *Verify: Cameras*, asks it as one check among nine — a JPEG frame per stream, fetched through the API server's service proxy, which can only exist if a pod opened an RTSP session to that camera. Still needs the Secret seeded and a camera on the LAN, so it is written and unrun.

**End to end.**

- [x] **The cluster half of everything below is now a workflow.** `scripts/k3s/Test-Cameras.ps1` and *Verify: Cameras* assert the nine facts a machine can check — the ExternalSecret is Ready, the Secret carries a non-empty `streams.yaml`, every stream name is a `camera.*` entity id (which is what `CameraStreamTarget` asks go2rtc for, and go2rtc accepts any string, so nothing else would object), the Deployment is 1/1 with exactly one pod, the Service answers, and a JPEG frame comes back per stream. Written against a stub node rather than a real one, which is how the one bug in it was found: `tr -d '[:space:]'` on the stream-name filter deletes newlines too, so two cameras came out as one concatenated name. It is `[:blank:]` now. **Nothing in it prints a stream source** — `GET /api/streams` returns every camera's password in the response body, and this output is a CI run log, so that call is made to `/dev/null` and names come from the Secret through a filter that keeps only the text left of the first colon. The four items below stay manual on purpose and the gate prints them on every run, pass or fail.

- [~] From inside the cluster, `curl http://go2rtc:1984/api/frame.jpeg?src=<entity id>` returns a JPEG. **Now the core of the *Verify: Cameras* gate**, one check per stream, so it is re-asked every time a camera is added rather than once at bring-up. **Done off-cluster instead, and it passed**: the same image, from the same go2rtc version with the same config, fetched from the dev Mac. That settles the camera half of the split — the remaining question is not "does the camera work" but "can a pod reach it", which is the egress item above. This is the clean split point: a frame here means the camera half works and anything still broken is Aerie's; no frame means the camera half is, and none of Phase 7 or 8 is implicated.
- [ ] Open the kiosk dashboard, walk in front of the camera, confirm the modal opens with live video and closes when the motion ends. Then confirm the ✕ closes it, and that the *next* motion event reopens it — that is the per-event dismissal rule, and it is the one piece of Phase 8's behaviour that a unit test can assert but not prove.
- [ ] **Confirm MSE works in the kiosk's Android WebView specifically.** Everything in Phase 8 rests on `MediaSource`, and the kiosk is a WebView rather than Chrome proper. `supportedCodecs()` already reports the empty case as "this display can't play the feed" rather than failing obscurely, so a bad answer here is visible rather than silent — but it would mean rethinking the transport.
- [ ] Measure motion → first frame. Partly answered before the chain exists: the camera contributes a 0–4 s keyframe wait all by itself (above), which is larger than the whole rest of the path is likely to be, and go2rtc's cold RTSP session — the suspect this item named as the fixable one — turns out to be the small term. Tune the camera's GOP first, then measure, or the measurement is mostly of the camera. HA's push latency and the SSE hop are still unmeasured and still need the live instance.
- [x] Replaced `go2rtc.resources` in `charts/aerie/values.yaml` with measured figures. The numbers themselves did not move — idle 11.7 MiB, one viewer 12 MiB / 0.9% of a core, five viewers 21.4 MiB / 1.6%, against a request of 25m and 64Mi — so the estimate was sound and the comment now says why rather than apologising for itself. The useful shape is a ~12 MiB floor plus ~2 MiB and ~1.5m CPU per viewer; the request has to cover every camera's producer, not one.

**Open, and deliberately not decided yet.**

- [ ] Whether to point HA's own integration at this go2rtc (`go2rtc: url:`), which the original Phase 7 assumed would be free. **It is not, any more.** The Service is `ClusterIP` with no Ingress and HA runs outside the cluster, so this would mean exposing go2rtc on a NodePort or Ingress — putting camera streams on a listener anything on the LAN can reach, to save one RTSP connection per camera. Worth revisiting only if the cameras turn out to be stingy with concurrent connections. **Leaning firmly to no, on a finding from bring-up:** go2rtc's API is unauthenticated by default and `GET /api/streams` returns each stream's producer URL *verbatim*, camera password included. Putting that on a NodePort or an Ingress publishes the camera credential to everything that can reach the listener. Closing this as "no" would also make one Phase 7 decision cheaper than it looks — the RTSP loopback listener exists for a transcode, and this camera's main stream turns out to be H.264 anyway.


### [] Phase 11 — Camera credentials in Aerie, not in git

**Supersedes the streams-file half of Phase 7 and the seeding half of Phase 9.**
Phase 7 put every camera's RTSP URL in a `go2rtc-streams` Secret, seeded from a
GitHub secret through Provision 2, and Phase 9 wired that up. It works, and it
is the wrong shape for the product: adding a camera means editing a GitHub
secret, dispatching a workflow and restarting a pod, which is three systems and
an operator with repository access for something a household should do from the
admin UI. Aerie ships to other operators (`docs/ethos.md`), and "add a camera"
has to be a form.

So the credential moves into Aerie's own database, set from the devices admin
UI, and go2rtc stops holding any camera configuration at all.

**The trade, stated plainly.** A camera password in Aerie's database is
protected by obfuscation, not encryption — the same `SecretObfuscator` the HA
token and the calendar refresh tokens already use. That is weaker than a
Kubernetes Secret backed by SSM SecureString, which is what this replaces. It
is a deliberate, temporary trade made with the values in hand: these cameras
point at trees. What this phase owes the future is that the *upgrade* is cheap,
which is why the protector becomes versioned here rather than when it matters.

#### What was verified about go2rtc, not assumed

Run against `alexxit/go2rtc:1.9.14` under Docker on 2026-08-24, the same
standard `go2rtc-deployment.yaml` holds itself to.

- `PUT /api/streams?name=<name>&src=<url>` registers a stream at runtime, and a
  second PUT under the same name **replaces** its producer. `DELETE
  /api/streams?src=<name>` removes it. So a running go2rtc can be told about a
  camera without a config file and without a restart — which is the whole
  premise of this phase.
- **PUT tries to persist to its *first* `-config` path**, and answers **400**
  when it cannot: `open /config/go2rtc.yaml: read-only file system`. The
  registry is still updated — the stream works — so the failure is a lie in
  both directions, and "400 means it worked" is not something to build on.
  With a *writable* first `-config` on an `emptyDir` and the ConfigMap second,
  PUT answers **200**, the ConfigMap's `api:`/`rtsp:`/`webrtc:` blocks still
  apply, and go2rtc writes the streams into the emptyDir. That is the
  arrangement: **Aerie is the source of truth and go2rtc's copy is a cache**
  that dies with the pod, which is exactly the lifetime it should have.
- go2rtc will also take a **raw RTSP URL as `src`** — `/api/ws?src=rtsp://...`
  upgrades, no registration needed. Rejected: it registers the stream under
  *the URL as its name*, so the password lands in the stream registry and in
  any log line that names a stream. Named streams keep the password in the
  producer field, which is one place instead of everywhere.

#### The pieces

- [ ] **A versioned secret envelope.** `SecretObfuscator`'s output becomes
  `v1:<payload>`, and the reader dispatches on the prefix. Base64 contains no
  colon, so a stored legacy value cannot collide with a scheme tag — which is
  what makes the discriminator free. v1 *is* the existing XOR, so converting
  every stored secret is prepending four characters, not re-encrypting
  anything. Adding real crypto later is a `v2:` scheme plus a lazy re-protect
  on write, with mixed rows readable throughout.
- [ ] **Convert the existing secrets in the same pass** — the four
  `SiteSettings` keys and the calendar OAuth tokens — so there is one format in
  the system rather than two. A data migration, since the bytes do not change.
- [ ] **Per-camera connection settings**, on their own table keyed by device:
  host, port, stream path, and the protected username and password. The
  protected-column-on-the-owning-table shape is what `EfCalendarAccount`
  already does; the unification this phase buys is in the *format*, not in
  moving every secret into one table.
- [ ] **The host comes from Home Assistant first.** The discovery template
  already calls `device_attr()`, so it gains `configuration_url` — which for a
  Reolink is `http://<ip>`, kept current by the integration's own DHCP
  handling. The admin field overrides it and is what gets used when HA has
  nothing. This is the answer to "avoid hardcoding a static IP": Aerie asks the
  system that already tracks the camera, and only falls back to being told.
- [ ] **Registration is lazy.** `CameraController` PUTs the stream immediately
  before it opens the relay socket. No reconciler, no startup pass, and a
  go2rtc restart self-heals on the next viewer — at the cost of one HTTP round
  trip per modal open, against a keyframe wait measured in seconds.
- [ ] **Remove the file path entirely**: the `cameras/go2rtc-streams`
  parameter, its ExternalSecret, the `GO2RTC_STREAMS` mapping in Provision 2,
  and the streams volume. One way to configure a camera.

### [] Phase 10 — Docs

- [ ] Add `docs/camera-devices-architecture.md` mirroring `device-architecture.md`'s phased structure, covering the schema additions, the WS listener, the dispatch seam, the SSE stream, and the video-proxy mechanism actually chosen in Phase 7
- [ ] Write it after Phase 10, not before — the mechanism is settled, but several numbers in it (the stream naming, the resource figures, the measured latency) are Phase 10's output, and a doc written now would need rewriting with them
