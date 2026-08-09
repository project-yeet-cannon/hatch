# Media Library

How a music file on the house SMB share becomes something a Sonos speaker will play, via Aerie's `Speaker` devices and their `MediaPlayback` channels — and how files get onto that share in the first place, via the web GUI at `media.${DOMAIN}`.

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

**The mount is optional and lives in a separate compose file.** `MediaLibrary__RootPath`, the `media` volume, and its mount into `api` are all defined in `compose.media.yml`, not `compose.prod.yml`. `.github/workflows/cd.yml` layers that file in with `-f compose.media.yml` only when the `MEDIA_LIBRARY_SHARE` repo variable is set; otherwise it's omitted entirely and `api` starts with no media mount. This split exists because the CIFS mount is resolved and created by the Docker *daemon* before any container starts — an unresolvable or misconfigured share fails `docker compose up` for the **entire stack** (db, caddy, everything), not just media serving. With the variable unset, `MediaLibraryOptions.RootPath` stays `""` and the API just logs a warning and serves nothing (see Program.cs and "Serving is off entirely" above) — the rest of the stack deploys normally.

**Which share gets mounted is not in source.** `device:` interpolates `MEDIA_LIBRARY_SHARE`, a GitHub Actions **repo variable** (Settings → Secrets and variables → Actions → Variables), wired into the deploy job's `env:` block in `.github/workflows/cd.yml` alongside `DOMAIN`, `HA_HOST` and friends. Set it to the share in `//server/share` form, e.g. `//NAS/Music`. A subdirectory of a share works too — `//NAS/public/music` mounts just `music` out of the `public` share, which the kernel's CIFS client handles as a mount prefix.

**Use an FQDN or an IP for the server, never the bare hostname.** The mount is performed by the Linux Docker VM, not by Windows: its resolver doesn't apply the host's DNS suffix search list and speaks neither NetBIOS nor mDNS, so a name that resolves fine in Explorer fails the deploy with `error resolving passed in network volume address: lookup <host> ...: no such host`. Note this makes the mount depend on whatever DNS the daemon reaches — if the record only exists on the LAN resolver, an IP in the variable (or `addr=` in the volume's `o:` options) is the way to sidestep resolution entirely. If the share is temporarily unreachable, unsetting `MEDIA_LIBRARY_SHARE` lets the rest of the stack keep deploying while media serving stays off.

**Changing the variable is not enough on its own.** Docker stores `driver_opts` on the named volume at creation and reuses the existing volume on later deploys, so `aerie_media` keeps the old device string until it's removed. After changing the variable, run `docker volume rm aerie_media` on the host (the api container must be down first) and redeploy. Nothing is lost — the volume holds no data, it's just a handle on the remote share.

It's a variable rather than a secret because it's a LAN path to an already-public-read share. A share needing credentials would want `username=`/`password=` in the volume's `o:` options, sourced from repo *secrets* instead.

### Browsing and uploading: `media.${DOMAIN}`

The share also gets a browser UI, served by [dufs](https://github.com/sigoden/dufs) as the `media-gui` service in `compose.media.yml` — directory browsing, drag-and-drop upload, delete, rename, folder download-as-archive, and search. It follows the standard pattern in [reverse-proxy-architecture.md](reverse-proxy-architecture.md): `edge` network, two `caddy` labels, no published port, so Caddy issues the cert and routes to it with no DNS or Caddy config change. Because it lives in `compose.media.yml`, it appears and disappears with `MEDIA_LIBRARY_SHARE` exactly like the API's mount does.

**It mounts the share read-write, through a second volume.** `media_rw` mounts the same `MEDIA_LIBRARY_SHARE` device with `rw` where `media` uses `ro`. Two volumes are required rather than one loosened set of options: Docker bakes `driver_opts` into a named volume at creation, so a volume carries exactly one mount mode — and the API's view is meant to stay read-only regardless, since nothing in Aerie writes to the library. The rw options also pin `uid=0,gid=0,file_mode=0664,dir_mode=0775`; a guest CIFS mount otherwise presents files as owned by an unmapped id, and the client-side permission check can reject a write before it ever reaches the server. Guest works for writes here only because the share itself permits anonymous writes — if that changes, both volumes want `username=`/`password=` from repo *secrets*.

Note that everything about `aerie_media` needing `docker volume rm` after a variable change (above) applies to `aerie_media_rw` too — remove both.

**The login is a placeholder.** `--auth admin:password@/:rw` is checked into `compose.media.yml`, the same way the Uptime Kuma admin credentials are in `compose.observability.yml`, so the app needs no secret wireup to stand up. Defining only that rule, with no anonymous rule, means *every* request authenticates — there is no unauthenticated read. But unlike Kuma's, this login gates write access to the actual music share: anyone on the LAN or Tailscale who reads this repo can upload, overwrite, and delete. Replace it with a GitHub Actions secret interpolated into that `--auth` value before this subdomain carries anything worth protecting.

The `--allow-upload`/`--allow-delete`/`--allow-search`/`--allow-archive` flags and the `:rw` in the auth rule are both load-bearing: the flags decide which operations exist at all, the rule decides who may use them. They're listed individually rather than as `-A`, which would also turn on `--allow-symlink` and let listings follow symlinks out of the share.

This is separate from the API's own `/media` endpoint, which stays read-only and unauthenticated — that one exists for the speakers, which can't log in.

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

- No directory browsing *in the API's `/media` endpoint* — you still have to know the relative path to type into the Play box. `media.${DOMAIN}` browses the same files, so paths can be read off there, but the two aren't linked; `UseDirectoryBrowser` on the same file provider would close the gap.
- No stop/pause/volume commands — `IHomeAssistantCommandService` only has `PlayMediaAsync` on the media side.
- The library is served without authentication to anything that can reach the API. (The `media.${DOMAIN}` GUI does require a login, but a placeholder one that's checked into source — see above.)
