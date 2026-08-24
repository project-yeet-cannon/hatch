# Camera Devices Architecture

## Summary

A camera is a [`Device`](device-architecture.md) like any other — imported from
Home Assistant, assigned to a zone, enabled or disabled — with two things no
other kind has: **motion events that arrive as they happen**, and **live video**.

Those are two independent paths, and keeping them independent is the main
structural decision in this feature:

```text
motion   camera → Home Assistant → HomeAssistantEventListener (WebSocket)
                                 → IMotionEventDispatcher
                                 → GET /api/motion-events/stream  (SSE)
                                 → the kiosk dashboard

video    camera ← go2rtc (RTSP)
                ← CameraController relay  (WebSocket, GET /api/devices/{id}/camera/stream)
                ← the kiosk dashboard, or the admin app's live view
```

Home Assistant is the source of a camera's **identity** — the `camera.*` entity
id that discovery imported, and the sibling `binary_sensor.*` that reports
motion. It is deliberately *not* in the video path. HA bundles go2rtc but binds
its API to a port it does not expose, documented as debug-only; a standalone
go2rtc pulls the same RTSP stream over a documented, stable endpoint instead.

Aerie is the source of a camera's **address and credential**. Both live in the
`CameraConnections` table, set from the devices admin UI, and go2rtc is told
about a stream over its own API immediately before someone watches it. go2rtc
therefore holds no camera configuration at rest, and adding a camera is a form
rather than a Kubernetes Secret plus a workflow dispatch plus a pod restart —
which is what [`ethos.md`](ethos.md) requires of anything an operator other than
the author has to do.

The one reaction to motion today is hardcoded: open the feed on the kiosk. It
sits behind `IMotionEventDispatcher`, so a trigger/action registry can replace
it without touching anything that knows about Home Assistant.

## Domain model

Three additions to the device-mapping schema
([`DeviceMapping.cs`](../src/Aerie.Api/Ef/DeviceMapping.cs)):

