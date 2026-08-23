# Tailscale VPN Architecture

## Summary

Remote access to the home LAN — and therefore every `*.${DOMAIN}` host Traefik
routes to (see [`docs/reverse-proxy-architecture.md`](reverse-proxy-architecture.md))
— is provided by [Tailscale](https://tailscale.com), running **natively on each
of the three Hyper-V hosts** as a subnet router. It is installed and brought up
idempotently by [`provision-0-new-node.yml`](../.github/workflows/provision-0-new-node.yml),
the same "converge on every run" pattern used for `windows_exporter` in the same
workflow. DNS for `${DOMAIN}` is handled by a tailnet-wide split-DNS nameserver
entry that forwards to pfSense's existing Unbound resolver — no changes to
Traefik, Route53, or pfSense were needed.

Three subnet routers, not one, and that is the point: each host advertises the
same LAN CIDR under its own Tailscale hostname (`aerie-<host>`), and Tailscale
supports more than one router for a given CIDR with automatic failover. Losing
one host no longer costs remote access.

## Why on the hosts, and not in the cluster

The obvious instinct — run Tailscale as a workload alongside everything else —
is wrong here, and for a reason that survived the cutover unchanged: **a subnet
router belongs on a machine that owns the LAN interface.**

A pod's network namespace is the cluster's, not the LAN's. Subnet routing needs
the real host's routing table and physical NIC; from inside the cluster the best
a router could advertise is a cluster-internal CIDR that no remote client has any
use for. It would also make remote access depend on the cluster being up, which
inverts the thing remote access is for — the first reason to reach the house from
outside is usually that something in the cluster is unwell.

The three Hyper-V hosts are the machines that own LAN interfaces, and they are
the machines that outlive any cluster rebuild. That is the whole argument, and
it is written down in the workflow step itself. (The same constraint, one layer
down, is why `windows_exporter` is a native install rather than a container —
see [`docs/metrics-architecture.md`](metrics-architecture.md)'s "Windows Server
detour".)

## Components

- **`tailscaled` / Tailscale Windows service** — runs natively on each Hyper-V
  host, installed via silent MSI by Provision 0. Set to Unattended Mode
  (`TS_UNATTENDEDMODE=always`) since these hosts never have an interactive
  desktop session to keep it alive.
- **Subnet routers** — each host advertises the LAN CIDR
  (`TAILSCALE_ADVERTISE_ROUTES`, e.g. `192.168.1.0/24`) via
  `tailscale up --advertise-routes=...`. Any tailnet device can then reach the
  whole LAN, including `${INGRESS_VIP}` and therefore every hostname on it.
- **Per-host tailnet names** — `--hostname=aerie-<host>`, from the workflow's
  own `host` input, so the three devices are distinguishable in the admin
  console and one of them can be identified when a route needs approving.
- **`--accept-routes=false`** — these hosts advertise routes; they do not consume
  anyone else's. A subnet router that accepts routes can end up preferring a
  tailnet path to a LAN it is physically on.
- **Split DNS** — configured once in the [Tailscale admin console](https://login.tailscale.com/admin/dns)
  → DNS → Nameservers: a custom nameserver scoped to `${DOMAIN}`, pointing at
  pfSense's LAN IP (its Unbound resolver). This reuses the `local-zone`/`local-data`
  redirect described in `docs/reverse-proxy-architecture.md` — Tailscale just
  forwards the one domain to it instead of the public resolver. Required because
  these subdomains have **no public DNS records at all** (deliberate — see the
  reverse-proxy doc); a tailnet client with no split DNS gets NXDOMAIN for
  `home.${DOMAIN}`.
- **Remote clients** — laptops/phones running the Tailscale app, logged into the
  same tailnet, with subnet-route acceptance on (default on most platforms;
  explicit `--accept-routes` needed on Linux).

```
[remote laptop/phone] --tailnet mesh--> [3x Hyper-V host: tailscaled, subnet router]
                                              |
                                     advertises LAN CIDR
                                              |
                                        [home LAN] -- pfSense (Unbound resolver,
                                              |         redirects *.${DOMAIN} -> ${INGRESS_VIP})
                                     kube-vip VIP -> Traefik -> api / files / etc.
```

## The Provision 0 step

The `Ensure Tailscale is installed and running` step, which runs on every
dispatch of Provision 0 against a host:

1. Installs the MSI silently if the `Tailscale` service doesn't exist yet
   (`TS_UNATTENDEDMODE=always`, `TS_NOLAUNCH=1` — no GUI to attach to under the
   runner's service account).
2. Ensures the service is running and set to auto-start.
3. Calls `tailscale up` with the desired flags every run, since **`tailscale up`
   flags are not cumulative** — an omitted flag resets to default, not "stays as
   it was."
4. Only passes `--authkey` when the node isn't already authenticated (checked via
   `tailscale status --json`'s `BackendState`). Re-passing `--authkey` on every
   run is tempting for idempotency, but `tailscaled` unconditionally attempts to
   register with a supplied key even when a valid authenticated state already
   exists on disk ([tailscale/tailscale#19501](https://github.com/tailscale/tailscale/issues/19501))
   — so a since-expired reusable key would break *every* run after its expiry,
   not just the one that needed it.

Required GitHub Actions config (Settings → Secrets and variables → Actions):

| Name | Kind | Value |
|---|---|---|
| `TAILSCALE_AUTH_KEY` | Secret | A **reusable, tagged** (not ephemeral) auth key from the [admin console](https://login.tailscale.com/admin/settings/keys). Ephemeral keys de-register the node when the key's session ends — wrong for a permanent subnet router. |
| `TAILSCALE_ADVERTISE_ROUTES` | Variable | The LAN CIDR, e.g. `192.168.1.0/24`. Not a secret. |

Both are read by Provision 0 for every host, so the three routers are configured
from one pair of values rather than three.

## One-time manual setup (not expressible in a workflow)

These live in Tailscale account state, not host state, so they aren't part of the
provisioning job:

1. Create the reusable tagged auth key and the `TAILSCALE_ADVERTISE_ROUTES`
   variable above, then run Provision 0 once.
2. **Approve the advertised subnet route**, per host:
   [admin console → Machines](https://login.tailscale.com/admin/machines) → the
   `aerie-<host>` device → enable the advertised route. Routes sit disabled until
   approved, even from an authenticated node — so a newly provisioned host is on
   the tailnet but routing nothing until this click. An `autoApprovers` entry for
   the key's tag removes the click; see below.
3. **Add the split-DNS nameserver**: admin console → DNS → Nameservers → add a
   nameserver scoped to `${DOMAIN}`, pointing at pfSense's LAN IP. Confirm
   MagicDNS is on. Tailnet-wide and set once — it does not care which host
   answers.
4. **Remove stale devices** after a host rebuild. A rebuilt host joins under its
   `aerie-<host>` name; a device left over from a previous identity is a name in
   the console that answers for nothing.

## Connecting a client

**Laptop** (Windows/Mac/Linux): install from
[tailscale.com/download](https://tailscale.com/download), log in with the same
tailnet account. Runs as a background service — no per-session "connect" step.

**Phone** (iOS/Android): install "Tailscale" from the app store, log in, toggle
on (accept the OS VPN-configuration prompt on first use).

**Subnet route acceptance**: most platforms accept advertised subnet routes by
default; verify in the client's device settings if `*.${DOMAIN}` resolves to the
VIP but the hostname itself doesn't load. Linux needs `--accept-routes` passed
explicitly at `tailscale up`.

Once connected with the route accepted, `https://home.${DOMAIN}` etc. load
exactly as they do on the LAN — the cluster's wildcard certificate is a real
Let's Encrypt certificate (DNS-01 via Route53), so it validates identically
regardless of where the client is.
[`Test-NameResolution.ps1`](../scripts/k3s/Test-NameResolution.ps1) is the check
for this: it detects whether it is running on the tailnet or on the VIP's own
subnet and reports which vantage point it just proved.

## Open ends

- **ACL tags over ad hoc rules.** The auth key is tagged; ACLs written against
  the tag rather than against individual devices are what keeps a host rebuild
  from being an ACL edit. Combined with `autoApprovers` for that tag, a new
  host's route is approved on join rather than by a manual click.
- **A second site or VLAN needs its own advertisement.** Subnet routing is
  per-LAN-segment, not per-server: the three hosts share one CIDR and one
  approval each. A machine on a *different* network needs its own
  `--advertise-routes` for its own CIDR, approved separately.
- **Nothing here is monitored.** No alert fires if a host's route is
  un-approved, if `tailscaled` stops, or if the tailnet loses its last router —
  which would present as "remote access is broken" and nothing else. That is a
  gap, named rather than closed.
