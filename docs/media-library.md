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

### Deployment: the API runs in a Linux container

`compose.prod.yml` runs `api` as a Linux container even though its host is Windows, so `MediaLibrary__RootPath` is the **in-container mount point** (`/media-library`), never a UNC path. The share is mounted there read-only by the `media` volume, which uses the CIFS driver with guest access to match the share's public read permissions. `:ro` on both the mount and the CIFS options is deliberate — nothing in Aerie writes to the library.

**Which share gets mounted is not in source.** `device:` interpolates `MEDIA_LIBRARY_SHARE`, a GitHub Actions **repo variable** (Settings → Secrets and variables → Actions → Variables), wired into the deploy job's `env:` block in `.github/workflows/cd.yml` alongside `DOMAIN`, `HA_HOST` and friends. Set it to the share in `//server/share` form, e.g. `//NAS/Music`.

It's a variable rather than a secret because it's a LAN path to an already-public-read share. A share needing credentials would want `username=`/`password=` in the volume's `o:` options, sourced from repo *secrets* instead.

The reference is written `${MEDIA_LIBRARY_SHARE:?...}`, so a deploy with the variable unset fails immediately with that message. Without the `:?`, compose would substitute an empty string and the failure would surface later as an opaque CIFS mount error.

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

## Not done yet

- No directory browsing — you have to know the relative path. `UseDirectoryBrowser` on the same file provider would fix that.
- No `PlayMedia` routine action, so speakers can't take part in Routines (`METRIC_TO_KIND` in the admin RoutinesPage has no `MediaPlayback` entry).
- No stop/pause/volume commands — `IHomeAssistantCommandService` only has `PlayMediaAsync` on the media side.
- The library is served without authentication to anything that can reach the API.
