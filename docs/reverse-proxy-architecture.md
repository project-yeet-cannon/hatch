# Reverse Proxy & Subdomain Architecture

## Summary

Every `*.${DOMAIN}` hostname in the house is served by **Traefik**, the ingress
controller k3s bundles, reached at a single virtual IP that kube-vip advertises
onto the LAN. Nothing about routing is host-specific any more: a hostname is an
`Ingress` object in git, and which node answers it is a scheduling detail.

- **Routing** is `Ingress` objects, reconciled by Flux
  ([`docs/delivery-architecture.md`](delivery-architecture.md)). Traefik's
  `kubernetesIngress` provider watches the API server and rebuilds its own
  routing table; there is no Traefik config file to hand-edit, and no proxy
  container watching a Docker socket.
- **One address for all of it.** Traefik's Service is a `LoadBalancer` pinned to
  `${INGRESS_VIP}` by a `kube-vip.io/loadbalancerIPs` annotation
  ([`traefik-helmchartconfig.yaml`](../deploy/cluster/infrastructure/config/traefik-helmchartconfig.yaml)),
  and kube-vip answers ARP for that address from whichever node currently holds
  it. Losing a node moves the VIP; it does not move the hostnames.
- **TLS** is one wildcard certificate — `*.${DOMAIN}` and `${DOMAIN}` — issued
  by cert-manager from Let's Encrypt over a **Route53 DNS-01** challenge
  ([`wildcard-certificate.yaml`](../deploy/cluster/infrastructure/config/wildcard-certificate.yaml)).
  Because it is DNS-01, no subdomain ever needs a public A record, and the
  house's IP stays off the public internet entirely.
- **Local resolution**: pfSense's DNS Resolver (Unbound) redirects the entire
  base domain to the VIP. Unchanged in shape since the Caddy era — only the
  address it points at changed, at [cutover](plans/swarm/phase-7-cutover.md).

## The path a request takes

```
[browser / tablet / Sonos]
   │  home.${DOMAIN}?
   ▼
pfSense Unbound  ──── local-zone redirect ────►  ${INGRESS_VIP}
   │
   ▼
kube-vip (DaemonSet, one node holds the VIP and answers ARP for it)
   │
   ▼
Traefik  ── :80 ──► permanent redirect to :443
         ── :443 ─► TLS terminated with the default wildcard cert
   │
   ├── matches an Ingress rule by Host + path
   ├── applies whatever Middlewares that rule's annotation names
   ▼
Service ──► pod
```

Two properties of that picture are worth stating outright, because both used to
be true of a single machine and are now true of a cluster:

- **Nothing publishes a port to a host.** A workload is reachable because a
  `Service` selects it and an `Ingress` names that Service, not because anything
  bound `0.0.0.0:8080` somewhere.
- **The VIP is not a machine.** `${INGRESS_VIP}` is a lease, not a NIC address.
  It is the only address the house's DNS knows about, and it is deliberately
  distinct from any node's own address so that a node rebuild is invisible from
  the LAN.

## The hostnames

Seven, all covered by the one wildcard certificate and the one resolver entry:

| Host | Serves | Defined in |
|---|---|---|
| `home.${DOMAIN}` | the API and every family app it hosts | [`charts/aerie/templates/ingress.yaml`](../charts/aerie/templates/ingress.yaml) |
| `kiosk.${DOMAIN}` | the same API, root-rewritten to the dashboard SPA | same |
| `files.${DOMAIN}` | the published-app manifest and bundles | same |
| `share.${DOMAIN}` | dufs, in front of the house share | same |
| `status.${DOMAIN}` | Uptime Kuma | [`ingress-status.yaml`](../deploy/cluster/observability/config/ingress-status.yaml) |
| `logs.${DOMAIN}` | OpenSearch Dashboards | [`ingress-logs.yaml`](../deploy/cluster/observability/config/ingress-logs.yaml) |
| `metrics.${DOMAIN}` | Grafana | [`kube-prometheus-stack.yaml`](../deploy/cluster/observability/controllers/kube-prometheus-stack.yaml)'s `grafana.ingress` |

[`Test-NameResolution.ps1`](../scripts/k3s/Test-NameResolution.ps1) asserts all
seven — that each resolves to `${INGRESS_VIP}` through the LAN resolver *and*
that the socket actually reached that address. A 200 proves something answered;
only the address proves it was the cluster.

## TLS: one certificate, served by default

`cert-manager` renews the wildcard into the `aerie-wildcard-tls` Secret in
`kube-system`, and
[`traefik-tlsstore.yaml`](../deploy/cluster/infrastructure/config/traefik-tlsstore.yaml)
names that Secret as the `default` `TLSStore`'s `defaultCertificate`.

**This is why no `Ingress` in the repo carries a `tls:` block.** A router that
names no certificate of its own gets the store default, which is the wildcard —
so adding a hostname needs no certificate work at all, and there is no
per-hostname issuance to wait on, rate-limit, or debug. That default is the
entire argument for having bought a wildcard rather than per-name certificates.

