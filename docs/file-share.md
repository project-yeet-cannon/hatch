# File Share GUI

A browser front end for the house SMB share at `https://share.${DOMAIN}` — directory browsing, drag-and-drop upload, delete, rename, folder download-as-archive, and search.

## What it is

[dufs](https://github.com/sigoden/dufs), a single-binary static file server with an upload UI, as the `share` service in [`compose.share.yml`](../compose.share.yml). No database, no state of its own — every byte it shows lives on the SMB share, so the container is disposable and nothing here is in the backup set.

That compose file owns **everything** that touches the share, which is two consumers rather than one: this GUI mounts it read-write, and the `api` container mounts the same device read-only to serve the music library under it to Sonos speakers ([media-library.md](media-library.md)). Two volumes on one device is forced by Docker baking `driver_opts` into a named volume at creation, so a volume carries exactly one mount mode; it also keeps the API's view read-only regardless of what the GUI does.

Routing follows the standard pattern in [reverse-proxy-architecture.md](reverse-proxy-architecture.md): `edge` network, two `caddy` labels, port never published. Caddy issues the cert via Route53 DNS-01 and pfSense's zone redirect already resolves the name, so there's no DNS or Caddy config to change.

### Why not `files.${DOMAIN}`

That name was already taken. The `files` service in [`compose.prod.yml`](../compose.prod.yml) is the nginx that hosts the kiosk APK, and `https://files.${DOMAIN}/version.json` + `/app-release.apk` are **compiled into every installed tablet** (`UpdateManager.kt`) and baked into the QR provisioning payload. Two containers carrying the same `caddy: files.${DOMAIN}` label get merged by caddy-docker-proxy into a single site block with two `reverse_proxy` directives, which round-robins between them — silently breaking self-update on tablets no one is standing next to. Path-routing the three kiosk files to nginx and everything else to dufs would work (with mutually exclusive matchers, so directive order can't matter), but `share.${DOMAIN}` costs nothing and keeps the kiosk update path untouched.

## Which share gets mounted

`SHARE_PATH`, a GitHub Actions **repo variable** (Settings → Secrets and variables → Actions → Variables), wired into the deploy job's `env:` block in [`cd.yml`](../.github/workflows/cd.yml). Set it to the share in `//server/share` form, e.g. `//NAS/public`. A subdirectory works too — `//NAS/public/docs` mounts just `docs` out of the `public` share, which the kernel's CIFS client handles as a mount prefix.

**Use an FQDN or an IP for the server, never the bare hostname.** The mount is performed by the Linux Docker VM, not by Windows: its resolver doesn't apply the host's DNS suffix search list and speaks neither NetBIOS nor mDNS, so a name that resolves fine in Explorer fails the deploy with `error resolving passed in network volume address: lookup <host> ...: no such host`. If the record only exists on the LAN resolver, put an IP in the variable (or `addr=` in the volume's `o:` options) to sidestep resolution entirely.

**Changing the variable is not enough on its own.** Docker stores `driver_opts` on the named volume at creation and reuses the existing volume on later deploys, so `aerie_share` and `aerie_share_ro` keep the old device string until removed. After changing the variable, run `docker volume rm aerie_share aerie_share_ro` on the host (both containers must be down first) and redeploy — remember there are two, one per mount mode. Nothing is lost; the volumes hold no data, they're just handles on the remote share.

**The GUI's mount is read-write**, unlike the API's, because uploading is the entire point. Guest works because the share already permits anonymous writes; a share needing credentials wants `username=`/`password=` in the volumes' `o:` options, sourced from repo *secrets* rather than variables. The rw options also pin `uid=0,gid=0,file_mode=0664,dir_mode=0775` — a guest CIFS mount otherwise presents everything as owned by an unmapped id, and the client-side permission check can reject a write before the request ever reaches the server.

## Deployment is conditional, and failure is loud

`compose.share.yml` is layered into the deploy only when `SHARE_PATH` is set; `cd.yml` omits it entirely otherwise.

This split exists because the CIFS mounts are resolved and created by the Docker *daemon* before any container starts: an unresolvable or misconfigured share fails `docker compose up` for the **entire stack** (db, caddy, everything), not just this GUI. If the share is temporarily unreachable, unsetting `SHARE_PATH` lets the rest of the stack keep deploying — at the cost of media serving too, since both now hang off the same variable.

**With the variable unset, `share.${DOMAIN}` fails at the TLS layer, not with a 404.** No container means no label, which means Caddy never issues a cert for the name, so the handshake dies at `tlsv1 alert internal error` and browsers report `ERR_SSL_PROTOCOL_ERROR`. The app-picker link is unconditional, so it points at a dead host until the variable is set. Worth knowing before debugging it as a certificate problem.

## The login is a placeholder

`--auth admin:password@/:rw` is checked into `compose.share.yml`, the same way the Uptime Kuma admin credentials are in `compose.observability.yml`, so the app needs no secret wireup to stand up. Defining only that rule and no anonymous (`@`-prefixed) one means *every* request authenticates — there is no unauthenticated read.

But unlike Kuma's, this login gates **write** access to real files: anyone on the LAN or Tailscale who reads this repo can upload, overwrite, and delete. Replace it with a GitHub Actions secret interpolated into that `--auth` value before the share holds anything worth protecting.

The `--allow-upload`/`--allow-delete`/`--allow-search`/`--allow-archive` flags and the `:rw` in the auth rule are both load-bearing: the flags decide which operations exist at all, the rule decides who may use them. They're listed individually rather than as `-A`, which would also turn on `--allow-symlink` and let listings follow symlinks out of the share.

## The app-picker link

The picker (`src/Aerie.Web/index.html`, served at `home.${DOMAIN}`) is a static file copied straight into `wwwroot/apps` by `Aerie.Api.csproj` with no build step, so it can't interpolate `${DOMAIN}` the way compose labels do. The Files entry is the only one that isn't same-origin: it carries `data-subdomain="share"`, and a small script at the bottom of the page rewrites the href against whatever domain the page is currently being served from, falling back to the literal href when the host is an IP or a single label. Any future entry pointing at a sibling subdomain (`status`, `logs`, `metrics`) can use the same attribute instead of hardcoding a host.