- **`DeviceKind.Camera`**.
- **`DeviceChannelMetric.CameraFeed`** — `HaEntityId` is the `camera.*` entity.
  It is also the go2rtc stream name; see [Video](#the-video-path).
- **`DeviceChannelMetric.MotionState`** — `HaEntityId` is the sibling
  `binary_sensor.*`, `HaAttribute` null, bare on/off state. It needed no
  extractor changes: it fits `EfStateChange`/`ChannelValueExtractor` as-is.

Both enums store their underlying int, so new members are appended and never
inserted — the comment on each says so. Adding them required no EF migration.

**`CameraConnections`** is a table of its own, keyed by device (a device has one
camera connection or none): `Host`, `DiscoveredHost`, `Port`, `StreamPath`,
`Username`, `PasswordProtected`. The protected-column-on-the-owning-table shape
is what `EfCalendarAccount` already does.

`Host` and `DiscoveredHost` are two fields on purpose. `DiscoveredHost` is what
Home Assistant last reported — written by discovery and by a refresh, never by
the operator. `Host` is the operator's override — written from the form, never
by a refresh. Keeping them apart is what lets a camera that moves on DHCP follow
along on its own without a discovery refresh silently clobbering a hand-typed
address. `CameraRtspUrl` resolves the override first, HA second.

The username is a plain column and the password is protected. That asymmetry is
the rule [`scripts/secrets/parameters.json`](../scripts/secrets/parameters.json)
already states about access key ids: an identifier is not credential material,
and keeping it readable is what makes the form usable — an operator can see
which account a camera is configured for without being handed its password back.

## Discovery and import

`DiscoveryService.InferKind` is a pure function of a Home Assistant device's
entity-id list. Precedence is **climate > media_player > camera > switch >
light**, and every step of that order exists because some device carries
entities that look like a different kind:

- A Sonos speaker's HA device also publishes `switch.*` tuning siblings
  (loudness, crossfade), so `media_player.*` is checked before `switch.*`.
- **A camera's HA device publishes both.** A Reolink carries six `switch.*`
  toggles — record, record audio, infrared lights, FTP upload, email on event,
  push notifications — and the spotlight models add `light.*_floodlight`. Before
  the camera branch was moved ahead of both, the first physical camera imported
  as a `SmartSwitch` anchored on `switch.*_email_on_event`, with no `CameraFeed`
  channel and therefore nothing downstream of import able to run at all. The
  failure was silent: the device imported, it just imported as the wrong kind.
  The precedence is pinned by tests over a real camera's verbatim entity list.

`CameraChannelBuilder` then builds the channels:

- **The feed anchor prefers the `_fluent` sub-stream** among the `camera.*`
  entities, ignoring the `_snapshots_*` still-image variants, and falls back to
  the ordinal-first candidate for non-Reolink cameras. Reolink publishes one
  `camera.*` entity per stream profile, and only the low-res sub-stream is
  guaranteed H.264 — the higher-res profiles can be H.265, which no browser
  here decodes. In practice the other four profiles ship *disabled*, and a
  disabled entity has no state object, so the grouping template never sees them
  and discovery is choosing from a single candidate. The preference is
  insurance for the day someone enables "Clear" in HA, which is exactly the case
  that would otherwise silently move the anchor.
- **The motion channel prefers `binary_sensor.*_person` over `*_motion`**,
  falling back to `_motion` when the camera has no on-camera AI. A plain motion
  sensor fires on trees, rain, headlights and shadows, and every transition opens
  a modal on the wall. (Reolink's AI sensors are `_person`/`_vehicle`/`_animal`;
  only `_person` and `_motion` are named in the preference list.)

**The host comes from Home Assistant first.** The grouping template also reads
`device_attr(id, 'configuration_url')`, which for a camera is its own web UI —
`http://<address>` — kept current by the integration's own DHCP handling.
`HaDeviceHost.FromConfigurationUrl` reduces that to a bare host (the port and
scheme describe the web UI, not RTSP; `homeassistant://` URLs are rejected
because their "host" is a config-flow handler name), and `DevicesController.Create`
seeds it into `DiscoveredHost` when a camera is imported. So a camera arrives
already knowing where it lives and the operator only supplies the credential.
It is null for most integrations and that costs nothing — every other kind
ignores it, and a camera whose device has none falls back to a typed host.

## The motion path

### The Home Assistant WebSocket listener

[`HomeAssistantEventListener`](../src/Aerie.Api/Services/DeviceMapping/HomeAssistantEventListener.cs)
is a `BackgroundService` on a raw `ClientWebSocket` against
`ws://{host}:{port}/api/websocket`. HADotNet wraps the REST API only, so this is
new infrastructure rather than a client call. It resolves host/port/token
through `IHomeAssistantConnectionManager.ResolveAsync`, so the WebSocket listener
and HADotNet's `ClientFactory` read one source of truth rather than two copies
of the SiteSettings-plus-deobfuscate logic.

**It runs on every `api` replica, not on one elected leader.** A kiosk's SSE
connection lands on whichever replica Traefik picked, so a replica that isn't
listening is a kiosk that never sees motion. Electing a leader would force a
cross-replica fan-out (Postgres `LISTEN`/`NOTIFY`, or similar) to exist before
the kiosk half could work at all, and three idle WebSocket subscriptions against
a single HA instance cost nothing. Same shape as `HomeAssistantClientFactoryGate`'s
"once per replica, not once per deploy".

Four behaviours are worth knowing about:

- **Subscribe broad, filter locally.** It subscribes to *every* `state_changed`
  event and matches `entity_id` against a cached map of `MotionState` channels
  (60s TTL, re-read on every reconnect). HA can filter server-side with
  `subscribe_trigger`, but that entity list would have to be re-sent — and the
  subscription torn down and rebuilt — every time a camera is imported or
  removed. The map is consulted only for frames that already parsed as an on/off
  transition, so it is not on the hot path. It maps to device*s*: nothing stops
  two devices being hand-mapped to one sensor, and picking one arbitrarily would
  drop the other's modal.
- **Reconnect with backoff, reset only after a session that reached an active
  subscription** — 2s doubling to 60s. Otherwise a revoked token becomes a
  full-speed reconnect loop. `auth_invalid` logs at Error, because reconnecting
  cannot fix it. An unconfigured Home Assistant is not a failure: it retries
  every 60s and logs once.
- **Keepalive, 30s ping / 15s timeout.** Backoff only helps once a socket
  reports that it broke. A connection killed without a FIN — HA's host losing
  power, Wi-Fi dropping mid-frame — leaves `ReceiveAsync` blocked forever and the
  listener silently deaf. The ping timeout is what converts that into a reconnect.
- **Losing the socket clears all motion**, in a `finally` around the receive
  loop. Without it, a device left active when the connection broke is stuck: the
  `off` that ended it arrives during the outage, so the next real `on` is no
  change from the stale state, nothing is announced, and a kiosk modal is pinned
  open for good.

[`HomeAssistantEventParser`](../src/Aerie.Api/Services/DeviceMapping/HomeAssistantEventParser.cs)
is the pure half — raw frame to `MotionTransition?`, no socket, no DB, no clock —
and is where the two rules that matter live:

- **Motion is exactly the state `on`.** `off`, `unavailable`, `unknown` and a
  missing state all read as no-motion, so a sensor that drops off the network
  mid-detection (reports `unavailable`, never reports `off`) closes the modal
  instead of pinning it open until the camera returns.
- **Attribute-only `state_changed` frames are not transitions.** HA fires one
  when a sensor re-reports the same `on` with a new `last_seen`; treated as a
  transition it would re-open a modal the user just dismissed.

### The dispatch seam

[`IMotionEventDispatcher`](../src/Aerie.Api/Services/DeviceMapping/MotionEventDispatcher.cs)
holds per-device motion state in memory and raises a C# event on change. It is a
singleton, and everything Home-Assistant-shaped stops here.

The state is a `HashSet<Guid>` of *active* devices under a `Lock`, not a
`ConcurrentDictionary<Guid, bool>`: membership is the state, so a no-op set is
detected by the set operation itself, and the lock makes mutate-and-raise one
step. Split apart, two transitions on one device can be applied in order and
announced out of order — which leaves a kiosk showing a modal for motion that
already ended. The lock is never contended today; one receive loop is the only
caller.

Two more properties the subscribers depend on:

- **`ActiveDeviceIds` snapshot.** Without it a kiosk connecting mid-motion would
  show nothing until the *next* transition.
- **Per-subscriber exception isolation in the raise loop.** Every subscriber is a
  kiosk connection, and one that died between the raise and its own cleanup would
  otherwise blind every other kiosk on the replica for that change.

### The stream to the kiosk

`GET /api/motion-events/stream` is **server-sent events**, not a WebSocket: the
traffic only goes one way, and `EventSource` reconnects on its own, so an API
restart or a rolling deploy heals without any client code. Framing is .NET's
built-in `TypedResults.ServerSentEvents`; each change goes out as
`{"deviceId":"…","isActive":true|false}`.

The sequence itself is
[`MotionEventStream`](../src/Aerie.Api/Services/DeviceMapping/MotionEventStream.cs),
separate from the controller so the ordering rules are testable without an HTTP
connection:

- **Handlers hand off to a channel rather than writing the response.** The
  dispatcher raises on the listener's receive loop, shared by every device; a
  response write in the handler would let one kiosk on a stalled connection stop
  motion for the whole replica. `AllowSynchronousContinuations` stays off for the
  same reason — a synchronous continuation would drag the write back onto that
  thread.
- **Subscribe first, read the snapshot second**, both on the first
  `MoveNextAsync`. A change landing between the two is queued behind the
  snapshot rather than lost. The cost is that it can repeat a device the snapshot
  already reported, which is harmless — a change carries absolute state, not a
  toggle — and the client is required to treat a repeat as a no-op.
- **A 20s heartbeat, under its own `event: heartbeat`** (which a browser
  `EventSource` ignores by default). A kiosk that loses power leaves a request
  that is never aborted and a handler that is never unsubscribed; motion can be
  quiet for days, so the heartbeat is what bounds how long a dead kiosk keeps its
  subscription, and it doubles as protection against a proxy closing an idle
  stream.
- **The opening frame is a heartbeat.** An SSE response writes no headers until
  its first frame, so a kiosk connecting on a quiet night would otherwise wait up
  to 20s for `EventSource.onopen` — indistinguishable from a broken API.

### On the wall

[`motionEvents.ts`](../src/Aerie.Web/apps/dashboard/src/lib/motionEvents.ts) is a
pure reducer over the stream; [`useMotionEvents.ts`](../src/Aerie.Web/apps/dashboard/src/hooks/useMotionEvents.ts)
is the `EventSource` wiring around it.

- **Dismissal is per *event*, not per camera.** Closing the modal has to stop
  being in effect when the motion ends, or the ✕ silently mutes that camera
  forever.
- **A repeated `isActive: true` is a no-op**, and in particular must not undo a
  dismissal — the SSE contract explicitly permits the repeat.
- **The stream dropping clears all motion**, mirroring what the listener does
  when it loses HA. Once we cannot see, we stop claiming there is something to
  look at; otherwise the end-of-motion frame arrives during the outage and the
  modal is pinned open for good. `EventSource` reconnects and the snapshot
  restores whatever is still active.

## The video path

### Where the credential lives

In Aerie's database, and nowhere else.

The first design put every camera's RTSP URL in a `go2rtc-streams` Secret,
seeded from a GitHub secret through the provisioning workflow. It worked, and it
was the wrong shape for the product: adding a camera meant editing a GitHub
secret, dispatching a workflow and restarting a pod — three systems and an
operator with repository access, for something a household should do from a
form.

**The trade, stated plainly.** A camera password in Aerie's database is
protected by obfuscation, not encryption — the same treatment the Home Assistant
token and the calendar refresh tokens already get, described honestly in
[`secrets-architecture.md`](secrets-architecture.md). That is weaker than a
Kubernetes Secret backed by an SSM SecureString, which is what it replaced. It is
a deliberate trade made with the values in hand, and what it owes the future is
that the upgrade is cheap — which is why
[`SecretProtector`](../src/Aerie.Api/Common/SecretProtector.cs) became *versioned*
in the same pass:

- Every protected value carries the scheme that produced it: `v1:<payload>`.
  Adding real crypto later is a `v2:` scheme plus a lazy re-protect on write,
  with mixed rows readable throughout — no stop-the-world pass over every table
  holding a secret.
- The discriminator is free rather than clever: a v1 payload is base64, and
  base64's alphabet has no colon in it, so no legacy value can be mistaken for a
  tagged one.
- **v1 *is* the algorithm the existing rows were already written with**, so
  converting every stored secret was prepending three characters and changing no
  bytes. The `AddCameraConnections` migration relabels the `SiteSettings` keys
  and the calendar OAuth tokens in the same pass, guarded by `NOT LIKE '%:%'` —
  which makes it safe to run twice, and safe against a database the new code has
  already written to.

`CameraRtspUrl` is the only place a credential and a URL are ever joined, and
percent-encodes both halves exactly once. That is the whole reason it exists as a
pure function: a camera password is chosen by a person in a router-grade web UI,
so it contains `@` and `:` and `/` far more often than a password that was ever
going to end up in a URL should. Unescaped, an `@` ends the userinfo early and
the URL silently points at a different host — which go2rtc reports as a
connection failure to an address nobody configured.

### Registration is lazy

`CameraController` PUTs the stream to go2rtc immediately before it opens the
relay socket ([`Go2RtcStreamRegistrar`](../src/Aerie.Api/Services/DeviceMapping/Go2RtcStreamRegistrar.cs)).
No reconciler, no startup pass. **Aerie is the source of truth and go2rtc's copy
is a cache that dies with the pod**, which is exactly the lifetime it should
have: a restarted go2rtc that has forgotten everything is correct rather than
broken, and there is no stale copy of a camera password to invalidate when one
changes. The cost is one HTTP round trip per modal open, against a keyframe wait
measured in seconds.

The **stream name is the `CameraFeed` channel's `HaEntityId`**. go2rtc accepts an
arbitrary string as a stream name, dots included, so the entity id is already the
whole mapping and a camera needs no second identifier stored anywhere.
`CameraStreamTarget` turns it into the upstream WebSocket URL.

### The relay

`GET /api/devices/{deviceId}/camera/stream` upgrades to a WebSocket and
[`CameraStreamRelay`](../src/Aerie.Api/Services/DeviceMapping/CameraStreamRelay.cs)
pumps bytes both ways until either end goes away.

**By device id rather than by channel id**: a device has exactly one `CameraFeed`
channel, and the kiosk gets a bare device id from the SSE stream, so resolving
the channel server-side saves a round trip at the one moment latency is the whole
point.

**Transport is MSE over WebSocket, not HLS.** Sub-second latency, no manifest to
rewrite, and no `hls.js` in the dashboard — `MediaSource` takes go2rtc's
fragmented MP4 directly.

**The relay is byte-transparent.** go2rtc's stream socket is a small JSON control
exchange followed by binary fMP4 forever; all of that is between the browser and
go2rtc. Teaching the relay that protocol would buy nothing and would put a second
implementation of it in the path of every frame. What the relay exists for
instead: a kiosk holds one connection, to one origin, authenticated the same way
every other Aerie request is, and never learns a camera's address or password —
the only two things that would let a camera be watched from anywhere else.

Details that are load-bearing rather than incidental:

- **Upstream first, then accept the client.** Accepting first would complete the
  handshake, and every failure below would reach the browser as a socket that
  opened and immediately closed, with the reason on the server only. Connecting
  first keeps those failures on the HTTP response, where a status code says which
  one it was: **404** no such enabled camera, **409** the camera has no address
  configured yet, **502** go2rtc refused or is unreachable, **500** the
  configured go2rtc address is unusable.
- **`MessageType` and `EndOfMessage` are forwarded as received.** The first
  matters because go2rtc's control replies are Text and its video is Binary, and
  a browser delivers them to different branches of `onmessage`; the second keeps
  a fragment that spilled over the 16 KB buffer a single message on arrival. The
  buffer is a throughput knob, not a correctness one.
- **Either direction ending ends the call**, so a pump is never left parked in
  `ReceiveAsync` holding a go2rtc connection — and therefore a camera connection
  — open.
- **Close frames are passed through rather than reinvented**, so a go2rtc that
  rejected the stream reaches the browser as that status and reason instead of a
  generic hang-up.
- **The camera's password never reaches a log line.** The registrar logs go2rtc's
  error *body* and the stream name, never the request URL; logs ship off the
  cluster.

### In the browser

[`cameraStream.ts`](../src/Aerie.Web/apps/dashboard/src/lib/cameraStream.ts) holds
the rules and [`useCameraStream.ts`](../src/Aerie.Web/apps/dashboard/src/hooks/useCameraStream.ts)
the imperative shell — the same split the API side makes between
`MotionEventStream` and its controller. No frontend dependency at all.

The exchange, verified against go2rtc 1.9.14:

```text
->  {"type":"mse","value":"avc1.640029,avc1.640028,…"}      codecs this browser can play
<-  {"type":"mse","value":"video/mp4; codecs=\"avc1.640029\""}
<-  <binary ftyp+moov>                                       init segment
<-  <binary moof+mdat> …                                     fragments, forever
```

- **The reply's codec string is what `addSourceBuffer` gets**, not the one that
  was asked for. go2rtc answers with the codec the *stream* actually has —
  asking for `avc1.640028` came back `avc1.640029` — and handing `addSourceBuffer`
  the requested string instead would reject every fragment that followed.
- **Only H.264 profiles are offered**, because the kiosk is deliberately on the
  sub-stream. Each candidate is checked against the browser first; an empty
  result is reported as "this display can't play the feed" rather than opening a
  socket that cannot succeed.
- **SourceBuffer trimming (30s) and drift correction.** A `MediaSource` has a
  finite quota, so a modal open for the length of a long motion event ends in
  `QuotaExceededError` and a dead feed; and a kiosk tab backgrounded between
  events comes back seconds behind, showing footage of what already happened.
  Both are silent without the fix and both are certain to happen on a wall
  display.
- **Appends are queued and drained on `updateend`** — `appendBuffer` is
  one-at-a-time, and calling it during an update throws `InvalidStateError` and
  ends the feed.

## Deployment

go2rtc is a Deployment, Service and ConfigMap in [`charts/aerie`](../charts/aerie).
It is the one thing in the system that speaks to a camera.

- **One replica, `Recreate` strategy.** Every replica is an independent RTSP
  *client*, and Reolink caps concurrent sub-stream connections in the single
  digits; a rolling update would briefly run two full sets of connections, and
  the failure mode — a camera refusing the new pod until the old one exits —
  reads as a broken deploy rather than as a connection limit. There is no state
  to lose in exchange.
- **ClusterIP, no Ingress.** The only client is the api pod's relay. go2rtc's
  API is unauthenticated by default and `GET /api/streams` returns each stream's
  producer URL *verbatim, camera password included* — so putting it on a NodePort
  or an Ingress would publish camera credentials to everything that can reach the
  listener. This is also why Home Assistant's own integration is **not** pointed
  at this instance (`go2rtc: url:`), which would otherwise save one RTSP
  connection per camera.
- **The `-config` order is load-bearing, and is the opposite of what reads
  naturally**: the writable `emptyDir` first, the ConfigMap second. go2rtc
  persists a runtime `PUT /api/streams` to its *first* `-config` path and answers
  **400** when that path is read-only — while still registering the stream, so
  the call looks failed and worked. In this order PUT answers 200, the ConfigMap's
  blocks still apply, and the streams land in the emptyDir. If that ever
  regresses, the registrar starts logging failures for streams that work; that is
  the symptom to recognise.
- **RTSP binds loopback rather than being disabled.** go2rtc's own `ffmpeg:`
  sources — any transcode, which an H.265 main stream would need — work by having
  ffmpeg push back into go2rtc over RTSP on localhost; with `rtsp: listen: ""`
  every one of them fails with `streams: exec: rtsp module disabled`. Loopback
  keeps that possible while exposing nothing. WebRTC is off: it is the one
  transport that would need the *viewer* to reach go2rtc directly.
- **`/api/streams` is the readiness probe.** It answers 200 with `{}` when
  nothing is configured, so a down camera cannot take the pod with it.
- **Non-root (uid 65532), read-only root filesystem**, needing only a writable
  `/tmp` and the runtime config dir.
- **Resources are a measurement, not an estimate**: idle 11.7 MiB, one viewer
  12 MiB / 0.9% of a core, five viewers 21.4 MiB / 1.6%, against a request of 25m
  and 64Mi. The shape is a ~12 MiB floor plus roughly 2 MiB and 1.5m CPU per
  viewer — the relay is a copy, not a decode. The request deliberately sits well
  above that because it has to cover every camera's producer, and the 4x limit is
  there so a transcode blows past it visibly.

Everything asserted about this image was verified by running
`alexxit/go2rtc:1.9.14` under Docker, not inferred from its docs — including that
repeated `-config` arguments *merge* and that a `-config` path that does not
exist is skipped silently, which is what lets the runtime file start out empty.

## Surfaces

**Admin — devices page.** A camera row gets a **Camera connection** panel
(`CameraConnectionForm`) and a **Live view** button (`CameraLiveViewModal`),
the latter only for an enabled `Camera`, because the relay serves enabled devices
only and offering it otherwise would be a button that always fails. The live view
is the answer to "did what I just typed work" in the place it was typed.

The password box carries the one rule in the form: the API will never hand a
stored password back, so the box cannot be pre-filled, and an empty box is
ambiguous unless something else resolves it. `hasPassword` is that something —
the placeholder says whether one is set, and leaving the box untouched sends
`undefined`, which the API reads as "leave it alone". Without that, every save
that only changed the host would silently clear the credential.

**Kiosk — the button row.** Cameras ride on the dashboard snapshot as
`CameraSummary(Id, Name, IsConfigured)`, next to routines, so rendering the
buttons costs no second fetch. `ICameraDirectory` keys on **a `CameraFeed`
channel plus `Device.Enabled`** — verbatim the predicate `CameraController` uses
before it answers or 404s, and deliberately not `DeviceKind.Camera`, which is
inferred at import and editable by hand. Keying the two on different questions is
how you get a button on the wall that answers 404.

Every enabled camera gets a button, configured or not. `IsConfigured` is
`CameraRtspUrl.TryBuild` over the connection row — the same question the relay
asks before its 409 — and an unconfigured camera renders "not set up yet"
*instead of* the `<video>`, which is also what keeps the socket shut. A camera
simply missing from the wall looks identical to one that was never imported, and
nobody standing in the hall can tell those apart.

**The modal is one component** for both paths. What differs is only what put it
there: **`manual ?? motion`** — someone deliberately watching the driveway should
not be shoved onto the back door because a branch moved, and motion resumes on
close if it is still going. The ✕ does both halves, clearing the manual selection
*and* dismissing the shown device's motion event; without the second half,
closing a hand-opened feed on a camera that is also in motion would leave the
modal exactly where it was — a close button that visibly does nothing.

**The idle rung.** A motion-opened feed holds the kiosk lifecycle, the same way
Gather does — a deploy reload firing while someone is watching who is at the door
is the same bug as one firing mid-Gather-entry. A hand-opened feed deliberately
does *not*, so the 30s idle reset closes it, and with it the relay's connection
and the camera's, rather than leaving the wall lit on a live stream all
afternoon. Watching a feed is a hands-in-pockets activity, so if 30s proves
short, `IDLE_DIM_AFTER_MS` (120s) is the value to move to; it is a one-line
change in [`kioskIdleTimings.ts`](../src/Aerie.Web/apps/dashboard/src/lib/kioskIdleTimings.ts).

The admin app carries its own copy of `cameraStream.ts` and `useCameraStream.ts`.
The apps share no package — `clientLogger`, `signIn` and `deviceMetadata` are
already carried as copies across three apps — and adding a workspace for two
files is more machinery than the duplication costs. Both copies speak the same
protocol to the same endpoint; a change to one belongs in the other, and the
tests live in the dashboard, which is where the file is authored.

## Hardware

Cameras were chosen for **event latency**, not image quality, because the whole
feature is a latency chain and push at the front is what makes the modal feel
live.

**Reolink PoE, wired only.** Platinum-tier *Works with Home Assistant*, fully
local, and it delivers motion binary sensors over TCP push — sub-second, rather
than the integration's 5-second fast-polling fallback. It also produces exactly
the entity shape discovery assumes: one `camera.*` plus a sibling
`binary_sensor.*` under one HA device.

What to avoid, and why:

- **Battery models** stay awake while the stream is viewed — fatal for a design
  that auto-opens a live feed on every motion event. **LTE models** are outright
  incompatible with the HA integration. Buy PoE.
- **Dual-lens models** (Duo 3, TrackMix) put multiple channels under one HA
  device, and the channel builders take the first match for both feed and motion,
  so half the camera would import silently. Single-lens keeps the
  one-device-one-feed assumption true.
- **An NVR** adds nothing here and complicates entity grouping.

Rejected alternatives: **UniFi Protect** — best-in-class when working, but has
repeatedly broken HA motion events on firmware updates, which is the wrong risk
profile for a system whose whole value is event-driven. **Amcrest/Dahua** — good
ONVIF hardware, but the motion path would depend on a HACS integration rather
than a core one. **Axis** — core, extremely reliable, ~4–6x the price.

### What the first camera actually published

Measured, and it corrected two predictions:

- The sub-stream at `/h264Preview_01_sub` is **H.264 High, 640×480, 10 fps**
  (plus an AAC track). The URL shape is exactly as assumed, which is why it is
  the `StreamPath` default.
- The main stream is **H.264 High, 2560×1920, 25 fps — not H.265** on this
  model. It changes nothing about which stream the kiosk uses, but it does mean
  the H.265 argument is no longer load-bearing anywhere; the loopback RTSP
  listener is kept for a *future* camera's transcode, not this one's.
- Only **one** `camera.*` entity appears, not five: the other profiles ship
  disabled and a disabled entity has no state object.
- Both `_person` and `_motion` exist, and the builder picks `_person`.
- **go2rtc drops the camera's audio track** rather than muxing it, because the
  client offers video codecs only. For a wall display that is the wanted
  behaviour rather than a gap.

### Latency, and where it actually comes from

**The dominant term is the camera's keyframe interval, and it is upstream of
everything Aerie does.** The sub-stream emits an IDR every 40 frames — at 10 fps
that is exactly 4.0 s. No viewer can be shown anything until one arrives, so
opening a feed costs a uniform 0–4 s wait, ~2 s on average, before the first
frame renders. Measured through go2rtc: first fragment at 3.82 s, 2.37 s and
1.29 s on three attempts; the spread is GOP phase, not jitter in the path.

**Keeping a stream warm is not the fix.** A second viewer joining a stream go2rtc
was already pulling still waited 1.29 s for its own first fragment — go2rtc does
not replay a cached keyframe to a new consumer. So a warm-stream mitigation buys
only the RTSP connect, a few hundred milliseconds.

Two levers, both on the camera rather than in this repo: shorten the I-frame
interval in the camera's web UI, or raise the sub-stream frame rate (the GOP is
40 *frames*, so 20 fps halves the wait). Worth doing before judging how the
kiosk feels — and before measuring motion-to-first-frame, or the measurement is
mostly of the camera.

