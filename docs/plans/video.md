# Video Digitization

I have [this USB hdmi capture card](https://www.walmart.com/ip/Monster-LED-4K-Black-Audio-Video-USB-3-0-HDMI-Capture-Card-Record-High-Quality-Videos-Live-Stream/5550338999) hooked up to a small media pc. I want to use it to digitize old physical media that I have.

In the overall workflow, I want to:
- Play old media on various players through the capture card, creating a raw stream of lots of media (old family videos, old vhs/dvd movies or tv shows, etc)
- Process the video formally - determine start/end of clips, movies, episodes; set metadata like title, episode/season, etc
- Store the video in the network media file share

I want to use the Aerie stack to do the middle (processing/metadata) step.

I want the app stack to be very stable and run for a long time, lazily processing media as my family watches through archives.

We are NOT going to write an entire plan upfront. I want to work step by step forward and determine things that will work in line with a vision

## First Prompt: Video Share Solution

I have setup the media capture PC and HDMI card. I need to figure out how to expose the video in a way that can later be picked up by an actor in the Aerie ecosystem (Which we wil write in a later plan).

Determine:

- Architecture/protocol for video share. My initial thought is using something like WebRTC to expose whatever video is playing to the network, and an Aerie process can subscribe to it. I don't know much about network video, so give me better suggestions if this is a bad idea.
- Application stack: what software do we need to run on the media capture PC to capture and share the video stream?
- Automation: I would like to automate as much of the app stack setup as possible. The capture PC is running Windows 11, so if feasible I want an idempotent powershell script that will set everything up

Create a plan within this document heading section and ask me any questions needed. Give me options where there are judgment calls to make. Create a todo list of concrete independent steps to develop the script.

### The reframe: two paths, not one

WebRTC is a reasonable instinct and the wrong primary mechanism. It is a *conversational* protocol — it is designed to drop frames, drop quality, and drop resolution rather than add latency, because in a video call a stale frame is worthless. Every one of those properties is backwards for this job.

The thing that actually disqualifies it: **a tape plays in real time and you get one shot.** A 2-hour Hi8 cassette is a 2-hour capture you cannot casually redo, and some of these tapes are already at the age where each play costs you a little of the oxide. A transport whose defined behavior under congestion is "discard the picture" cannot be the one carrying the only copy. Three more, in descending order of how much they'd hurt:

- **A stream has no memory.** If the Aerie actor is down at 8pm — deploy, k3s node reboot, Longhorn rebalancing — a stream loses the tape. A folder does not. The stated vision is "very stable, run for a long time, lazily processing media as my family watches through archives," and that is a description of a *queue*, not a stream. The whole design should follow from that sentence.
- **The processing step needs random access.** Finding clip boundaries, scrubbing to check where an episode really starts, re-running detection with a better threshold — none of that is possible against a live stream. You would be building a recorder inside the subscriber anyway.
- **It costs an extra generation.** Capture→file is one encode. Capture→WebRTC→Aerie→file is two, with a lossy adaptive hop in between, on source material that is already the worst it will ever be.

So split it:

| | Carries | Protocol | If it breaks |
| --- | --- | --- | --- |
| **Archive path** | The only copy of the tape | ffmpeg → local file → atomic move to an inbox on the share | You lost a tape. Must not break. |
| **Monitor path** | A live preview, so you can see what the deck is doing from another room | WebRTC/HLS via go2rtc | Nothing. Disposable by construction. |

WebRTC is the right answer for the monitor path and it is genuinely nice to have — but it is optional, and I'd build it second. See "Scope call" below.

### Architecture

```text
  deck (VHS/Hi8/DVD) ──HDMI──> capture card ──USB3──> capture PC (Win11)
                                                          │
                                            ┌─────────────┴──────────────┐
                                            │   ffmpeg (single process)  │
                                            │   sole opener of the device│
                                            └──────┬──────────────┬──────┘
                                          tee muxer│              │tee muxer
                                                   ▼              ▼
                                    D:\capture\staging\      rtsp://127.0.0.1
                                    session.mkv (archival)   (low-bitrate copy)
                                                   │              │
                                     atomic move   │              ▼
                                     on completion │           go2rtc
                                                   ▼         (WebRTC/HLS)
                                    \\share\Video\inbox\          │
                                    session.mkv                   ▼
                                    session.capture.json     browser / kiosk
                                                   │
                                                   ▼
                                    ( later plan: Aerie actor claims
                                      from inbox/, clips, tags, files )
```

**One process owns the device.** This is a hard constraint, not a preference: a UVC capture device admits exactly one opener. You cannot have OBS previewing while ffmpeg records, and you cannot have go2rtc grab the device for monitoring while a capture is running — the second opener gets `I/O error` or silently gets nothing. Everything fans out from a single ffmpeg invocation via the `tee` muxer, or it doesn't fan out at all.

**Capture to local disk, never straight to the share.** A CIFS blip mid-capture destroys a tape, and the share is spinning disks with other traffic on them. Local NVMe during, atomic move after.

**The handoff contract is a directory, and it is the entire interface** to the not-yet-written Aerie actor:

```text
D:\capture\staging\      being written, local only, never visible to Aerie
\\share\Video\inbox\     complete, atomically moved, unclaimed
\\share\Video\processing\ actor has claimed it (move = the lock)
\\share\Video\archive\    processed, retained until the operator deletes
```

Atomic-move-on-complete is what makes this safe without any coordination protocol: the actor never observes a partial file, because a file appears in `inbox/` only in its final form. Move-to-claim gives you a lock for free, and a crashed actor leaves evidence in `processing/` instead of a lost job. No queue, no broker, no liveness assumption between the two machines — which is exactly the "runs for a long time, lazily" property being asked for.

**Each capture emits a sidecar** `<session>.capture.json` alongside the video: session id, start/end UTC, the operator's typed label ("Christmas 1994, tape 3 of 5"), source format (VHS/Hi8/DVD), the resolved device string, negotiated resolution/fps/pixel format, the exact ffmpeg command, and the dropped-frame count ffmpeg reports. That file is the seed for the metadata step and the audit trail for the capture — when a clip looks wrong two years from now, it says what produced it.

### Application stack

| Component | Role | Why this one |
| --- | --- | --- |
| **ffmpeg** (Gyan build, pinned) | Sole opener of the capture device; writes the archival file and tees the preview | Nothing else is needed. OBS is a GUI wrapped around the same libraries and adds a scene graph, a plugin surface, and a websocket API for a job that is one command line. |
| **go2rtc** (pinned) | Republishes the preview as WebRTC/HLS/MSE for browsers | **Already the chosen sidecar in [the cameras plan](cameras.md) Phase 7.** One streaming concept across the product instead of two. MediaMTX is the equally good alternative that would make it two. |
| **Capture agent** | Session lifecycle: start/stop, naming, sidecar, atomic move, retry | The seam the future Aerie actor plugs into. See Q4. |
| **WinSW** (pinned) | Runs go2rtc and the agent as Windows services | Single exe + XML, pins by hash, no installer. NSSM is the alternative; its last release is 2018. |

Rejected: **OBS + obs-websocket** (correct if a human wants scenes, overlays, and a multi-source composite; here it's a heavier dependency, a GUI session requirement, and a second thing that can break between you and the tape). **Plex/Jellyfin live capture** (not what they do). **Raw HLS-only** (5–20s latency makes it useless as a "is the deck actually playing?" check, which is the only reason to have a preview at all).

### The things that will actually bite

These are in the plan because each one silently ruins a capture rather than failing loudly, and the setup script is the right place to prevent all of them.

1. **HDCP.** Consumer DVD and Blu-ray players assert HDCP on HDMI, and a card in this class will give you black frames or "no signal" rather than an error. VHS/Hi8 decks have no HDMI at all — they need a composite/S-Video→HDMI converter, and analog outputs aren't protected, so the family-video path is unaffected. For commercial discs the clean route is the player's own composite/component output into the same converter. Worth knowing before you spend an evening debugging a black picture as a driver problem. (Drives Q1.)
2. **The card is probably MJPEG-first.** Cards in this class (MacroSilicon MS2130 and relatives) typically offer MJPEG at high resolution/fps and uncompressed YUY2 only at lower ones. **Prefer YUY2 even at lower resolution.** Encoding MJPEG into a "lossless" FFV1 file preserves the JPEG artifacts and lies to you about it; and the real detail in a VHS frame is roughly 330×480, so 1080p60 MJPEG is spending every bit of its bitrate on upscaled noise. The discovery step enumerates what the card actually offers rather than assuming.
3. **A/V drift over a 2-hour capture.** The classic USB-capture failure: audio and video arrive on independent clocks and separate by seconds by the end of a tape. Mitigated with `-use_wallclock_as_timestamps 1` and `-af aresample=async=1000`, and by taking audio from the card's *own* audio device (embedded HDMI audio) rather than a separate sound card, which would put a third clock in the picture. Verified by the smoke test, not assumed.
4. **Don't deinterlace at capture time.** It's irreversible, and the good deinterlacers (bwdif/yadif) belong in the processing step where you can re-run them. Keep the fields if the card gives them. Test what it actually delivers — many of these cards deinterlace in hardware and give you no choice, which is worth *knowing* even though you can't change it.
5. **Windows will sabotage an unattended 2-hour capture** in at least five ways: sleep/hibernate, USB selective suspend, disk spindown, Windows Update auto-restart, and Defender real-time scanning a file that is growing at 40 MB/s. Also OneDrive, if the staging path ever lands under a synced folder. All five are script-fixable and all five are in the harden stage.
6. **MKV, not MP4.** An MP4 killed mid-write — power loss, USB drop, crash — is unplayable, because the index is written at the end. Matroska tolerates truncation and you keep everything up to the cut. On a one-shot capture this is the difference between losing 10 minutes and losing the tape.

### Scope call: cut the monitor path from v1

I'd build the archive path first and completely, and add go2rtc after it's proven. The preview is the fun part and the part you asked about, so I want to be explicit rather than quietly dropping it: it is roughly a third of the setup complexity (a second service, firewall rules, a config file, a browser client, and a `tee` branch that can stall the archival write if the RTSP sink backpressures) in exchange for convenience while sitting next to a deck you are already sitting next to. It gets much more valuable later, when the kiosk can show "tape currently digitizing" — which is also when the camera work will have already proven the go2rtc pattern.

Phases 1–4 below are the archive path. Phase 5 is the monitor path, separable and droppable. Tell me if you'd rather have it in v1 and I'll fold it in — the `tee` seam is in the design from the start either way, so adding it later is not a rewrite.

### Repo fit

Follows the conventions already set by [scripts/k3s/](../../scripts/k3s/) and [scripts/hyperv/](../../scripts/hyperv/) rather than inventing a shape:

- **Stages**: Preflight → Discover → Install → Configure → Harden → Register → Verify, mirroring `Install-K3sNode.ps1`'s Preflight/Inspect/Install/Verify, with a full comment-based help block carrying the reasoning.
- **Pins in [scripts/versions.json](../../scripts/versions.json)**, not in the script: `ffmpeg`, `go2rtc`, `winsw`, each with a version-specific immutable URL and a SHA256, with a `_comment` explaining the pin — same treatment as `awsCli`. Never a `latest` URL. Read through `scripts/lib/AerieVersions.ps1`, with a `-FfmpegVersion`-style override parameter for a by-hand run.
- **Parameterized per [docs/ethos.md](../ethos.md)**: `-InboxPath`, `-StagingPath`, `-CaptureDeviceName`, `-AudioDeviceName`, `-CaptureProfile`, `-MonitorPort`. No domain, no IP, no share path, no device string baked in. The card's device name is *discovered* and written to config, not committed — it differs per card and per USB port.
- **GHA wrapper alongside the script**, per the established rule: `provision-6-capture-host.yml`, `runs-on: self-hosted`, thin — checks out and hands every decision to the script, so the manual path and the automated path can't drift. This requires the capture PC to be a self-hosted runner ([scripts/runner/](../../scripts/runner/) already exists for that). Drives Q5.

## Open questions

Ordered by how much the answer changes the build. Nothing below blocks starting Phase 1 — I've noted the default I'll assume if you'd rather just say "go."

**Q1 — What's actually going into the card?** VHS, Hi8/Video8, MiniDV, DVD players, a camcorder playing back its own tapes, some mix? This decides whether a composite→HDMI converter is in the parts list, whether HDCP ever comes up, and what resolution/field setup the discovery step should be looking for. Also: **is there a specific deck already hooked up**, or is the card currently connected to nothing?
*Default if unanswered: assume analog decks via a converter, 480i source, HDCP not a factor.*

**Q2 — Archival codec, i.e. how much disk are you willing to spend?**

| Option | Size/hr @480i | Notes |
| --- | --- | --- |
| **FFV1 in MKV** (lossless) | ~25–40 GB | True archival master. Over-preserves tape noise, but the decision is never revisited. |
| **H.264 CRF 18** (visually lossless) | ~8–12 GB | Indistinguishable in practice on analog source. One generation of loss you can't undo. |
| **Split by value** *(recommended)* | — | FFV1 for irreplaceable family video, H.264 for commercial discs you could rebuy. Falls out naturally as a `-CaptureProfile Archive\|Standard` parameter. |

How much free space is on the share, and roughly how many tapes?
*Default: the split, with `Archive` as the default profile.*

**Q3 — How does a capture start and stop?**

| Option | How it feels | Cost |
| --- | --- | --- |
| **Manual command** | You RDP/sit at the PC, run `Start-CaptureSession -Label "Christmas 1994"`, hit stop | Lowest. Works today. |
| **Signal-driven auto** | ffmpeg runs forever; blackdetect/silencedetect starts and stops it | Fragile — these cards emit black frames rather than "no signal," so it's heuristics all the way down, and a dark scene in a home video looks exactly like a stopped tape |
| **HTTP agent** *(recommended)* | A tiny local endpoint; you drive it from a phone bookmark now, Aerie calls the same endpoint later | Moderate, and it's the seam the later plan needs anyway |

*Default: HTTP agent, with the PowerShell functions underneath it so the manual path always works.*

**Q4 — What is the agent written in?** PowerShell + Task Scheduler is fastest and matches `scripts/`. A small .NET worker (`src/Aerie.Capture`) is more code now but is a real service, shares the repo's logging/config, and gives the future actor a typed API instead of a shell-out. Given "very stable, run for a long time," I lean .NET — but PowerShell-first and port later is a legitimate call if you want a tape captured this week.
*Default: PowerShell for Phases 1–4; revisit before building the actor.*

**Q5 — Can the capture PC be a self-hosted runner?** Is it on the LAN, always-on, and are you willing to put a runner on it? If yes, the GHA wrapper is real automation. If no, the wrapper degrades to lint + Pester in CI and the script is dispatched by hand, which I'd rather know now than discover in Phase 4.
*Default: assume yes.*

**Q6 — Do you want the monitor path in v1?** See "Scope call." *Default: no, Phase 5 deferred.*


## Working the phases

Independent steps, each landing something checkable on its own. Phases 1–2 are pure discovery against the real hardware and should happen before any script is written — every parameter below depends on what the card actually reports.

## Phase 1 — Characterize the hardware (no code)

- [ ] Confirm the card enumerates: `ffmpeg -list_devices true -f dshow -i dummy` — record the exact video **and** audio device strings verbatim (they contain the vendor's punctuation and are what the config will hold)
- [ ] Enumerate supported formats: `ffmpeg -f dshow -list_options true -i video="<name>"` — record every pixel format / resolution / fps combination offered
- [ ] Decide the capture format from that list, applying "prefer YUY2 over MJPEG even at lower resolution" (bite #2). Write down the choice and the reason
- [ ] Capture 60 seconds of a real deck playing and eyeball it: is the picture actually there, is it interlaced or hardware-deinterlaced, is audio present and on the card's own device
- [ ] Check A/V sync on a **10-minute** capture, not a 60-second one — drift doesn't show up in a minute (bite #3)
- [ ] If a DVD player is in scope, confirm whether HDMI gives a black frame (HDCP) and whether its composite output works instead (bite #1)
- [ ] Measure sustained write throughput and confirm the staging disk keeps up at the chosen format without dropped frames

## Phase 2 — Nail the capture command

- [ ] Build the archival ffmpeg command for the `Archive` profile (FFV1/MKV) and run a full 2-hour capture end to end, unattended, overnight
- [ ] Same for the `Standard` profile (H.264 CRF 18/MKV)
- [ ] Confirm ffmpeg's dropped/duplicated frame counters read zero across the full 2 hours; if not, stop and fix before anything else is built on top
- [ ] Confirm the resulting file plays, seeks, and is still sync'd at the 2-hour mark
- [ ] Kill ffmpeg mid-capture on purpose and confirm the MKV is still playable up to the cut (bite #6)
- [ ] Add the `tee` branch (null sink for now) and confirm it changes neither the output nor the drop counters — proves the fan-out seam before Phase 5 needs it

## Phase 3 — `Initialize-CaptureHost.ps1`

Each bullet is a stage that can be written and tested independently.

- [ ] Scaffold `scripts/capture/Initialize-CaptureHost.ps1` with the full comment-based help block, `[CmdletBinding()]`, and the parameter set from "Repo fit"
- [ ] Add `ffmpeg`, `go2rtc`, `winsw` entries to `scripts/versions.json` with version-specific URLs, SHA256s, and `_comment` blocks
- [ ] **Preflight**: admin rights, Windows 11, a USB *3* port, staging disk free space against the profile's GB/hr, inbox path reachable and writable
- [ ] **Discover**: enumerate DirectShow devices, match the capture card, enumerate its formats, resolve the chosen profile against what's actually offered — fail loudly if the card moved USB ports and the string changed
- [ ] **Install**: download + hash-verify + extract each pinned tool to `C:\Aerie\capture\`; skip cleanly when already at the pinned version (idempotency lives here)
- [ ] **Configure**: write the capture profile config and the directory structure; create staging/inbox/processing/archive if absent
- [ ] **Harden**: power plan (no sleep/hibernate/disk-spindown), USB selective suspend off, Windows Update active hours + no auto-restart, Defender exclusion for the staging dir, assert staging is not under a OneDrive-synced path (bite #5)
- [ ] **Register**: WinSW service definitions; idempotent create-or-update, never a blind `New-Service`
- [ ] **Verify**: 30-second smoke capture asserting non-black frames, audio above a noise floor, zero dropped frames, and a valid sidecar; delete the artifact afterward
- [ ] Re-run the whole script three times on a clean box and confirm runs 2 and 3 change nothing and report so

## Phase 4 — Session lifecycle + the handoff contract

- [ ] `Start-CaptureSession` / `Stop-CaptureSession`: launch ffmpeg with the resolved profile, name the file, capture the operator's label
- [ ] Write the `<session>.capture.json` sidecar with every field listed in "Architecture"
- [ ] Atomic move staging → inbox on clean completion, with retry/backoff for a share that's briefly unreachable, and a local quarantine dir if it stays unreachable
- [ ] Crash recovery on service start: anything left in `staging/` from a killed capture gets a sidecar marked `incomplete` and is moved to inbox rather than silently orphaned
- [ ] Wrap both in the HTTP agent if Q3 lands there — one `POST /capture/start`, one `POST /capture/stop`, one `GET /capture/status`
- [ ] `provision-6-capture-host.yml`: thin `self-hosted` wrapper, host choice input, every decision delegated to the script
- [ ] Pester tests for the pure parts (format selection from an enumerated list, path/name construction, sidecar shape) — the device-touching parts stay untested, same precedent as the HA-facing code in the cameras plan Phase 4
- [ ] `docs/video-capture-architecture.md` documenting the two paths, the inbox contract, and the sidecar schema — this is what the next plan's actor is written against

## Phase 5 — Monitor path (deferred; see Scope call)

- [ ] Add the low-bitrate H.264 RTSP branch to the `tee` and confirm the archival write is unaffected when the sink stalls
- [ ] go2rtc config + WinSW service; firewall rule scoped to the LAN
- [ ] Confirm WebRTC playback in a plain `<video>` element, reusing whatever the cameras plan Phase 7 settles on
- [ ] Decide whether this surfaces in the kiosk as a "currently digitizing" tile — likely belongs in the *next* plan, with the actor
