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

## Implementation Progress

This doc is being implemented one checklist item at a time, each in its own fresh chat session, to keep token spend low and let every step be verified/committed independently before moving on.

**Resuming prompt (paste this in a new chat to continue):**

> Implement the next step in the monitoring/alerting architecture doc.

**Process for each session:**

1. Read this section, find the first unchecked `- [ ]` item below (prerequisites first, then phases in order).
2. Implement only that one item — don't get ahead of it, even if the next step looks trivial.
3. Item tags mean:
   - `[code]` — Claude does this directly (files, config, workflow edits).
   - `[manual]` — requires the live home server or clicking through a web UI (first-run setup, adding monitors, hitting Test buttons). Claude cannot do these — instead, give the user precise instructions for what to click/run, then wait for them to confirm it's done before checking the box.
   - `[verify]` — a checkpoint to confirm the previous item(s) actually work (per that phase's "Deliverable"). Usually needs the user to confirm observed behavior (a push notification arrived, a page loads, a query returns rows), since Claude doesn't have browser/notification access.
4. Check the box, and if anything deviated from the plan as written, or you learned something the next session should know, add a dated bullet under **Step log** below.
5. If you spot follow-up work that isn't part of the current step but shouldn't be forgotten (a hardening item, a thing that felt hacky, something deferred), add it to **Cleanup backlog** below rather than doing it now or letting it evaporate.
6. Commit the change (code/config + this doc's checklist update together) with a message noting the phase/step.
7. Report back briefly what was done and what the next unchecked item is, so the user knows what to expect before they clear context.

Do not skip ahead to implement multiple items in one session, even if it seems efficient — the whole point is small, independently-verifiable, committed increments.

### Checklist

#### Prerequisites

- [x] `[manual]` Confirm an HA long-lived access token is available (reuse existing `ha_token` from `src/Aerie.Api/.env.json`, or mint a dedicated one).
- [x] `[manual]` Check free RAM on the home server (OpenSearch wants ~1GB+, Dashboards a few hundred MB more).
- [x] `[code]` Confirm whether `Aerie.Api` exposes a health-check endpoint; add one (e.g. ASP.NET Core health checks at `/health`) if not.
- [x] `[manual]` Add GitHub Actions repo config for the `kuma-provision` step: vars `HA_HOST`, `HA_PORT` and secret `HA_TOKEN` (same HA instance as `src/Aerie.Api/.env.json`'s `ha_host`/`ha_port`/`ha_token`, just re-homed as CD inputs since `cd.yml` only sparse-checks out compose files, not the API's `.env.json`).

#### Phase 1 checklist — Status page + alerting (Uptime Kuma)

- [x] `[code]` 1. Create `compose.observability.yml` with the `uptime-kuma` service.
- [x] `[code]` 2. Update `.github/workflows/cd.yml` to add `-f compose.observability.yml` to the deploy invocation.
- [x] `[code]` 3. First-run account setup, automated: `containers/kuma-provision/provision.py` calls Kuma's `setup` Socket.IO event on first boot (no-op afterward), then logs in.
- [x] `[code]` 4. Monitors, automated: `db` (TCP 5432), `api` (HTTP `/health`), `caddy` (HTTP on `caddy:80`), Home Assistant (HTTP on `homeassistant.local:8123`) are declared as static-monitor files under `containers/autokuma/static-monitors/` and synced continuously by the `autokuma` sidecar service.
- [x] `[code]` 5. HA notification provider, automated: `provision.py` creates/updates it from `HA_HOST`/`HA_PORT`/`HA_TOKEN` and attaches it to every existing monitor on each run (self-healing regardless of `autokuma` sync timing).
- [x] `[verify]` 6. Stop `db` (`docker compose stop db`) and confirm a push arrives; restart it after.

#### Phase 2 checklist — Log pipeline (OpenSearch + Fluent Bit)

- [x] `[code]` 1. Add `opensearch` and `fluent-bit` services (+ `observability` network, `opensearch_data` volume) to `compose.observability.yml`.
- [x] `[code]` 2. Add/confirm `containers/fluent-bit/parsers.conf`.
- [x] `[code]` 3. Add `containers/fluent-bit/fluent-bit.conf`.
- [x] `[code]` 4. ISM retention policy, automated: `containers/opensearch-provision/apply-ism-policy.sh` runs as a one-shot `opensearch-provision` service, checking for the policy before creating it (idempotent, safe on every `up -d`).
- [x] `[verify]` 5. `curl http://127.0.0.1:9200/_cat/indices?v` shows `aerie-logs-*` growing; sample query returns real `api`/`db`/`caddy` log lines.

#### Phase 3 checklist — Log inspection UI (OpenSearch Dashboards)

- [x] `[code]` 1. Add `opensearch-dashboards` service to `compose.observability.yml`.
- [x] `[code]` 2. Index pattern, automated: `containers/opensearch-provision/create-index-pattern.sh` creates the `aerie-logs-*` index pattern (time field: `time`) via Dashboards' saved objects API, idempotent via `overwrite=true`.
- [ ] `[verify]` 3. Confirm filtering/search works (by container, level, free text) at `logs.${DOMAIN}`.
- [ ] `[dns]` 4. Create Caddy subdomains `logs.landis.family` for opensearch and `status.landis.family` for kuma (make a suggestion if there would be more appropriate names)

#### Phase 4 checklist — Log-based alerting

- [ ] `[manual]` 1. In Dashboards → Alerting, create a webhook notification channel pointed at HA.
- [ ] `[manual]` 2. Create a monitor (error-signature count over a trailing window) and attach the channel.
- [ ] `[verify]` 3. Force an error in `api` and confirm the push arrives.

- [ ] USER REQUEST - add logging to the UI apps (Console/admin). Especaily make sure that errors get back, but I'd like to be able to pull metrics on dashboard views. break this down into granular steps if needed

#### Phase 5 — Cleanup backlog (work through after Phase 4, before calling this done)

- [ ] *(items get appended here during implementation — see Cleanup backlog below; promote them into checkboxes here as they're identified, so nothing from the Deferred section or ad-hoc discoveries gets lost)*

### Cleanup backlog

*(running list of follow-up items discovered mid-implementation; pull the relevant ones into Phase 5's checklist above, don't just leave them prose-only)*

- Auth on Dashboards/OpenSearch/Kuma's Caddy front door — deferred by design this round, see [Deferred](#deferred--not-in-this-round). Revisit if the server's exposure model changes.
- Kuma's admin password (`aerie-kuma-admin!23` in `compose.observability.yml`) is a hardcoded dummy value by design (per the "no real secrets behind this login" reasoning), but it's plaintext in git. Harmless today since Kuma is LAN-only with nothing sensitive gated behind that login — revisit alongside the broader "Auth on ..." item above if that ever changes.
- AutoKuma's notification-provider support (as opposed to monitors) is marked experimental upstream, which is why `provision.py` owns the HA notification directly via the Kuma API instead of a static-monitors-style declarative file. If AutoKuma's notification support matures, revisit whether it's worth folding in.

### Step log

*(dated notes per session — what happened, what deviated from the plan, anything the next session needs to know)*

- 2026-07-26: Implementation Progress tracking added to this doc; no phases started yet.
- 2026-07-26: `Aerie.Api` had no health-check endpoint. Added a plain liveness check — `builder.Services.AddHealthChecks()` + `app.MapHealthChecks("/health")` in `Program.cs`, no new NuGet packages needed (the middleware ships in the shared framework). Deliberately did *not* wire in an EF Core/Npgsql DB check here: Kuma's `db` monitor (Phase 1) already covers DB liveness via a separate TCP check on `5432`, so `/health` only needs to answer "is the API process up and serving requests," keeping this step to exactly what the checklist item asked for.
- 2026-07-26: Created `compose.observability.yml` with the `uptime-kuma` service exactly as specced in Phase 1 step 1 — `local` declared `external: true` (owned/created by `compose.prod.yml`), `edge` also `external: true` (matches `compose.prod.yml`'s existing declaration). No deviations from the plan.
- 2026-07-26: Updated `.github/workflows/cd.yml` for Phase 1 step 2 — added `compose.observability.yml` to the sparse-checkout list and `-f compose.observability.yml` to both the `pull` and `up -d` invocations. No other changes; the `containers/fluent-bit/` config files needed for Phase 2 aren't checked out yet and will need to be added to sparse-checkout in that phase.
- 2026-07-26: Automated Phase 1 steps 3-5 (previously `[manual]`) at the user's request, so environments are reproducible without clicking through Kuma's UI. Researched Kuma's Socket.IO protocol (no REST API for setup/monitors/notifications) and confirmed via the `uptime-kuma-api` and `AutoKuma` source that: (a) the first-run admin account is created via an undocumented `setup` Socket.IO event, reachable directly since it doesn't require prior auth; (b) `isDefault`/`applyExisting` on a Kuma notification are WebUI-only conventions that AutoKuma's API client doesn't respect, so notification-to-monitor attachment can't be "set and forget" across both provisioning paths — `provision.py` instead re-attaches on every run, which is idempotent and ordering-independent. Added `containers/kuma-provision/` (one-shot `provision.py` + `requirements.txt`, run via a `python:3.12-slim` service with no persistent image build) and `containers/autokuma/static-monitors/` (`db.toml`, `api.toml`, `caddy.toml`, `ha.toml`). Added `autokuma` and `kuma-provision` services to `compose.observability.yml`, both `containers/kuma-provision` and `containers/autokuma` to `cd.yml`'s sparse-checkout, and `HA_HOST`/`HA_PORT`/`HA_TOKEN` to `cd.yml`'s env (from GitHub Actions vars/secrets — **these must be added to the repo's Actions config before the next deploy touches this compose file**, tracked as a new Prerequisites item above). Did not touch Phase 4's HA webhook (OpenSearch Alerting), which is unrelated and still fully manual/pending.
- 2026-07-26: Phase 2 step 1 — added `opensearch` and `fluent-bit` services, the `observability` network, and the `opensearch_data` volume to `compose.observability.yml`, exactly as specced. `fluent-bit`'s config-file volume mounts (`containers/fluent-bit/{fluent-bit.conf,parsers.conf}`) point at files that don't exist yet by design — those are Phase 2 steps 2 and 3, left for the next session(s). Note for that next session: `cd.yml`'s sparse-checkout list will also need `containers/fluent-bit` added (same pattern as `containers/kuma-provision`/`containers/autokuma` from Phase 1), since it isn't there yet.
- 2026-07-26: Phase 2 step 2 — pulled `fluent/fluent-bit:4.2.7` and extracted its built-in `/fluent-bit/etc/parsers.conf` (via `docker create` + `docker cp`, since the image has no shell/`cat` to `docker run` against) to confirm the `docker` parser it ships matches the doc's spec exactly. Result: no local `containers/fluent-bit/parsers.conf` was added — would've been a pure duplicate of the image default. Instead removed the now-unneeded `./containers/fluent-bit/parsers.conf:/fluent-bit/etc/parsers.conf:ro` bind mount from `compose.observability.yml`'s `fluent-bit` service (it referenced a file that was never created). `containers/fluent-bit/` still has no files on disk after this step — step 3 (`fluent-bit.conf`) is the first real file there, so `cd.yml`'s sparse-checkout still needs `containers/fluent-bit` added once that lands, not before.
- 2026-07-26: Phase 2 step 3 — added `containers/fluent-bit/fluent-bit.conf` verbatim per the doc's spec (tail input over the host's `json-file` logs, `opensearch` output with `Logstash_Format On`/`Logstash_Prefix aerie-logs`). `compose.observability.yml` already bind-mounted this path from step 1, so no compose changes needed. Also added `containers/fluent-bit` to `cd.yml`'s sparse-checkout list, per the note left in the previous entry — this is now the first file that directory has, so the deploy would otherwise silently mount an empty path.
- 2026-07-26: Phase 2 step 4 — automated at the user's request instead of leaving it as a manual host `curl`, for reproducible environments (same reasoning as Phase 1 steps 3-5). Added `containers/opensearch-provision/apply-ism-policy.sh` (retries until OpenSearch responds, `GET`s the policy first and only `PUT`s it if missing — a plain `PUT` on an existing ISM policy 409s without a `seq_no`/`primary_term`, so check-then-create was needed rather than an unconditional `PUT`) and a matching one-shot `opensearch-provision` service (`curlimages/curl:8.11.0`, no build needed) in `compose.observability.yml`. Added `containers/opensearch-provision` to `cd.yml`'s sparse-checkout list.
- 2026-07-26: Phase 3 step 1 — added `opensearch-dashboards` service to `compose.observability.yml` exactly as specced (joins both `observability` and `edge`, `DISABLE_SECURITY_DASHBOARDS_PLUGIN=true` per this doc's no-auth-this-round decision, Caddy label for `logs.${DOMAIN}` on port 5601). No new files under `containers/`, so — unlike every prior code step — `cd.yml`'s sparse-checkout list needs no change here.
- 2026-07-26: Phase 3 step 2 — automated at the user's request instead of a manual Dashboards UI click-through, same reasoning as the Phase 1/2 provisioning steps. Added `containers/opensearch-provision/create-index-pattern.sh` and folded it into the existing `opensearch-provision` one-shot service (now runs `apply-ism-policy.sh && create-index-pattern.sh`), with `depends_on: opensearch-dashboards` added. Used Dashboards' saved-objects API (`POST /api/saved_objects/index-pattern/aerie-logs?overwrite=true`, header `osd-xsrf: true`) rather than the check-then-create pattern from the ISM script — `overwrite=true` makes a plain repeated `POST` idempotent on its own, so no existence check was needed. `cd.yml`'s sparse-checkout already covers `containers/opensearch-provision` as a whole directory (non-cone-mode gitignore-style match), so no change needed there.

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
| `autokuma` | `ghcr.io/bigboot/autokuma:latest` | Syncs `containers/autokuma/static-monitors/*.toml` into Kuma monitors | `local` |
| `kuma-provision` | `python:3.12-slim` | One-shot: Kuma first-run account setup + HA notification provider, then attaches it to every monitor | `local` |
| `opensearch` | `opensearchproject/opensearch:3.7.0` | Log store + search + alerting engine | `observability` |
| `opensearch-dashboards` | `opensearchproject/opensearch-dashboards:3.7.0` | Log inspection UI | `observability`, `edge` |
| `fluent-bit` | `fluent/fluent-bit:4.2.7` | Tails host container logs, ships to OpenSearch | `observability` |
| `opensearch-provision` | `curlimages/curl:8.11.0` | One-shot: applies the `aerie-log-retention` ISM policy | `observability` |

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
3. First-run account setup is automated, not manual: Kuma's web UI creates the initial admin account via a Socket.IO `setup` event under the hood, and that event is reachable directly — no HTTP/REST setup endpoint exists, but `containers/kuma-provision/provision.py` connects with `python-socketio` (via the `uptime-kuma-api` package) and calls it. It's a one-shot service in `compose.observability.yml`: it fails (harmlessly) if an account already exists, so it's safe to run on every `up -d`. Kuma's own login has no supported way to disable, so the account uses fixed dummy credentials committed directly in the compose file — there are no real secrets behind this login, matching the "LAN-only, trusted network" reasoning above.
4. Monitors are declared, not clicked through:
   - `db`: TCP check on `db:5432`.
   - `api`: HTTP check against `/health` (added to `Aerie.Api` for this purpose).
   - `caddy`: HTTP check directly on `caddy:80` (container-to-container on `edge`), not through a public hostname — keeps this monitor independent of DNS/cert state, which is arguably a separate concern worth its own monitor later.
   - Home Assistant: HTTP check against `homeassistant.local:8123` (same host/port as `src/Aerie.Api/.env.json`).

   Each is a `.toml` file under `containers/autokuma/static-monitors/`, synced into Kuma continuously by the `autokuma` sidecar (`ghcr.io/bigboot/autokuma`) — no docker-label wiring on `compose.prod.yml`'s services, keeping this decoupled per the Summary's design goal. Adding a fifth monitor later is a new file, not a UI click-through.
5. The Home Assistant notification provider is also handled by `provision.py`: it creates (or updates, if already present) a notification named "Home Assistant" from `HA_HOST`/`HA_PORT`/`HA_TOKEN` — populated in `cd.yml` from GitHub Actions vars/secrets, the only real credential in this phase — then walks every current monitor and attaches it if missing. This runs after `autokuma`'s sync on every deploy, so it self-heals regardless of which service created a given monitor or how the two containers race on startup.
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

2. `containers/fluent-bit/parsers.conf` — confirmed unnecessary: the `fluent/fluent-bit:4.2.7` image's built-in `/fluent-bit/etc/parsers.conf` already defines a `docker` parser matching this exactly (`Format json`, `Time_Key time`, `Time_Format %Y-%m-%dT%H:%M:%S.%L`, `Time_Keep On`). No local file is added, and the `compose.observability.yml` bind mount for it (from step 1) was removed rather than shipping a duplicate — `fluent-bit.conf`'s `Parsers_File parsers.conf` (step 3) resolves relative to `/fluent-bit/etc/`, so it finds the image's own default file there.

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

4. Retention is set up automatically, not via a manual one-time `curl`: `containers/opensearch-provision/apply-ism-policy.sh` runs as the one-shot `opensearch-provision` service in `compose.observability.yml` (`curlimages/curl` image, no build). It retries until OpenSearch answers, checks whether the `aerie-log-retention` policy already exists (`GET`), and only `PUT`s it if missing — so it's idempotent and safe to re-run on every `up -d`, matching the `kuma-provision` pattern from Phase 1. The policy itself is unchanged from the original plan:

   ```json
   {
     "policy": {
       "description": "Delete Aerie log indices after 30 days",
       "default_state": "hot",
       "states": [
         { "name": "hot", "transitions": [{ "state_name": "delete", "conditions": { "min_index_age": "30d" } }] },
         { "name": "delete", "actions": [{ "delete": {} }] }
       ],
       "ism_template": { "index_patterns": ["aerie-logs-*"], "priority": 100 }
     }
   }
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

2. Index pattern creation is automated, not clicked through: `containers/opensearch-provision/create-index-pattern.sh` runs as part of the `opensearch-provision` one-shot service (alongside the Phase 2 ISM policy script), `POST`ing to Dashboards' saved objects API (`/api/saved_objects/index-pattern/aerie-logs?overwrite=true`) to create/update an index pattern for `aerie-logs-*` with time field `time`. `overwrite=true` makes it idempotent outright — no need for the check-then-create pattern the ISM script uses, since re-POSTing the same definition is always safe.
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