## Verifying it

`scripts/k3s/Test-Cameras.ps1`, dispatched as the **Verify: Cameras** workflow,
asserts the cluster half: the go2rtc Deployment is 1/1 with exactly one pod, the
Service answers, every registered stream name is a `camera.*` entity id, and a
JPEG frame comes back for each of them through the API server's service proxy.

That last check is the one that matters most, because it is the only thing that
proves **pod-network egress reaches a camera's RTSP port** — the single link that
could have forced go2rtc off-cluster or onto host networking. A frame also proves
the credential Aerie sent was accepted and the camera is producing decodable
video.

Two things shape the script more than anything else:

- **Nothing in it prints a stream source.** `GET /api/streams` returns every
  camera's password in the response body and the output is a CI run log, so the
  body is piped through a filter on the node that emits top-level keys only.
- **Registration is lazy, so an idle go2rtc legitimately holds no streams.** The
  gate reports that as a warning naming what was *not* exercised, rather than
  passing quietly: open a camera in the admin UI and re-run.

It says nothing about HA's motion events, the SSE hop, or whether MSE works in
the kiosk's WebView. The gate prints those as manual items on every run.

## What is still unproven

Everything in this document is built, unit-tested, and — for the cluster and
go2rtc halves — verified against real hardware. These are the items that need a
person standing in front of a camera, and they are listed here rather than
quietly assumed:

