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

The library isn't a share of its own — it's a folder inside the house SMB share, the same one the file browser at `share.${DOMAIN}` serves ([file-share.md](file-share.md)). Both live in [`compose.share.yml`](../compose.share.yml), which mounts `SHARE_PATH` twice: read-write for the GUI, and read-only as `share_ro` for `api`. Two volumes on one device is forced by Docker baking `driver_opts` into a named volume at creation — a volume carries exactly one mount mode — and it's what keeps the API's view read-only no matter what the GUI does. Nothing in Aerie writes to the library.

`compose.prod.yml` runs `api` as a Linux container even though its host is Windows, so `MediaLibrary__RootPath` is an **in-container path** (`/share-ro/<subpath>`), never a UNC path.

**Two variables, with very different failure modes.** Both are GitHub Actions **repo variables** (Settings → Secrets and variables → Actions → Variables), wired into the deploy job's `env:` block in [`cd.yml`](../.github/workflows/cd.yml):

| Variable | What | If it's wrong |
| --- | --- | --- |
| `SHARE_PATH` | The `//server/share` the CIFS driver mounts, e.g. `//NAS/public` | Fails `docker compose up` for the **entire stack**, because the daemon resolves the mount before any container starts |
| `MEDIA_LIBRARY_SUBPATH` | Folder within that share holding the music, e.g. `Music` or `Media/Music` | Startup warning only — Program.cs logs `RootPath ... does not exist` and serves nothing |

That asymmetry is the point of composing the path in the environment rather than mounting the subdirectory directly: a typo in the *music* folder name can't take the house down.

**`MEDIA_LIBRARY_SUBPATH` unset turns media serving off, and that's a security property, not just a default.** The compose file builds the path as `${MEDIA_LIBRARY_SUBPATH:+/share-ro/${MEDIA_LIBRARY_SUBPATH}}`, so an empty variable yields an empty `RootPath` (serving off per "Serving is off entirely" above). Written the obvious way instead — `/share-ro/${MEDIA_LIBRARY_SUBPATH}` — an unset variable would collapse to `/share-ro/`, silently publishing **the entire house share** on an endpoint that is unauthenticated by design.

**The whole layer is optional.** `cd.yml` adds `-f compose.share.yml` only when `SHARE_PATH` is set; otherwise it's omitted entirely and `api` starts with no share mount, `RootPath` stays `""`, and the rest of the stack deploys normally.

**Use an FQDN or an IP for the server, never the bare hostname.** The mount is performed by the Linux Docker VM, not by Windows: its resolver doesn't apply the host's DNS suffix search list and speaks neither NetBIOS nor mDNS, so a name that resolves fine in Explorer fails the deploy with `error resolving passed in network volume address: lookup <host> ...: no such host`. If the record only exists on the LAN resolver, an IP in the variable (or `addr=` in the volume's `o:` options) sidesteps resolution entirely. If the share is temporarily unreachable, unsetting `SHARE_PATH` lets the rest of the stack keep deploying.

**Changing `SHARE_PATH` is not enough on its own.** Docker stores `driver_opts` on the named volume at creation and reuses the existing volume on later deploys, so `aerie_share` and `aerie_share_ro` keep the old device string until removed. After changing it, run `docker volume rm aerie_share aerie_share_ro` on the host (both containers must be down first) and redeploy — remember there are two. Nothing is lost; the volumes hold no data, they're just handles on the remote share. `MEDIA_LIBRARY_SUBPATH` needs none of this: it's an env var, so a redeploy is enough.

`SHARE_PATH` is a variable rather than a secret because it's a LAN path to an already-guest-accessible share. A share needing credentials would want `username=`/`password=` in the volumes' `o:` options, sourced from repo *secrets* instead.

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
