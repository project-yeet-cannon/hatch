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

## Implementation Plan

Follows the existing `Device`/`DeviceChannel` model (`src/Aerie.Api/Ef/DeviceMapping.cs`) and discovery pipeline (`src/Aerie.Api/Services/DeviceMapping/DiscoveryService.cs`) that Mysa/power-switches used — see `docs/device-architecture.md` for the phase-doc convention this mirrors.

### Phase 1 — Domain model

- [x] Add `Camera` to `DeviceKind` (`Ef/DeviceMapping.cs:33`)
- [x] Add `CameraFeed` and `MotionState` to `DeviceChannelMetric` (`Ef/DeviceMapping.cs:61`) — `CameraFeed` channel's `HaEntityId` is the `camera.*` entity; `MotionState` channel's `HaEntityId` is the sibling `binary_sensor.*_motion` entity, `HaAttribute` null, bare on/off state (fits `EfStateChange`/`ChannelValueExtractor` as-is, no extractor changes needed)
- [x] Confirm no EF migration is actually required (new enum members, same underlying int column, no CHECK constraint) — run `dotnet ef migrations add CameraDevices` and check the generated migration is empty/only whitespace; delete it if so

### Phase 2 — Discovery/import

- [x] Add a `camera.*` branch to `DiscoveryService.BuildSuggestion` (`Services/DeviceMapping/DiscoveryService.cs:58`), before the sensor fallback
- [x] Add a `CameraChannels(cameraEntityId, entityIds)` helper mirroring `ThermostatChannels`/`SwitchChannels`: always emits the `CameraFeed` channel, plus a `MotionState` channel if a sibling `binary_sensor.*_motion` entity is present in the group — landed as `CameraChannelBuilder.cs`, matching `LightChannelBuilder`'s standalone-class shape
- [x] Extract `BuildSuggestion`'s kind-inference chain into a pure function of `IReadOnlyList<string> entityIds` (it isn't today) so the new branch is unit-testable — `DiscoveryService` currently has zero test coverage because `TemplateClient` is sealed; this sidesteps that without adding a wrapper interface — landed as `DiscoveryService.InferKind` + `KindMatch`
- [x] Unit test: camera+motion grouping produces the right `DeviceKind`/channels; camera-without-motion-sibling still imports with just `CameraFeed`

### Phase 3 — Admin UI

- [x] Add `'Camera'` to the `DeviceKind` union and `'CameraFeed'`/`'MotionState'` to `DeviceChannelMetric` in `apps/admin/src/types.ts`
- [x] Add a `Camera` option to the device-kind `<select>` in both `DevicesPage.tsx` and `DiscoveryPage.tsx` (the latter was never updated for `SmartSwitch` — don't repeat that gap here)

### Phase 4 — HA WebSocket event listener

- [ ] Prototype the HA WS auth + subscribe handshake by hand against the real instance (e.g. `websocat ws://<host>:<port>/api/websocket`) before writing production code, to confirm `auth_required`/`auth_ok` and `subscribe_events(event_type=state_changed)` payload shapes for the HA version actually running
- [ ] Add a `BackgroundService` (e.g. `Services/DeviceMapping/HomeAssistantEventListener.cs`) using raw `System.Net.WebSockets.ClientWebSocket` (HADotNet has no WS client) against `ws://{host}:{port}/api/websocket`, built from `IHomeAssistantConnectionManager`'s host/port/token
- [ ] Implement the auth handshake + `subscribe_events` for `state_changed`
- [ ] On each event, match `entity_id` against `MotionState` channels (query `AerieContext` for `HaEntityId` where `Metric == MotionState`) and extract old→new state
- [ ] Implement reconnect-with-backoff — HA restarts and home-network blips are routine, not exceptional, given prod runs on a home Windows box
- [ ] Register via `builder.Services.AddHostedService<HomeAssistantEventListener>()` in `Program.cs`
- [ ] Unit test: extract "does this raw event represent a motion on/off transition" into a pure function and test it; accept the socket/reconnect plumbing itself stays untested, same precedent as `HomeAssistantStateReader`/`HomeAssistantCommandService` wrapping untestable HA-facing code

### Phase 5 — Motion dispatch (the hardcoded-for-v1 seam)

- [ ] Add `IMotionEventDispatcher` (`Services/DeviceMapping/MotionEventDispatcher.cs`) holding per-device motion-active state in memory (`ConcurrentDictionary<Guid, bool>`) and raising a C# event on change
- [ ] `HomeAssistantEventListener` calls `SetMotionState(deviceId, isActive)` on each transition
- [ ] Register `IMotionEventDispatcher` as a singleton in `Program.cs`
- [ ] Unit test: state transitions and change-event firing, no HA/DB dependency

### Phase 6 — Aerie → kiosk push (SSE)

- [ ] Add a controller endpoint `GET /api/motion-events/stream` (`text/event-stream`), subscribing to `IMotionEventDispatcher`'s change event for the connection's lifetime and writing an SSE `data:` line per change
- [ ] Unsubscribe cleanly on `HttpContext.RequestAborted`
- [ ] Manual smoke test with `curl -N` against the running dev API to confirm events flow before wiring up the frontend

### Phase 7 — Video stream proxy

- [ ] Confirm what the actual cameras/HA setup support (HA `stream` integration HLS vs. go2rtc WebRTC) by prototyping directly against one real camera entity — **flag back if the real capabilities push this toward a heavier lift than expected**, since this is genuinely new territory for the repo
- [ ] Add a `CameraController` endpoint (e.g. `GET /api/devices/{id}/channels/{channelId}/camera/stream`) resolving the `CameraFeed` channel's `HaEntityId`, fetching the HA-side stream (playlist/segments or WebRTC negotiation) via `IHomeAssistantConnectionManager`'s host/port/token, and proxying it back
- [ ] If proxying HLS, extract playlist-URL-rewriting (so segment URLs route back through this endpoint) into a pure, unit-testable function, same convention as `ChannelValueExtractor`
- [ ] Unit test: the playlist-rewriting function
- [ ] Manual test: confirm the proxied stream actually plays (e.g. `ffplay` or a bare `<video>` tag) against a real camera before wiring the kiosk modal to it

### Phase 8 — Kiosk dashboard modal (frontend)

- [ ] Port the `Modal.tsx` pattern (`apps/admin/src/components/Modal.tsx`) into a new `apps/dashboard/src/components/CameraFeedModal.tsx`, plus matching `.modal-overlay`/`.modal-panel` CSS in dashboard's `theme.css` (doesn't exist there yet — dashboard has no modal today)
- [ ] If the stream is HLS, add `hls.js` as a dashboard dependency (native `<video>` doesn't support HLS outside Safari) — **flag this new frontend dependency back given the lean-build ask**
- [ ] In `App.tsx`, subscribe to `/api/motion-events/stream` via `EventSource` on mount; track which camera device (if any) currently has active motion
- [ ] Render `CameraFeedModal` when a camera's motion is active, sourcing video from the Phase 7 proxy endpoint; close on motion-inactive or the modal's X button
- [ ] Build + lint the dashboard app; in-browser verification of the live feed/modal is on you as usual, not claimed here as tested

### Phase 9 — Docs

- [ ] Add `docs/camera-devices-architecture.md` mirroring `device-architecture.md`'s phased structure, covering the schema additions, the WS listener, the dispatch seam, the SSE stream, and the video-proxy mechanism actually chosen in Phase 7