- **Walk the whole thing once**: add a camera in the admin UI, open the kiosk
  dashboard, walk in front of the camera, confirm the modal opens with live video
  and closes when the motion ends. Then confirm the ✕ closes it and that the
  *next* motion event reopens it — the per-event dismissal rule is the one piece
  of the client's behaviour a unit test can assert but not prove.
- **Confirm MSE works in the kiosk's Android WebView specifically.** The video
  path rests on `MediaSource`, and the kiosk is a WebView rather than Chrome
  proper. `supportedCodecs()` reports the empty case as "this display can't play
  the feed" rather than failing obscurely, so a bad answer is visible rather than
  silent — but it would mean rethinking the transport.
- **Measure motion → first frame.** Partly answered above: the camera contributes
  a 0–4 s keyframe wait by itself, larger than the rest of the path is likely to
  be. HA's push latency and the SSE hop are still unmeasured.
- **Confirm a camera's HA device carries `configuration_url`.** One look at the
  discovery page answers it, and the fallback (type the host) is already built
  either way.

## See also

- [`device-architecture.md`](device-architecture.md) — the Zone/Device/Channel
  model a camera is a special case of, and the discovery pipeline it imports
  through
- [`kiosk-architecture.md`](kiosk-architecture.md#cameras-on-the-wall) — the
  wall's side: the button row, the modal, and the idle ladder it participates in
- [`dashboard-api-manifest.md`](dashboard-api-manifest.md#cameras-and-motion) —
  the endpoints, in the API surface table
- [`secrets-architecture.md`](secrets-architecture.md) — why a camera password is
  a protected row in Postgres rather than a parameter in the store
- [`delivery-architecture.md`](delivery-architecture.md) — how the go2rtc
  Deployment reaches the cluster
