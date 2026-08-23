# Media Library

How a music file on the house SMB share becomes something a Sonos speaker will play, via Aerie's `Speaker` devices and their `MediaPlayback` channels.

## Why anything is needed at all

Home Assistant's `media_player.play_media` doesn't stream audio to a Sonos speaker — it hands the speaker a URI and the **speaker fetches it itself**. Two consequences drive this whole design:

- **SMB paths never work.** A Sonos speaker's HTTP client can't read `\\NAS\Music\track.mp3`. (Sonos *can* index an SMB library through its own app, but that's the Sonos music-library feature, not something HA drives.)
- **The URL must be reachable from the speaker**, not from Aerie and not from HA. `localhost`, container-internal hostnames, and anything only routable from the API's network are all dead ends.

So Aerie serves the library over HTTP itself, and hands out URLs pointing back at it.

## The two knobs

| Where | What | Why there |
| --- | --- | --- |
| `MediaLibrary:RootPath` (appsettings / env) | Absolute path to the library root that gets served | Decides which directory the API exposes **unauthenticated** on the LAN. Deploy-time, not repointable from a web form. |
| `MediaLibraryBaseUrl` (SiteSetting, admin Settings page) | Absolute base URL speakers fetch from, e.g. `https://home.example.com/media` | Changes with hostnames and reverse-proxy layout, not with trust — so it's admin-editable with no redeploy. |

`MediaLibrary:RequestPath` (default `/media`) is the URL prefix the files are served under; the path part of `MediaLibraryBaseUrl` has to match it.

Serving is off entirely when `RootPath` is empty. A configured-but-missing path logs a startup warning rather than failing silently — see Program.cs.

### Deployment: a subdirectory of the house share

The library isn't a share of its own — it's a folder inside the house SMB share, the same one the file browser at `share.${DOMAIN}` serves ([file-share.md](file-share.md)). Both come from [`share-volumes.yaml`](../charts/aerie/templates/share-volumes.yaml), which declares the same device twice: `aerie-share-rw` for the GUI, and `aerie-share-ro` mounted at `/share-ro` in `api`. Two PersistentVolumes on one device is forced by a volume carrying exactly one mount mode, and it's what keeps the API's view read-only no matter what the GUI does — held that way twice over, by the `ro` mount option and by `csi.readOnly`. Nothing in Aerie writes to the library.

`api` runs as a Linux container, so `MediaLibrary__RootPath` is an **in-container path** (`/share-ro/<subpath>`), never a UNC path.

**Three variables, with very different failure modes.** All three are GitHub Actions **repo variables** (Settings → Secrets and variables → Actions → Variables), declared in [`cluster-config.json`](../scripts/k3s/cluster-config.json) and substituted into the HelmRelease by Flux:

| Variable | What | If it's wrong |
| --- | --- | --- |
| `SHARE_HOST` | FQDN or IP of the SMB server, e.g. `10.0.0.40` | The `share` and `api` pods sit in `ContainerCreating` until it's fixed; nothing else in the cluster is affected |
| `SHARE_NAME` | The share name alone, e.g. `aerie` — no slashes, not a path under the share | Same as above |
| `MEDIA_LIBRARY_SUBPATH` | Folder within that share holding the music, e.g. `Music` or `Media/Music` | Startup warning only — Program.cs logs `RootPath ... does not exist` and serves nothing |

That asymmetry is the point of composing the path in the environment rather than mounting the subdirectory directly: a typo in the *music* folder name can't take the app down.

**`MEDIA_LIBRARY_SUBPATH` unset turns media serving off, and that's a security property, not just a default.** [`api-deployment.yaml`](../charts/aerie/templates/api-deployment.yaml) renders the env var only when the value is non-empty, so an unset subpath leaves `RootPath` unset (serving off per "Serving is off entirely" above). Written the obvious way instead — an unconditional `/share-ro/{{ .Values.mediaLibrarySubpath }}` — an unset variable would collapse to `/share-ro/`, silently publishing **the entire house share** on an endpoint that is unauthenticated by design. The same reasoning is what keeps the value genuinely absent rather than empty on the way through: `MEDIA_LIBRARY_SUBPATH:=` in the HelmRelease, and `required: false` in `cluster-config.json`.

**The `/share-ro` mount itself is not optional.** It's mounted unconditionally, whether or not a subpath is set — the guard is on the env var, not on the volume, so turning media serving on is a variable change and not a redeploy shape change.

**Use an FQDN or an IP for `SHARE_HOST`, never the bare hostname.** The mount is performed by the node's CIFS client, not by Windows: its resolver doesn't apply the host's DNS suffix search list and speaks neither NetBIOS nor mDNS, so a name that resolves fine in Explorer leaves the pod stuck with a resolution failure on its `MountVolume.SetUp` event. If the record only exists on the LAN resolver, an IP in the variable sidesteps resolution entirely.

**Changing `SHARE_HOST` or `SHARE_NAME` is not enough on its own.** A PersistentVolume's `csi` block is immutable once the object exists, so re-rendering the chart with a new `source` is rejected rather than applied. Delete the two PVCs and the two PVs (`aerie-share-rw`, `aerie-share-ro`) and let Flux recreate them; both are `Retain`, hold no data, and are just handles on the remote share. `MEDIA_LIBRARY_SUBPATH` needs none of this: it's an env var, so a rollout is enough.

Both share variables are variables rather than secrets because they name a LAN host and an already-guest-accessible share. The credential that would be needed for a share that isn't guest-accessible already has its own home — the `smb-share` Secret the PVs reference through `nodeStageSecretRef` — so switching to one is a value change, not a manifest change.

### Getting files into the library

The file browser at `share.${DOMAIN}` can upload straight into the music folder, since it's mounting the same share read-write. That's the one path by which anything in this stack writes there — the API's own mount is read-only end to end.

Finally, set `MediaLibraryBaseUrl` to a hostname the speakers can resolve — with Caddy fronting the API that's the published `home.${DOMAIN}` name (`https://home.${DOMAIN}/media`), not the container's own `:8080`, which isn't published. The speaker verifies TLS, so that URL only works while Caddy is serving a publicly-trusted cert for the name; otherwise publish the API port on the LAN and use a plain `http://` base URL.

## What you can type into the Play box

`DevicesController.PlayMedia` runs the value through `MediaLibraryUrlResolver` before calling HA:

| Input | Result |
| --- | --- |
| `Miles Davis/Kind of Blue/01 So What.flac` | Expanded to `{MediaLibraryBaseUrl}/Miles%20Davis/...`, escaped per path segment |
| `https://…` / `http://…` | Passed through untouched |
| `media-source://…` | Passed through untouched — HA resolves and signs its own URL |
| `\\NAS\Music\x.mp3`, `D:\Music\x.mp3` | Rejected with an explanation, since the speaker can't fetch it |
| Anything containing `.` or `..` segments | Rejected |

Backslashes in a relative path are normalized to `/`, so pasting a share-relative path out of Explorer works.

## Content types

`StaticFileMiddleware` 404s any extension it can't type, and the built-in map misses several formats a music library actually holds (`.flac`, `.m4a`, `.opus`). `MediaContentTypes` adds them; anything not on that list won't be served.

## In Routines

A `MediaPlayback` channel is a valid Routine target via the `PlayMedia` action kind, so "Dinner" can dim the lights and start a record in one trigger.

The action stores **what the admin typed** — normally the library-relative path — not the URL it resolves to. `MediaLibraryBaseUrl` changes with hostnames and proxy layout, and a routine saved a year ago shouldn't still point at whatever URL was current then, so `RoutineActionExecutor` resolves through the same `MediaLibraryUrlResolver` at trigger time. A path that no longer resolves (base URL cleared, say) throws and fails the trigger rather than sending the speaker something unfetchable.

Like every other action kind, the value isn't validated at save time — a bad path surfaces when the routine runs, the same way a non-numeric `SetTemperature` value does.

## Not done yet

- No directory browsing — you have to know the relative path to type into the Play box. `UseDirectoryBrowser` on the same file provider would fix that; in the meantime `share.${DOMAIN}` browses the same files, so paths can be read off there and typed in by hand.
- No stop/pause/volume commands — `IHomeAssistantCommandService` only has `PlayMediaAsync` on the media side.
- The library is served without authentication to anything that can reach the API.
