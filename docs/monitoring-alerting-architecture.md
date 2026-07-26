# Monitoring/Logging/Alerting Architecture

## Summary

A self-hosted logging + status + alerting stack, delivered as a new `compose.observability.yml` that merges alongside the existing [`compose.prod.yml`](../compose.prod.yml) (`docker compose -f compose.prod.yml -f compose.observability.yml up -d`), kept deliberately decoupled from the app stack so it can be stood up and iterated on independently.

Decisions locked in for this round:

- **Log store/search**: OpenSearch + OpenSearch Dashboards (full-text search, accepted the heavier JVM footprint over Loki).
- **Log shipping**: Fluent Bit tails Docker's own `json-file` logs on the host (`/var/lib/docker/containers/*/*.log`) rather than replacing Docker's logging driver — `docker logs` keeps working locally even if OpenSearch is down, and app containers need zero changes (they just log to stdout as they already do).
- **Status page**: Uptime Kuma, using its native Home Assistant notification provider — no separate push service needed since HA is already wired into this stack (same `ha_host`/`ha_token` pattern as `Aerie.Api`).
- **Log-based alerting**: OpenSearch's Alerting plugin, via a webhook destination that calls HA's REST API directly — no relay service needed.
- **Scope**: this repo's containers only (`db`, `api`, `caddy`) for the first pass. Broader homelab ingestion (pfSense, HA itself, etc.) is a later extension, not blocked by anything here.
- **Auth: none, this round.** Every new UI (Dashboards, and OpenSearch's own API) is exposed with `plugins.security.disabled=true` / `DISABLE_SECURITY_DASHBOARDS_PLUGIN=true` and **no** Caddy `basic_auth` layer. This mirrors how [`reverse-proxy-architecture.md`](reverse-proxy-architecture.md) already reasons about exposure: subdomains get real TLS certs via Route53 DNS-01, but are never publicly resolvable — only pfSense's internal wildcard override resolves them — so they're LAN-only in practice today. Given that, and that the LAN is fully trusted, skipping auth is a legitimate smaller first increment. It is not free of cost: **if the server's exposure model ever changes** (a real public A record gets added for anything, a guest/IoT VLAN gets bridged onto the same network, split-tunnel VPN access is granted to someone not fully trusted), auth needs to be added to these three services before that happens. Adding it later is cheap — see [Deferred](#deferred--not-in-this-round) — so this is a real trade-off, not a permanent one.
- Uptime Kuma is the one exception: it requires a username/password on first launch with no supported way to disable that entirely, so it keeps its own lightweight login regardless. No Caddy-layer auth is added on top of it.

## Architecture

```text
[api / db / caddy containers] --stdout--> json-file logs on host (/var/lib/docker/containers/*/*.log)
                                              |
                                        Fluent Bit (tails log dir, ships to OpenSearch)
                                              |
                                         OpenSearch  <---- OpenSearch Dashboards (logs.${DOMAIN}, no auth)
                                              |
                                     Alerting plugin (log-pattern monitors)
                                              |
                                    webhook -> HA /api/services/notify/*  -> your phone

[api / db / caddy / HA] <--health checks-- Uptime Kuma (status.${DOMAIN}, own login only)
                                              |
                                  native HA notification provider -> your phone
```

Two independent alert paths, both terminating in Home Assistant's existing notify service: Uptime Kuma answers "is it up," OpenSearch Alerting answers "is it up but throwing errors."

## Components

| Service | Image | Role | Networks |
| --- | --- | --- | --- |
| `uptime-kuma` | `louislam/uptime-kuma:2.4.0-slim` | Health checks, public status page, HA-native alerting | `local`, `edge` |
| `opensearch` | `opensearchproject/opensearch:3.7.0` | Log store + search + alerting engine | `observability` |
| `opensearch-dashboards` | `opensearchproject/opensearch-dashboards:3.7.0` | Log inspection UI | `observability`, `edge` |
| `fluent-bit` | `fluent/fluent-bit:4.2.7` | Tails host container logs, ships to OpenSearch | `observability` |

(`-slim` on Uptime Kuma skips the bundled Chromium used only for browser-based monitors, which we don't need — smaller image, less RAM.)

New internal network `observability` (bridge, not `external`) isolates OpenSearch/Dashboards/Fluent Bit traffic from the `local` network the app stack uses; Dashboards and Kuma additionally join `edge` to get a Caddy-routed subdomain, following the existing pattern in [`reverse-proxy-architecture.md`](reverse-proxy-architecture.md).

## Phase 1 — Status page + alerting (Uptime Kuma)

The fastest path to a working alert end-to-end, and needs nothing else in this doc to be true first.

1. Create `compose.observability.yml`:

   ```yaml
   services:
     uptime-kuma:
       image: louislam/uptime-kuma:2.4.0-slim
       restart: always
       networks:
         - local
         - edge
       volumes:
         - uptime_kuma_data:/app/data
       labels:
         caddy: status.${DOMAIN}
         caddy.reverse_proxy: "{{upstreams 3001}}"

   networks:
     local:
       external: true
     edge:
       external: true

   volumes:
     uptime_kuma_data:
   ```

   Note `local` has to be declared `external: true` here since `compose.prod.yml` owns/creates it — Compose will error if two files both try to create the same network non-externally.

2. Update `.github/workflows/cd.yml` to add `-f compose.observability.yml` to whatever `docker compose` invocation deploys the stack.
3. First-run: set a Kuma username/password (required, can't be skipped).
4. Add monitors:
   - `db`: TCP check on `db:5432`.
   - `api`: HTTP check. Confirm `Aerie.Api` has a health endpoint (e.g. ASP.NET Core health checks at `/health`) before wiring this up — add one if it doesn't exist yet.
   - `caddy`: HTTP check directly on `caddy:80` (container-to-container on `edge`), not through a public hostname — keeps this monitor independent of DNS/cert state, which is arguably a separate concern worth its own monitor later.
   - Home Assistant: HTTP check against `${ha_host}:${ha_port}` (same values already in `src/Aerie.Api/.env.json`).
5. Configure the Home Assistant notification provider in Kuma's UI (needs an HA long-lived access token — can reuse the existing one or mint a dedicated `uptime-kuma` token), attach it to all monitors, hit **Test**.
6. Verify end-to-end by stopping one container (`docker compose stop db`) and confirming a push arrives.

**Deliverable:** public status page at `status.${DOMAIN}` and a real "it's down" push to your phone, before any log infrastructure exists.

## Phase 2 — Log pipeline (OpenSearch + Fluent Bit)

No UI yet — just get data flowing and durable.

1. Add to `compose.observability.yml`:

   ```yaml
   services:
     opensearch:
       image: opensearchproject/opensearch:3.7.0
       restart: always
       environment:
         - discovery.type=single-node
         - DISABLE_SECURITY_PLUGIN=true
         - OPENSEARCH_JAVA_OPTS=-Xms512m -Xmx512m
       ulimits:
         memlock: { soft: -1, hard: -1 }
         nofile: { soft: 65536, hard: 65536 }
       volumes:
         - opensearch_data:/usr/share/opensearch/data
       ports:
         - "127.0.0.1:9200:9200"   # loopback-only, for setup/debug curl access
       networks:
         - observability

     fluent-bit:
       image: fluent/fluent-bit:4.2.7
       restart: always
       volumes:
         - /var/lib/docker/containers:/var/lib/docker/containers:ro
         - ./containers/fluent-bit/fluent-bit.conf:/fluent-bit/etc/fluent-bit.conf:ro
         - ./containers/fluent-bit/parsers.conf:/fluent-bit/etc/parsers.conf:ro
       networks:
         - observability
       depends_on:
         - opensearch

   networks:
     observability:
       driver: bridge

   volumes:
     opensearch_data:
   ```

   Deliberately no `/var/run/docker.sock` mount for Fluent Bit — plain log-file tailing doesn't need Docker API access. Label/container-metadata enrichment (which does need the socket) can be added later if plain container-ID-in-path isn't enough.

2. `containers/fluent-bit/parsers.conf` — the image ships a default one with a `docker` parser already defined for the exact JSON shape Docker's `json-file` driver writes (`log`, `stream`, `time` fields); reference it rather than duplicating it, unless it's missing then add:

   ```ini
   [PARSER]
       Name        docker
       Format      json
       Time_Key    time
       Time_Format %Y-%m-%dT%H:%M:%S.%L
       Time_Keep   On
   ```

3. `containers/fluent-bit/fluent-bit.conf`:

   ```ini
   [SERVICE]
       Flush         5
       Daemon        off
       Log_Level     info
       Parsers_File  parsers.conf

   [INPUT]
       Name              tail
       Path              /var/lib/docker/containers/*/*.log
       Parser            docker
       Tag               docker.*
       Refresh_Interval  5
       Mem_Buf_Limit     10MB
       Skip_Long_Lines   On

   [OUTPUT]
       Name            opensearch
       Match           docker.*
       Host            opensearch
       Port            9200
       Index           aerie-logs
       Logstash_Format On
       Logstash_Prefix aerie-logs
       tls             Off
       Suppress_Type_Name On
   ```

   `Logstash_Format On` gives daily-rotated indices (`aerie-logs-%Y.%m.%d`) automatically, which is what the retention policy below targets.

4. Set up retention immediately, before volume grows unbounded — this is a home server, not a cluster with headroom to spare. One-time setup via the loopback-bound API:

   ```bash
   curl -s -X PUT 'http://127.0.0.1:9200/_plugins/_ism/policies/aerie-log-retention' \
     -H 'Content-Type: application/json' -d '{
       "policy": {
         "description": "Delete Aerie log indices after 30 days",
         "default_state": "hot",
         "states": [
           { "name": "hot", "transitions": [{ "state_name": "delete", "conditions": { "min_index_age": "30d" } }] },
           { "name": "delete", "actions": [{ "delete": {} }] }
         ],
         "ism_template": { "index_patterns": ["aerie-logs-*"], "priority": 100 }
       }
     }'
   ```

5. Verify: `curl http://127.0.0.1:9200/_cat/indices?v` shows `aerie-logs-*` growing, and a sample query returns real log lines from `api`/`db`/`caddy`.

**Deliverable:** logs durably indexed and queryable via raw API, retention bounded, before any UI work.

## Phase 3 — Log inspection UI (OpenSearch Dashboards)

1. Add to `compose.observability.yml`:

   ```yaml
   services:
     opensearch-dashboards:
       image: opensearchproject/opensearch-dashboards:3.7.0
       restart: always
       environment:
         - OPENSEARCH_HOSTS=http://opensearch:9200
         - DISABLE_SECURITY_DASHBOARDS_PLUGIN=true
       networks:
         - observability
         - edge
       depends_on:
         - opensearch
       labels:
         caddy: logs.${DOMAIN}
         caddy.reverse_proxy: "{{upstreams 5601}}"
   ```

2. In Dashboards, create an index pattern for `aerie-logs-*` (time field: `time`).
3. Confirm filtering/search works (by container, by log level if the app emits structured fields, by free text).

**Deliverable:** browsable log search at `logs.${DOMAIN}`, no login prompt (per the auth decision above).

## Phase 4 — Log-based alerting

1. In Dashboards → Alerting, create a webhook **notification channel** pointed directly at Home Assistant:
   - URL: `http://${ha_host}:${ha_port}/api/services/notify/<your_notify_service>`
   - Header: `Authorization: Bearer <ha_token>`
   - Body: `{"message": "{{ctx.monitor.name}} triggered: {{ctx.trigger.name}}", "title": "Aerie Alert"}`
2. Create a monitor (e.g. count of log lines matching an error signature over a trailing 5-minute window, threshold-based trigger) and attach the channel above.
3. Test by forcing an error in `api` and confirming the push arrives.

**Deliverable:** alerts fire on "up but broken," closing the gap Uptime Kuma's uptime-only checks leave open.

## Deferred / not in this round

- **Auth on Dashboards/OpenSearch/Kuma's Caddy front door.** When it's time: re-enable `DISABLE_SECURITY_DASHBOARDS_PLUGIN`/`DISABLE_SECURITY_PLUGIN` (OpenSearch's built-in security plugin, real accounts) *or* add a Caddy `basic_auth` label (bcrypt hash via `caddy hash-password`) in front of each `edge`-exposed service — either is a small, additive change against what's built here, not a rearchitecture.
- **Metrics/dashboards** (Prometheus + Grafana + node-exporter/cAdvisor, or host CPU/memory/disk trends generally) — not part of the original ask (logging + status + alerting), would be a legitimate Phase 5 later if wanted.
- **SMS/text alerting** — no viable free/self-hosted path exists (would require a paid gateway like Twilio); HA notify (push) covers the "alerting channel" requirement instead.
- **Broader homelab log ingestion** (pfSense, HA itself, NAS) — Fluent Bit's `tail` input generalizes to this later; not blocked by anything above.

## Prerequisites before starting Phase 1

- An HA long-lived access token (reuse the existing one from `src/Aerie.Api/.env.json`'s `ha_token`, or mint a dedicated one for observability tooling).
- A rough check of free RAM on the home server — OpenSearch alone wants ~1GB+ (512MB heap plus JVM/off-heap overhead), Dashboards another few hundred MB, Kuma and Fluent Bit are lightweight by comparison.
- Confirm whether `Aerie.Api` already exposes a health-check endpoint for Kuma's `api` monitor; add one if not.
