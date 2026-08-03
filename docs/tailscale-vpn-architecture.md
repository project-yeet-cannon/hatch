# Tailscale VPN Architecture

## Summary

Remote access to the home LAN — and therefore every `*.${DOMAIN}` host Caddy routes to (see [`docs/reverse-proxy-architecture.md`](reverse-proxy-architecture.md)) — is provided by [Tailscale](https://tailscale.com), running **natively on the Aerie host** as a subnet router. It's installed and brought up idempotently by [`cd.yml`](../.github/workflows/cd.yml), the same "converge on every deploy" pattern used for `windows_exporter`. DNS for `${DOMAIN}` is handled by a tailnet-wide split-DNS nameserver entry that forwards to pfSense's existing Unbound resolver — no changes to Caddy, Route53, or pfSense were needed.

## Why native, not a container

The obvious instinct — add a `tailscale` service to one of the `compose.*.yml` files — doesn't work on this host. The Aerie host is **Windows Server running Docker Desktop (WSL2 backend)**: every container's network stack is the WSL2 VM's own namespace, not the real Windows host's NIC or the physical LAN. This is the exact constraint documented in [`docs/metrics-architecture.md`](metrics-architecture.md)'s "Windows Server detour", which forced `node_exporter` out of Docker and into a native `windows_exporter` install for the same reason.

A containerized Tailscale would join the tailnet fine, but subnet routing — the whole point here — needs access to the real host's routing table and physical interface. Under Docker Desktop it could only ever advertise the WSL VM's private virtual subnet, never the actual LAN where pfSense, Home Assistant, and everything else lives. Hence: native install, not `compose`.

## Components

- **`tailscaled` / Tailscale Windows service** — runs natively on the Aerie host, installed via silent MSI in `cd.yml`. Set to Unattended Mode (`TS_UNATTENDEDMODE=always`) since this host never has an interactive desktop session to keep it alive.
- **Subnet router** — the Aerie host advertises the LAN CIDR (`TAILSCALE_ADVERTISE_ROUTES`, e.g. `192.168.1.0/24`) via `tailscale up --advertise-routes=...`. Any tailnet device can then reach the whole LAN, not just the host itself.
- **Split DNS** — configured once in the [Tailscale admin console](https://login.tailscale.com/admin/dns) → DNS → Nameservers: a custom nameserver scoped to `${DOMAIN}`, pointing at pfSense's LAN IP (its Unbound resolver). This reuses the `local-zone`/`local-data` redirect already described in `docs/reverse-proxy-architecture.md` — Tailscale just forwards the one domain to it instead of the public resolver. Required because these subdomains have **no public DNS records at all** (deliberate — see the reverse-proxy doc); a tailnet client with no split DNS gets NXDOMAIN for `home.${DOMAIN}`.
- **Remote clients** — laptops/phones running the Tailscale app, logged into the same tailnet, with subnet-route acceptance on (default on most platforms; explicit `--accept-routes` needed on Linux).

```
[remote laptop/phone] --tailnet mesh--> [Aerie host: tailscaled, subnet router]
                                              |
                                     advertises LAN CIDR
                                              |
                                        [home LAN] -- pfSense (Unbound resolver,
                                              |         redirects *.${DOMAIN} -> LAN IP)
                                    Caddy (edge network) -> api / files / etc.
```

## `cd.yml` step

The `Ensure Tailscale is installed and running` step:

1. Installs the MSI silently if the `Tailscale` service doesn't exist yet (`TS_UNATTENDEDMODE=always`, `TS_NOLAUNCH=1` — no GUI to attach to under the runner's service account).
2. Ensures the service is running and set to auto-start.
3. Calls `tailscale up` with the desired flags every run, since **`tailscale up` flags are not cumulative** — an omitted flag resets to default, not "stays as it was."
4. Only passes `--authkey` when the node isn't already authenticated (checked via `tailscale status --json`'s `BackendState`). Re-passing `--authkey` on every deploy is tempting for idempotency, but `tailscaled` unconditionally attempts to register with a supplied key even when a valid authenticated state already exists on disk ([tailscale/tailscale#19501](https://github.com/tailscale/tailscale/issues/19501)) — so a since-expired reusable key would break *every* deploy after its expiry, not just the one that needed it.

Required GitHub Actions config (Settings → Secrets and variables → Actions):

| Name | Kind | Value |
|---|---|---|
| `TAILSCALE_AUTH_KEY` | Secret | A **reusable, tagged** (not ephemeral) auth key from the [admin console](https://login.tailscale.com/admin/settings/keys). Ephemeral keys de-register the node when the key's session ends — wrong for a permanent subnet router. |
| `TAILSCALE_ADVERTISE_ROUTES` | Variable | The LAN CIDR, e.g. `192.168.1.0/24`. Not a secret. |

## One-time manual setup (not expressible in `cd.yml`)

These live in Tailscale account state, not host state, so they aren't part of the deploy job:

1. Create the reusable tagged auth key and the `TAILSCALE_ADVERTISE_ROUTES` variable above, then deploy once.
2. **Approve the advertised subnet route**: [admin console → Machines](https://login.tailscale.com/admin/machines) → the Aerie host → enable the advertised route. Routes sit disabled until approved, even from an authenticated node.
3. **Add the split-DNS nameserver**: admin console → DNS → Nameservers → add a nameserver scoped to `${DOMAIN}`, pointing at pfSense's LAN IP. Confirm MagicDNS is on.

## Connecting a client

**Laptop** (Windows/Mac/Linux): install from [tailscale.com/download](https://tailscale.com/download), log in with the same tailnet account. Runs as a background service — no per-session "connect" step.

**Phone** (iOS/Android): install "Tailscale" from the app store, log in, toggle on (accept the OS VPN-configuration prompt on first use).

**Subnet route acceptance**: most platforms accept advertised subnet routes by default; verify in the client's device settings if `*.${DOMAIN}` resolves to a raw LAN IP but the hostname itself doesn't load. Linux needs `--accept-routes` passed explicitly at `tailscale up`.

Once connected with the route accepted, `https://home.${DOMAIN}` etc. load exactly as they do on the LAN — Caddy's certs are real Let's Encrypt certs already (DNS-01 via Route53), so they validate identically regardless of where the client is.

## Scaling to a multi-server cluster

- **Mesh connectivity is automatic.** Every new server gets the same native-install step; each joins the tailnet as a peer with full connectivity to every other node and to remote clients, no per-pair config.
- **Split DNS is tailnet-wide**, set once — it doesn't care which server answers a given subdomain. Adding services on new hosts still follows the unchanged "Adding a new app" flow in `docs/reverse-proxy-architecture.md`.
- **Subnet routing is per-LAN-segment, not per-server.** A second server on the *same* LAN doesn't need its own advertised route. A server on a *different* network (a second site, a cloud VPS, a separate VLAN) needs its own `--advertise-routes` for its own CIDR, approved separately.
- **HA for the subnet router**: Tailscale supports multiple nodes advertising the same CIDR, with automatic failover — worth setting up once a second box exists on the primary LAN, so losing the Aerie host doesn't also kill remote access.
- **ACL tags over ad hoc rules** as node count grows: tag each server at `tailscale up` time (`--advertise-tags=tag:aerie-server`) and write ACLs against the tag. Combined with `autoApprovers` in the ACL policy for that tag, new servers' routes can be approved automatically instead of needing a manual admin-console click each time.
- **The container caveat is Windows/Docker-Desktop-specific.** A future Linux host doesn't have the WSL2 boundary, but containerized Tailscale there still needs `NET_ADMIN` / `--network=host` and IP-forwarding sysctls for subnet routing to work — a native package (or the official image run explicitly with those privileges) stays simpler than folding it into that host's compose stack.
