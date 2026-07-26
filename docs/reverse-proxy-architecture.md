# Reverse Proxy & Subdomain Architecture

## Summary

This repo hosts all apps on the home server behind a single Caddy reverse proxy (`containers/caddy`, service `caddy` in [`compose.prod.yml`](../compose.prod.yml)). Caddy is built with [`caddy-docker-proxy`](https://github.com/lucaslorentz/caddy-docker-proxy) and [`caddy-dns/route53`](https://github.com/caddy-dns/route53), so:

- **Routing** is driven entirely by Docker container labels — Caddy watches the Docker socket and rebuilds its config whenever a labeled container starts/stops. There is no central Caddyfile to hand-edit.
- **TLS** certs are issued automatically via Let's Encrypt DNS-01 challenges against Route53. Because it's DNS-01, subdomains never need to be publicly resolvable or have a public A record — the home server's IP stays off the public internet entirely.
- **Local resolution**: pfSense's DNS Resolver (Unbound) redirects the entire base domain to the server's LAN IP via a `local-zone`/`local-data` pair in Services → DNS Resolver → General Settings → Custom options (each line prefixed `server:`, matching the convention pfBlockerNG already uses in that box on this install):

  ```text
  server:local-zone: "<domain>." redirect
  server:local-data: "<domain>. A <lan-ip>"
  ```

  This covers every subdomain automatically — no DNS edit needed for new ones. Note pfSense's own Host Override GUI does *not* support a literal wildcard `*` entry (its hostname field rejects `*`), and per-host overrides can't coexist with the zone above: Unbound's `redirect` zone type only permits a single `local-data` entry, at the zone apex — any Host Override for a subdomain of the same domain produces a second, non-apex entry and `unbound-checkconf` will fail the whole config. Any prior individual overrides (e.g. one per subdomain) must be deleted once this is in place.
- **Secrets** (Route53 credentials, ACME email, base domain) are injected at deploy time from GitHub Actions secrets/variables into the `docker compose up -d` environment in [`cd.yml`](../.github/workflows/cd.yml) — nothing sensitive lives on the server's filesystem.

All app services and `caddy` share a single external Docker network, `edge`, created once on the server (`docker network create edge`) and referenced as `external: true` in compose — it's shared across every app stack, not owned by any one of them.

## Adding a new app

To expose a new service (e.g. `immich`, `dashboard`) at `<name>.<domain>`:

1. Add the service to `compose.prod.yml` (or its own compose file, if it doesn't already live in this repo).
2. Attach it to the `edge` network, in addition to whatever private network it needs for its own dependencies:
   ```yaml
   networks:
     - edge
   ```
3. Add two labels:
   ```yaml
   labels:
     caddy: <name>.${DOMAIN}
     caddy.reverse_proxy: "{{upstreams <container-port>}}"
   ```
4. Don't publish the service's port to the host — Caddy reaches it directly over `edge` by container name.
5. Deploy. Caddy detects the new labels, requests a cert via Route53 DNS-01, and starts routing — no Caddy config changes, no DNS changes (the pfSense wildcard override already covers the new hostname).

That's the whole process — steps 2–4 mirror what's already done for the `api` service in `compose.prod.yml`.

## Aliasing a subdomain to a path on an existing app

Some apps (e.g. `dashboard`) aren't separate services — they're static SPAs served by the `api` container under a path like `/apps/dashboard/`. To give one of these its own subdomain (e.g. `kiosk.${DOMAIN}` → `/apps/dashboard/`), add a second, numbered `caddy_1`-prefixed label block to the *same* container rather than standing up a new service:

```yaml
labels:
  caddy: home.${DOMAIN}
  caddy.reverse_proxy: "{{upstreams 8080}}"
  caddy_1: kiosk.${DOMAIN}
  caddy_1.@root.path: /
  caddy_1.rewrite: "@root /apps/dashboard/"
  caddy_1.reverse_proxy: "{{upstreams 8080}}"
```

`caddy-docker-proxy` treats each `caddy_<N>` label as an independent site block on the same container. The `@root` matcher only rewrites the bare `/` request to `/apps/dashboard/`; everything else (hashed asset requests, API calls like `/api/dashboard`) passes straight through to the same upstream unmodified. This only works cleanly because the target SPA already emits root-absolute asset URLs baked in at build time (Vite's `base: '/apps/dashboard/'`) and has no client-side router — if a future aliased app uses relative asset paths or client-side routing, the rewrite/matcher will need to be broader (or the app served from a real subdomain instead).

## Notes / gotchas

- `edge` must exist on the server *before* the first `docker compose up -d` that references it — it's declared `external: true` so compose won't create or manage it.
- The `caddy` container mounts `/var/run/docker.sock`, which is equivalent to root on the host. Keep its image (and the two plugins it's built with) intentionally minimal, and don't add other capabilities to that container.
- Global Caddy options (ACME email, default DNS provider) are set via labels on the `caddy` service itself (`caddy.email`, `caddy.acme_dns`), not a config file — see `caddy` service in `compose.prod.yml`.
- Route53 IAM credentials should stay scoped to `_acme-challenge.*` TXT records on the one hosted zone, per the policy set up when this was first configured.