The `websecure` entrypoint terminates TLS
(`ports.websecure.http.tls.enabled: true`), and `web` is a permanent redirect to
it, so plain HTTP to any hostname is a 308 rather than a second code path.

The Route53 IAM credentials stay scoped to `_acme-challenge.*` TXT records on
the one hosted zone, and reach cert-manager as the `route53-credentials` Secret
that External Secrets syncs from SSM — see
[`docs/secrets-architecture.md`](secrets-architecture.md).

## Local resolution (pfSense Unbound)

**Services → DNS Resolver → General Settings → Custom options** — each line
prefixed `server:`, matching the convention pfBlockerNG already uses in that box
on this install:

```text
server:local-zone: "<domain>." redirect
server:local-data: "<domain>. IN A <ingress-vip>"
```

This covers every subdomain automatically — no DNS edit for a new one. Two
constraints on that pair, both learned the hard way and both still true:

- pfSense's own Host Override GUI does **not** accept a literal wildcard `*`
  (its hostname field rejects it).
- Unbound's `redirect` zone type permits a single `local-data` entry, at the
  zone apex. Any Host Override for a subdomain of the same domain produces a
  second, non-apex entry and `unbound-checkconf` fails the whole config. Prior
  per-subdomain overrides must be deleted once this pair is in place.

Remote clients get the same answer through Tailscale's split DNS, which
forwards `${DOMAIN}` to this same resolver — see
[`docs/tailscale-vpn-architecture.md`](tailscale-vpn-architecture.md).

## Adding a new hostname

To expose a new service at `<name>.<domain>`:

1. Give it a `Service` in whatever chart or Kustomization owns it.
2. Add an `Ingress` naming `ingressClassName: traefik`, one rule with
   `host: <name>.{{ .Values.domain }}` (or `<name>.${DOMAIN}` in a plain
   manifest), and a backend pointing at that Service and port.
3. Commit. Flux applies it; Traefik picks it up from the API server within
   seconds of the apply.

That is the whole process. **No certificate step** (the wildcard TLSStore
default covers it), **no DNS step** (the Unbound redirect covers it), and no
node-level anything. `ingressClassName: traefik` is k3s's own bundled
IngressClass, which is marked default — the observability Ingresses omit the
field and rely on that; the chart's set it explicitly. Either is fine, as long
as no second IngressClass ever appears.

## Path rewriting and other per-route behaviour

What Caddy expressed as extra `caddy_<N>` label blocks on a container is now a
Traefik `Middleware` CR plus an annotation on the route that wants it.

The kiosk tablet's pinned URL is the live example:
[`middleware-kiosk.yaml`](../charts/aerie/templates/middleware-kiosk.yaml)
declares a `replacePathRegex` from `^/$` to `/apps/dashboard/`, and the `kiosk`
Ingress names it in
`traefik.ingress.kubernetes.io/router.middlewares`. `replacePathRegex`, not
`redirectRegex`: Caddy rewrote internally, server-side, and a redirect would
change the tablet's address bar — breaking the pinned URL the kiosk shell loads
on boot.

Three sharp edges live in that annotation, and each one fails *silently*:

- **The name format is `<namespace>-<name>@kubernetescrd`, and it is
  unforgiving.** A bare name is not found, and the route then serves without the
  middleware. For the kiosk that looks like the tablet showing the API landing
  page; for
  [`middleware-auth.yaml`](../charts/aerie/templates/middleware-auth.yaml) it
  looks exactly like success, because the route serves *unauthenticated*. Only a
  302 from an un-enrolled client proves the auth middleware is attached — see
  [`docs/auth-architecture.md`](auth-architecture.md).
- **Order matters, and the annotation is a comma-separated list applied in
  order.** Auth is named before the rewrite: rewriting a request that is about
  to be refused wastes the work and puts the rewritten path into the return-to
  the sign-in shell sends the client back to.
- **Two Ingresses on the same host are ranked by rule length**, not by file
  order. `PathPrefix(/apps/docs)` outranks `PathPrefix(/)`, which is what makes
  the auth canary work. If that ever inverts, the gated prefix serves ungated.

## Notes / gotchas

- **`prune: true` is live here.** Deleting an `Ingress` from git deletes the
  route from the cluster on the next reconcile. `git rm` is a production action
  on this path.
- **A missing substitution is an empty host, not an error.** `${DOMIAN}` in an
  Ingress reconciles happily into a rule with no host — which matches nothing,
  or worse, matches everything. See the substitution notes in
  [`docs/delivery-architecture.md`](delivery-architecture.md).
- **No proxy holds a Docker socket any more.** The single most privileged thing
  in the old design — Caddy mounting `/var/run/docker.sock`, equivalent to root
  on the host — has no equivalent here. Traefik reads the Kubernetes API through
  a ServiceAccount scoped to what an ingress controller needs.
- **kube-vip needs the right interface.** `vip_interface` comes from
  `${NODE_INTERFACE}` in the cluster ConfigMap; a wrong value is a VIP that is
  never ARPed for and a house that resolves correctly to an address nothing
  answers.
