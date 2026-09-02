# File Share GUI

A browser front end for the house SMB share at `https://share.${DOMAIN}` — directory browsing, drag-and-drop upload, delete, rename, folder download-as-archive, and search.

## What it is

[dufs](https://github.com/sigoden/dufs), a single-binary static file server with an upload UI, as the `share` Deployment in [`share-deployment.yaml`](../charts/aerie/templates/share-deployment.yaml). One replica, `strategy: Recreate`. No database, no state of its own — every byte it shows lives on the SMB share, so the pod is disposable and nothing here is in the backup set.

[`share-volumes.yaml`](../charts/aerie/templates/share-volumes.yaml) owns **everything** that touches the share, which is two consumers rather than one: this GUI mounts it read-write, and the `api` pod mounts the same device read-only to serve the music library under it to Sonos speakers ([media-library.md](media-library.md)). That is two static PersistentVolume/PersistentVolumeClaim pairs over one device — `aerie-share-rw` and `aerie-share-ro`, both `smb.csi.k8s.io` — because a volume carries exactly one mount mode. The read-only half is held read-only twice over, by the `ro` mount option and by `csi.readOnly`, so the API's view stays read-only regardless of what the GUI does.

Routing is the `share` Ingress in [`ingress.yaml`](../charts/aerie/templates/ingress.yaml): `ingressClassName: traefik`, no `tls:` block of its own, so the wildcard `TLSStore` default is what gets served. pfSense already sends every `*.${DOMAIN}` name to the ingress VIP, so there is no DNS or certificate work in adding one.

### Why not `files.${DOMAIN}`

That name was already taken. The `files` Deployment ([`files-deployment.yaml`](../charts/aerie/templates/files-deployment.yaml)) is the nginx that hosts the kiosk APK, and `https://files.${DOMAIN}/version.json` + `/app-release.apk` are **compiled into every installed tablet** (`UpdateManager.kt`) and baked into the QR provisioning payload. Two backends claiming the same host and path is not an error either routing layer raises — caddy-docker-proxy merged the two labels into one site block and round-robined between them, and two Ingresses naming one host resolve by Traefik's own precedence rules rather than by anything written down here. Either way the failure is silent, and it lands on tablets no one is standing next to. Path-routing the three kiosk files to nginx and everything else to dufs would work, but `share.${DOMAIN}` costs nothing and keeps the kiosk update path untouched.

## Which share gets mounted

`SHARE_HOST` and `SHARE_NAME`, two GitHub Actions **repo variables** (Settings → Secrets and variables → Actions → Variables), declared in [`cluster-config.json`](../scripts/k3s/cluster-config.json) and written into the `aerie-cluster-config` ConfigMap by Provision 4, from which Flux substitutes them into the HelmRelease. The pair is split rather than kept as one `//server/share` string because the PV needs the host on its own for its `volumeHandle`; the template is the one place that joins them back up.

Both are **required**. Unlike the old `SHARE_PATH`, `SHARE_NAME` is the share name alone — no slashes, and no subdirectory-as-mount-prefix trick. Serving a folder *inside* the share is what `MEDIA_LIBRARY_SUBPATH` does, and only for the media library; dufs always browses the share root.

**Use an FQDN or an IP for the server, never the bare hostname.** The mount is performed by the node's CIFS client, not by Windows: its resolver doesn't apply the host's DNS suffix search list and speaks neither NetBIOS nor mDNS, so a name that resolves fine in Explorer leaves the pod stuck in `ContainerCreating` with a resolution failure on the `MountVolume.SetUp` event. If the record only exists on the LAN resolver, put an IP in the variable to sidestep resolution entirely.

**Changing either variable is not enough on its own.** A PersistentVolume's `csi` block is immutable once the object exists, so re-rendering the chart with a new `source` doesn't move the mount — the update is rejected outright. Delete the two PVCs and the two PVs (`aerie-share-rw`, `aerie-share-ro`) and let Flux recreate them. Nothing is lost: both are `persistentVolumeReclaimPolicy: Retain`, they hold no data, and they're just handles on the remote share.

**The GUI's mount is read-write**, unlike the API's, because uploading is the entire point. Credentials come from the `smb-share` Secret via `nodeStageSecretRef` rather than a `guest` mount option — an empty `SMB_PASSWORD` is the guest path, which the driver treats the same way the kernel CIFS client's guest mount does, and a share needing real credentials fills the same two fields with no manifest change. The rw mount options also pin `uid=0,gid=0,file_mode=0664,dir_mode=0775` — a guest CIFS mount otherwise presents everything as owned by an unmapped id, and the client-side permission check can reject a write before the request ever reaches the server.

## Deployment is unconditional, and failure is contained

On the compose host this layer was optional, because the CIFS mounts were resolved by the Docker *daemon* before any container started: one unreachable share failed `docker compose up` for the **entire stack**, so `SHARE_PATH` doubled as an escape hatch that dropped the share and media serving together to keep everything else deploying.

In the cluster the mount is per-pod, staged by the CSI driver on the node the pod lands on. An unreachable or misconfigured share now leaves the `share` and `api` pods in `ContainerCreating` with the reason on their events, and touches nothing else — so the variables are plain required values with no off switch, and the blast radius that made the switch worth having is gone.

**A missing `share` Ingress no longer fails at the TLS layer.** The certificate is the wildcard `TLSStore` default terminated at Traefik's entrypoint, not one issued per hostname, so the handshake succeeds for any `*.${DOMAIN}` name and an unrouted host answers with Traefik's 404 instead of `ERR_SSL_PROTOCOL_ERROR`. Worth knowing, because the old advice — debug it as a certificate problem — now points the wrong way.

## The login is a placeholder

`--auth admin:password@/:rw` is assembled from `share.auth` in [`values.yaml`](../charts/aerie/values.yaml), a dummy credential checked into source on purpose — the same call, for the same reason, as the two admin logins in [`admin-secrets.yaml`](../deploy/cluster/observability/controllers/admin-secrets.yaml): a known default an operator changes, rather than one installation's password shipped to every other. So the app needs no secret wireup to stand up. Defining only that rule and no anonymous (`@`-prefixed) one means *every* request authenticates — there is no unauthenticated read.

But unlike Kuma's, this login gates **write** access to real files: anyone on the LAN or Tailscale who reads this repo can upload, overwrite, and delete. Move it through ESO — the machinery [`secrets-architecture.md`](secrets-architecture.md) describes, already carrying every other credential in the cluster — before the share holds anything worth protecting.

The `--allow-upload`/`--allow-delete`/`--allow-search`/`--allow-archive` flags and the `:rw` in the auth rule are both load-bearing: the flags decide which operations exist at all, the rule decides who may use them. They're listed individually rather than as `-A`, which would also turn on `--allow-symlink` and let listings follow symlinks out of the share.

## The app-picker link

The picker (`src/Aerie.Web/apps/home`, served at `home.${DOMAIN}`) is built and shipped like every other app, but it still can't be told the base domain: Aerie redeploys under someone else's, so `${DOMAIN}` is not a value this repo holds. The Files entry is one of four that aren't same-origin - `share` plus the three observability services - and each carries a `subdomain` in [`src/apps.ts`](../src/Aerie.Web/apps/home/src/apps.ts) instead of an href. [`siblingOrigin`](../src/Aerie.Web/apps/home/src/lib/siblingOrigin.ts) resolves it against whatever domain the page is being served from, so `home.example.org` yields `https://share.example.org/` and a redeploy under a different domain needs no edit. Where there is no domain to borrow - an IP, or `localhost` in development - it returns null and the tile renders without a link, saying so.
