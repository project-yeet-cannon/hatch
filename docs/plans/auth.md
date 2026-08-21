# Auth — device grants, a QR to hand them out, one wall in front of everything

Aerie has no authentication. `app.UseAuthorization()` runs in
[`Program.cs`](../../src/Aerie.Api/Program.cs) with no scheme behind it, which
is a no-op, and every app and endpoint is open to anything that can reach the
host. That was a deliberate call — see the tripwire in
[`family-apps-architecture.md`](../family-apps-architecture.md#auth-none-now-and-the-tripwire)
— and this plan is that tripwire being tripped.

The design goal is **magic for the family, administrative overhead for the
operator**: a household member's device is enrolled once, by the operator, and
never asks again. Everything else — people, roles, scopes, passkeys — is
deliberately deferred behind a model that has room for it.

**How to use this file:** one phase per commit, each leaving the app working.
Phases 1–4 build the whole mechanism with the wall **switched off**, so nothing
can lock anyone out until Phase 5 flips one value. Every phase names its goal,
its files, its steps, and how to know it's done, so it can be picked up cold.

**Verification, everywhere:** `make build` and `make test-api` for backend work,
`make test-web` for frontend. Migrations via `make ef-migration
migration=Name`, applied locally with `make ef-database-update`. Per repo
convention, UI is not browser-tested here — build and lint it and leave the
clicking to a human.

## Status

- [x] **1** — Grant + invite schema, token primitives, the gate decision
- [x] **2** — Auth endpoints and in-process middleware (still off)
- [x] **3** — The sign-in shell (`apps/auth`)
- [ ] **4** — Admin Sessions page: view, delete, generate invite
- [ ] **5** — Turn the wall on: bootstrap grant, Traefik middleware, Ingress annotations
- [ ] **6** — *(optional)* Kiosk tablets scan the QR instead of typing the code
- [ ] **7** — Widen to `share.`, dissipate this plan into `docs/auth-architecture.md`

## The ask, restated

From the original brief, the four things that define "done" for v1:

1. Apps we have written sit behind an auth provider.
2. API calls are authentication-gated — a gate, not fine-grained permissions.
3. The admin app can create or approve new sessions.
4. The admin app can view and delete existing sessions.

Everything beyond those four is in [Deferred](#deferred-on-purpose), including
the parts of the brief that were explicitly "ultimately" — users, roles, scopes,
and the observability tier.

## Better ideas than the ones in the brief

The brief listed three schemes — wait-for-approval, QR code, "effectively ways
of distributing an API key." That last parenthetical is the actual insight, and
it reframes the whole problem:

> **All three are the same mechanism.** They differ only in the *ceremony* by
> which a device acquires a long-lived credential. There is one thing to build
> — an issued, revocable, listable **grant** — and several ways to hand one out.

So the plan builds the grant once and makes the ceremony pluggable. That is
cheaper than any single scheme, and it means adding "wait for approval" later is
a controller and a page, not a redesign.

Ranked, with the honest tradeoffs:

| Scheme | Verdict | Why |
|---|---|---|
| **Admin-generated invite code (QR + short code)** | **Ship first** | Least machinery of the three: no pending queue, no notification path, no polling screen. The admin app already carries `qrcode` and a working QR page to copy ([`ProvisioningPage.tsx`](../../src/Aerie.Web/apps/admin/src/pages/ProvisioningPage.tsx)). Covers the common household case — you are standing next to the person. |
| **Wait-for-approval / pending device** | Ship second, deferred | Genuinely better for the case the invite can't reach: they're not next to you. Costs a pending table, a polling screen, an admin queue, and some way you *notice*. Same grant underneath, so it's additive. |
| **Passkeys (WebAuthn)** | The right long-term answer, deferred | This is what "auth without a username and password" actually means as a standard: Face ID / fingerprint, phishing-proof, nothing shared to steal. It is the largest first slice (Fido2 library, attestation/assertion ceremonies, credential table) and it solves *re-authentication*, which a permanent grant means you rarely do. Right after grants exist, wrong as the thing that makes grants exist. |
| **mTLS client certificates** | Rejected | The purest "permanent grant" and the worst user experience on the platforms that matter: installing a client cert on iOS is a profile download plus three Settings screens, and revocation means a CRL nobody will operate. |
| **Tailscale identity headers** | Rejected as the primary gate, keep in mind | The tailnet already authenticates remote access and does real security work today. But devices on the house LAN are not on the tailnet, and the family devices are exactly those — so it gates the wrong population. Worth revisiting as an *auto-enrollment* signal later ("a tailnet-authenticated request may mint its own grant"). |
| **Shared house password** | Rejected | One secret, no revocation without rotating everyone, no per-device list. It is the thing the Sessions page exists to avoid. |

One more idea worth naming, because it is what makes the whole thing feel
magical rather than merely short: **the wall remembers where you were going.**
Every gated request carries its original URL through the sign-in shell and lands
back on it after redemption. That is what turns a printed storage-bin QR label,
scanned by a phone that has never authenticated, into "one extra tap" instead of
"a dead end."

## Decisions already made

| Decision | Choice | Why |
|---|---|---|
| Where the gate runs | **Traefik `forwardAuth` → an endpoint in Aerie.Api**, plus the same check in-process | One annotation puts any Ingress behind it, including services we didn't write (`share.`, and `logs.`/`status.` later). Sessions stay in our DB and the admin UI stays our admin app. No new container, no second identity system. |
| Why also in-process | Defense in depth, and local dev | A pod reached directly inside the cluster bypasses Traefik entirely. The in-process middleware is also the *only* gate under `make run`, where there is no proxy at all. Both call the same `AuthGate`, so there is one allow-list, not two. |
| Third-party IdP (Authelia, Pocket ID, Keycloak) | **Rejected** | Standards-compliant and self-hosted, but: a new HelmRelease and its secrets, its own admin UI instead of ours, and the invite/approval ceremonies the brief actually asked for don't exist there — they'd get bolted on anyway. Revisit if OIDC is ever needed *for* something rather than as an end. |
| Credential format | **Opaque 256-bit random token, SHA-256 hashed at rest** | Not a JWT and not an ASP.NET auth cookie. [`Program.cs`](../../src/Aerie.Api/Program.cs) documents that there is no persisted DataProtection key ring across the three replicas — cookie auth would decrypt on one replica and fail on another. A hashed opaque token has no key ring, no clock skew, and is revoked by deleting a row. Follows [`VmConsoleLogsController`](../../src/Aerie.Api/Controllers/VmConsoleLogsController.cs), which already does fixed-time shared-secret comparison. |
| Where the token rides | Cookie `__Secure-aerie_grant`, `Domain=.${DOMAIN}`, `HttpOnly`, `Secure`, `SameSite=Lax` | The `Domain` attribute is what makes one enrollment cover `home.`, `kiosk.`, `share.` and later `logs.`/`status.` — this is the entire SSO story and it costs one attribute. Note this rules out the `__Host-` prefix, which forbids `Domain`; `__Secure-` is the correct prefix here. |
| Grant lifetime | **No server-side expiry by default**; cookie re-issued on a sliding window | "Permanent until revoked" is the request. Note the browser-side catch: Chrome caps cookie `Max-Age` at **400 days** regardless of what we send, so a genuinely permanent cookie does not exist — the gate re-issues the cookie whenever it is older than `Auth:GrantRenewAfterDays`, which means an in-use device never lapses and a device untouched for a year re-enrolls. |
| Invite code shape | 8 chars of Crockford base32 (no I/L/O/U), displayed `AERIE-XXXX-XXXX`, 15-minute TTL, single use | ~40 bits behind a 15-minute window and a rate limiter. Short enough to read aloud across a room or type on a tablet soft keyboard, which is the fallback that makes Phase 6 optional. |
| Where the data lives | Core `AerieContext`, `public` schema | Auth is infrastructure for every module, not a family app. A `Modules/` schema would make every module depend on one module — exactly what [`Modules/README.md`](../../src/Aerie.Api/Modules/README.md) forbids. |
| Bootstrap | The **migrate Job** mints a first invite when the grant table is empty, and logs it | Solves the chicken-and-egg (the invite generator lives behind the wall) without an env-var admin token or a committed credential. The migrate hook runs exactly once per deploy, ahead of any replica — doing this at replica startup would mint three invites and race. Recoverable after a DR restore-to-empty for free. |
| Legacy Windows/Caddy host | **Not targeted** | Phase 7b of the [cluster cutover](swarm/phase-7-cutover.md) is next and 7c retires the host. Caddy `forward_auth` labels would be written to be deleted. The in-process gate still covers that host if the soak runs long. |
| Deployment surface | k3s only, behind `auth.enabled` in [`values.yaml`](../../charts/aerie/values.yaml) | Off is the default until Phase 5. One value is the whole rollback. |

## The allow-list is load-bearing

The gate's exempt paths are not an afterthought — two of them break things
silently and confusingly if they're missed, so they are written down here first
and implemented once, in `AuthGate`, shared by the middleware and the
`forwardAuth` endpoint.

| Path | Why it must stay open |
|---|---|
| `/health/live`, `/health/ready` | Kubernetes probes present no cookie. Gating these fails readiness on every pod and the Deployment never becomes available — a total outage whose cause looks nothing like auth. |
| **`/media/*`** (`MediaLibrary:RequestPath`) | **The one that will bite.** Sonos speakers fetch the stream themselves; they cannot hold a cookie. [`MediaLibraryOptions`](../../src/Aerie.Api/Services/Media/MediaLibraryOptions.cs) already says this in as many words — "which directory the API exposes unauthenticated on the LAN." Gating it stops all music with no error message that mentions authentication. Read the prefix from the same options object rather than hardcoding `/media`. |
| `/api/ui-logs` | Browser log shipping, including from the sign-in shell itself. A gated log endpoint means the failures you most want to see are the ones that can't report. |
| `/api/vm-console-logs` | Server-to-server from Hyper-V scheduled tasks, which hold no cookie — and it already carries its own `X-Vm-Log-Token` gate, which is strictly stronger than the cookie would be. |
| `/api/kiosk/provisioning-info` | Operator's explicit call: tablet provisioning stays friction-free. Nothing behind it is more sensitive than an APK URL and the Wi-Fi credentials the tablet is about to join with anyway. |
| `/apps/auth/*`, `/auth` (exactly), `/api/auth/verify`, `/api/auth/redeem` | The sign-in shell and its two endpoints. Gating these is an infinite redirect loop. `/auth` is the shell's short alias — a redirect into `/apps/auth/`, exempt *exactly* rather than by prefix so nothing later mounted underneath it inherits the exemption. |
| `files.${DOMAIN}` (whole host, no middleware) | Operator's explicit call: the APK and its signature checksum are public by design. This host simply never gets the annotation. |
| `logs.${DOMAIN}`, `status.${DOMAIN}` (whole host, for now) | OpenSearch Dashboards and Uptime Kuma keep their own logins. Revisit in [Deferred](#deferred-on-purpose). |

Two consequences worth stating plainly, because they are behavior changes the
operator chose rather than bugs to discover:

- **Printed storage-bin QR labels now hit the wall.** `/apps/family/storage/c/{code}`
  is gated, so a cold scan from a phone that isn't enrolled lands on sign-in.
  The return-to redirect (Phase 3) makes that one extra step rather than a dead
  end, but a guest holding a labeled bin can no longer scan it. That is the
  intended trade.
- **`share.${DOMAIN}` gains a second gate** in Phase 7, in front of the dummy
  dufs credential that [`values.yaml`](../../charts/aerie/values.yaml) documents
  as a placeholder. The dufs credential does not go away; it just stops being
  the only thing there.

## Conventions that apply to every phase

- **Multi-replica safe.** Three replicas. No grant state in process memory —
  invites and grants are rows, and the only cache permitted is a short one that
  is correct when cold.
- **Fixed-time comparison, hashes at rest.** Never store or log a token or an
  invite code. `CryptographicOperations.FixedTimeEquals` on the hash, as
  `VmConsoleLogsController` already does.
- **Log every refusal at Warning, with the reason and the client IP.** These
  flow to `logs.${DOMAIN}` through the existing fluent-bit pipeline, which is
  how a brute-force attempt becomes visible without building alerting for it.
- **Nothing operator-specific.** The cookie domain, the base URL, and the
  branding in the sign-in shell come from config — `Apps:PublicBaseUrl` and
  `DOMAIN` already exist and are already wired in
  [`api-deployment.yaml`](../../charts/aerie/templates/api-deployment.yaml). See
  [`ethos.md`](../ethos.md).
- **The gate defaults off through Phase 4.** `Auth:Enabled=false` everywhere
  until Phase 5. Every phase before that is provable with a local override and
  a `curl`.

---

## Phase 1 — Grant + invite schema, token primitives, the gate decision

**Goal:** everything the gate needs to decide, with nothing calling it yet.

**Files**

- `src/Aerie.Api/Ef/Auth.cs` *(new)* — `EfAuthGrant`, `EfAuthInvite`
- `src/Aerie.Api/Ef/AerieContext.cs` — two `DbSet`s and their `OnModelCreating` entries
- `src/Aerie.Api/Services/Auth/AuthOptions.cs` *(new)*
- `src/Aerie.Api/Services/Auth/AuthTokens.cs` *(new)* — generate, hash, format, parse
- `src/Aerie.Api/Services/Auth/AuthGate.cs` *(new)* — the allow-list and the decision
- `src/Aerie.Api/Services/Auth/IAuthService.cs` / `AuthService.cs` *(new)*
- `src/Aerie.Api.Tests/Auth/*` *(new)*

**Steps**

1. `EfAuthGrant`: `Id`, `TokenHash` (`byte[32]`, unique index), `Label`, `Kind`
   (`Interactive` | `Device`), `CreatedAt`, `LastSeenAt`, `LastSeenIp`,
   `UserAgent`, `ExpiresAt` (nullable — null means until revoked),
   `CookieIssuedAt`. Leave a nullable `PersonId` **out** for now; adding a
   column later is cheaper than a nullable FK to a table that doesn't exist.
2. `EfAuthInvite`: `Id`, `CodeHash` (`byte[32]`, unique index), `Label`
   (optional pre-fill for the grant it becomes), `CreatedAt`, `ExpiresAt`,
   `RedeemedAt`, `RedeemedGrantId`, `IsBootstrap`. Model it on
   [`EfOAuthState`](../../src/Aerie.Api/Ef/Calendar.cs) — same shape, same
   opportunistic sweep of expired rows when a new one is created, no job.
3. `AuthTokens`: 32 bytes from `RandomNumberGenerator`, base64url for the grant
   token; Crockford base32 for the invite code with a normalizing parse that
   folds `I`/`L` → `1`, `O` → `0`, strips dashes, and uppercases — so a code
   read aloud and typed back survives.
4. `AuthGate.Evaluate(path, host, cookie)` returns `Allow` (exempt),
   `Authenticated(grant)`, or `Challenge`. The allow-list table above lives here
   and nowhere else. `MediaLibrary:RequestPath` is read from `MediaLibraryOptions`,
   not hardcoded.
5. `AuthService`: `CreateInviteAsync`, `RedeemAsync`, `VerifyAsync`,
   `ListGrantsAsync`, `RevokeGrantAsync`. `VerifyAsync` updates `LastSeenAt` /
   `LastSeenIp` — throttled to at most once a minute per grant, since this runs
   on *every* request through the wall and an unthrottled write is a write
   amplifier on the hottest path in the app.
6. `make ef-migration migration=AddAuthGrants`, then `make ef-database-update`.
7. Tests: token round-trip, Crockford normalization (`AERIE-l0O1-...` parses),
   invite single-use, invite expiry, revoked grant fails verify, allow-list
   matches and near-misses (`/media` vs `/mediafoo`, `/health/live` vs
   `/healthz`), fixed-time comparison is actually reached.

**Done when:** `make build` and `make test-api` are green, the migration applies
to the local `db` container, and no request path in the app behaves differently.

---

## Phase 2 — Auth endpoints and in-process middleware (still off)

**Goal:** the gate can refuse, and refuses correctly, but is switched off.

**Files**

- `src/Aerie.Api/Controllers/AuthController.cs` *(new)*
- `src/Aerie.Api/Common/AuthMiddleware.cs` *(new)*
- `src/Aerie.Api/Program.cs` — register options and services, insert middleware
- `src/Aerie.Api/appsettings.json` / `appsettings.Development.json`

**Steps**

1. `GET /api/auth/verify` — the `forwardAuth` target. Reads Traefik's
   `X-Forwarded-Method` / `-Proto` / `-Host` / `-Uri` to reconstruct the original
   request, since the proxied request's own path is always `/api/auth/verify`.
   On success: `204` plus `X-Aerie-Grant` and `X-Aerie-Label` response headers
   (which the Middleware will copy through). On failure, **content-negotiate the
   refusal** — this matters more than it looks:
   - a document request (`Sec-Fetch-Mode: navigate`, or `Accept:` containing
     `text/html`) gets `302` to `/apps/auth/?r=<original url, encoded>`;
   - anything else — XHR, `fetch`, a Sonos GET, a probe — gets a bare `401`.

   A `302` returned to an XHR is invisible to the caller and shows up as a CORS
   or JSON-parse error somewhere unrelated, which is the single most common way
   this kind of gate wastes an afternoon.
2. `POST /api/auth/redeem` `{ code, label? }` — validates, mints the grant,
   sets the cookie, returns the grant summary. Rate-limited with the built-in
   `AddRateLimiter`, fixed window partitioned by client IP. Note the honest
   caveat in a comment: the limiter is per-process, so three replicas means three
   times the configured budget — acceptable against a 40-bit code with a
   15-minute TTL, and the Warning logs are the real detector.
3. `GET /api/auth/me` and `POST /api/auth/sign-out` (deletes the grant and
   expires the cookie).
4. `AuthMiddleware`: calls the same `AuthGate`, applies the same
   content-negotiated refusal, and re-issues the cookie when
   `CookieIssuedAt` is older than `Auth:GrantRenewAfterDays`. Register it in
   `Program.cs` **after** `UseForwardedHeaders` (it needs the client IP) and
   **before** the `UseStaticFiles` block for `/apps` (otherwise SPA bundles
   serve to anyone). No-ops entirely when `Auth:Enabled` is false.
5. `Auth:Enabled` — **`false` in `appsettings.json`**, and explicitly `false`
   in `appsettings.Development.json` so `make run` stays frictionless. The
   local flip is `Auth__Enabled=true dotnet run …`.

   *This step originally said `true` in `appsettings.json`, which contradicts
   the invariant stated twice above — "nothing can lock anyone out until Phase
   5" and "`Auth:Enabled=false` everywhere until Phase 5" — and would have
   been a live lockout rather than a documentation mismatch. The chart doesn't
   pass `Auth__Enabled` at all until Phase 5, so a Phase 2–4 deploy would take
   its value from this file; so would the legacy Windows/Caddy host, which
   runs `compose.prod.yml` with no override and has no bootstrap invite to
   recover through. The wall now turns on in exactly one place, which is what
   Phase 5 step 5 already describes.*

**Done when:** with the local override on, `curl -i localhost:5197/apps/admin/`
returns `302` to the sign-in shell, `curl -i -H 'Accept: application/json'
localhost:5197/api/zones` returns `401`, and `/health/ready`, `/media/...`,
`/api/ui-logs` all return their normal responses. With the override off,
everything behaves exactly as it does today. `make test-api` green.

---

## Phase 3 — The sign-in shell

**Goal:** a human can turn a code into a session and land where they were going.

**Files**

- `src/Aerie.Web/apps/auth/*` *(new Vite app)* — mirror
  [`apps/admin`](../../src/Aerie.Web/apps/admin/vite.config.ts): `base:
  '/apps/auth/'`, `outDir` into `src/Aerie.Api/wwwroot/apps/auth`
- `src/Aerie.Api/Program.cs` — `MapFallbackToFile` and a `^auth$` rewrite
- `Makefile` — add `auth` to the `test-web` app list

**Steps**

1. One screen. Big code input that auto-uppercases, auto-inserts the dashes, and
   accepts a paste of the full `AERIE-XXXX-XXXX`. A device-name field
   pre-filled from the user agent ("Ada's iPhone") so the Sessions list is
   legible instead of a wall of `Mozilla/5.0`. One button.
2. `/apps/auth/r/:code` — the deep link a scanned QR resolves to. Redeems
   immediately, no typing. This is the path that makes Phase 6 optional: the
   admin's QR can be scanned by *any* camera app on any phone, and only the
   kiosk tablets need in-page scanning.
3. Carry `?r=` through redemption and `location.replace` to it on success,
   defaulting to `/apps/`. **Validate it is a same-origin relative path before
   redirecting** — an open redirect on the one endpoint everybody is trained to
   trust is the classic own-goal here.
4. Failure states that say something: expired, already used, not a valid code,
   too many attempts. Ship the `clientLogger` pattern from
   [`apps/admin/src/lib/clientLogger.ts`](../../src/Aerie.Web/apps/admin/src/lib/clientLogger.ts)
   so shell failures reach `logs.${DOMAIN}` — the allow-list already keeps
   `/api/ui-logs` open for exactly this.
5. Add the `^auth/?$` → `apps/auth/` rewrite beside the existing ones in
   `Program.cs`, so `home.${DOMAIN}/auth` works when read aloud. The alias also
   has to join the allow-list: the rewrite is a redirect the gate sees *first*,
   so without an exemption the one URL meant to be read to someone who has no
   grant is the one URL that refuses them. It goes in `AuthGate` rather than by
   reordering the rewriter, because in production the decision is Traefik's,
   asking about the original URI, and never reaches this app's rewrite rules.

**Done when:** `make test-web` green, and with the gate on locally, hitting
`/apps/admin/devices` bounces to sign-in, redeeming a code minted with `psql`
lands back on `/apps/admin/devices`.

---

## Phase 4 — Admin Sessions page

**Goal:** brief goals 3 and 4 — create sessions, view and delete sessions.

**Files**

- `src/Aerie.Api/Controllers/AuthController.cs` — three admin endpoints
- `src/Aerie.Web/apps/admin/src/pages/SessionsPage.tsx` *(new)*
- `src/Aerie.Web/apps/admin/src/api/client.ts`, `src/types.ts`, `src/App.tsx`

**Steps**

1. `GET /api/auth/grants`, `DELETE /api/auth/grants/{id}`, `POST /api/auth/invites`.
2. Sessions page: a table of label, kind, created, last seen (relative — reuse
   [`lib/format.ts`](../../src/Aerie.Web/apps/admin/src/lib/format.ts)), last IP,
   and a delete button with a confirm. Mark the caller's own grant "this device"
   so nobody revokes their way out of the room.
3. **Generate invite** → a modal with the QR (the `qrcode` dependency is already
   in `package.json`; copy the render from `ProvisioningPage.tsx`), the short
   code in large type beneath it for reading aloud, and a live countdown to
   expiry. The QR encodes `${Apps:PublicBaseUrl}/apps/auth/r/{code}`.
4. Nav entry in `App.tsx`.

**Done when:** `make test-web` green, and all four goals from the brief are
demonstrable end to end against a local instance with `Auth__Enabled=true`.

---

## Phase 5 — Turn the wall on

**Goal:** the cluster actually refuses. One value is the rollback.

**Files**

- `src/Aerie.Api/Program.cs` — bootstrap invite in the `AERIE_MIGRATE` branch
- `charts/aerie/templates/middleware-auth.yaml` *(new)*
- `charts/aerie/templates/ingress.yaml` — annotations on `home` and `kiosk`
- `charts/aerie/values.yaml` + `deploy/cluster/apps/helmrelease.yaml`
- `scripts/k3s/Test-AppTier.ps1` — checks
- `README.md` — how to enroll a device

**Steps**

1. In the `AERIE_MIGRATE=1` branch, after the seeders: if `AuthGrants` is empty
   and no unredeemed bootstrap invite exists, mint one with a longer TTL (an
   hour) and log it at Warning with a banner. This is the only place a code is
   ever written to a log, and it is correct there — it is reachable only when
   the install has no way in at all.
2. `middleware-auth.yaml` — a Traefik `Middleware` with
   `forwardAuth.address: http://api.{{ .Release.Namespace }}.svc.cluster.local:8080/api/auth/verify`
   and `authResponseHeaders: [X-Aerie-Grant, X-Aerie-Label]`. Guard the whole
   file on `.Values.auth.enabled`.
3. Annotate the `home` and `kiosk` Ingresses. **The `kiosk` Ingress already
   carries a middleware annotation** — Traefik takes a comma-separated list and
   applies it in order, so it becomes
   `"{{ ns }}-aerie-auth@kubernetescrd,{{ ns }}-kiosk-root-rewrite@kubernetescrd"`,
   auth first. Same `<namespace>-<name>@kubernetescrd` rule the existing comment
   in that file warns about: a bare name is silently not found and the route
   serves *unauthenticated*, which is a failure that looks like success.
4. `auth.enabled: false` in `values.yaml`, `${AUTH_ENABLED}` in the HelmRelease,
   and the matching entry in `cluster-config.json`.
5. Deploy with it still false. Confirm nothing changed. Then flip it.
6. Add to `Test-AppTier.ps1`: unauthenticated `home.${DOMAIN}/` → 302 to
   `/apps/auth/`; `/health/ready` → 200; a `/media/...` HEAD → 200; `files.` and
   `status.` unchanged; with a test grant cookie, `home.${DOMAIN}/` → 200.

**Verify by hand, in this order, before walking away**

- Sonos plays a track from the media library. *(The `/media` exemption. If this
  fails, nothing else in this list matters.)*
- Every kiosk tablet: shows the wall, accepts a typed code, returns to the
  dashboard, and **survives a reboot still signed in.** Cookie persistence in
  GeckoView is the thing to actually confirm here rather than assume.
- The dashboard's self-update poll (`/api/app-version`) still works from an
  enrolled tablet.
- `kubectl -n aerie logs job/…-migrate` shows no bootstrap banner on an install
  that already has grants.

**Done when:** an un-enrolled device on the LAN cannot reach `home.${DOMAIN}`,
every enrolled device is unaware anything changed, and `auth.enabled: false`
plus a Flux reconcile is a complete, tested rollback.

---

## Phase 6 — *(optional)* Kiosk tablets scan instead of type

**Goal:** the tablet's own camera reads the admin's QR.

**This phase is optional and severable.** Phase 5 already leaves tablets working
via a typed code — an eight-character code on a soft keyboard, once, per tablet,
maybe once a year. Read the cost below before deciding it's worth it; the
cheaper alternative is to do nothing here at all.

**The cost, stated honestly.** The kiosk shell is **GeckoView**, not Android
WebView ([`MainActivity.kt`](../../apps/kiosk/app/src/main/java/family/landis/aeriekiosk/MainActivity.kt)).
There is no `CAMERA` permission in the manifest and no `PermissionDelegate` on
the session, so `getUserMedia` currently fails closed. Making it work is:

- `<uses-permission android:name="android.permission.CAMERA" />`
- a `GeckoSession.PermissionDelegate` implementing both
  `onAndroidPermissionsRequest` and `onMediaPermissionRequest`
- a runtime grant that does not prompt — the app is Device Owner, so
  `DevicePolicyManager.setPermissionGrantState(..., PERMISSION_GRANT_STATE_GRANTED)`
  can self-grant silently, which is the one piece of this that is genuinely
  elegant
- a QR decoder in the sign-in shell. Gecko does not ship `BarcodeDetector`, so
  this is a small JS library over a `<canvas>`, not a platform API
- a new APK build, signed, published to `files.`, and installed on every tablet
  — and the tablets need to be enrolled to load the dashboard that tells them to
  update, so **sequence this after Phase 5 has them enrolled**, not before

**Done when:** a tablet at the wall opens a scanner, reads a QR from the admin's
screen, and lands on the dashboard without a keyboard appearing.

---

## Phase 7 — Widen, then dissipate

**Goal:** finish the "all our apps" half of the brief and retire this file.

**Steps**

1. Annotate the `share` Ingress with the auth middleware. dufs needs no changes
   — Traefik refuses before it proxies. Its own dummy credential stays as the
   second gate.
2. Write `docs/auth-architecture.md`: the grant model, the cookie and its domain
   scope, the allow-list *with the `/media` and probe reasoning intact*, the
   ceremony seam, the bootstrap path, and a lockout-recovery runbook
   (`kubectl exec` into the CNPG primary, insert an invite by hash).
3. Update the two places that currently document auth's absence: the tripwire in
   [`family-apps-architecture.md`](../family-apps-architecture.md#auth-none-now-and-the-tripwire)
   and the "No module invents a user" rule in
   [`Modules/README.md`](../../src/Aerie.Api/Modules/README.md) — which stays a
   rule, but now points at `AuthService` instead of at a future.
4. Delete this file and its row in [`README.md`](README.md), per the plan
   lifecycle.

---

## Deferred on purpose

Each of these is additive against the grant model. None requires revisiting a
Phase 1–7 decision.

- **Wait-for-approval ceremony.** A `PendingRequest` row, a polling screen in
  the sign-in shell, a queue on the Sessions page. Wants a notification path so
  you notice — `POST /api/auth/pending` could push through the existing
  Uptime Kuma / Home Assistant plumbing rather than growing its own.
- **Passkeys.** A `Credential` table hanging off a grant, `Fido2NetLib`, and two
  ceremony endpoints. This becomes the *re-enrollment* path: a lapsed device
  proves itself with Face ID instead of finding the operator. It is also the
  point at which a grant stops being a device and starts being a person.
- **People, roles, scopes.** `Person`, `PersonId` on the grant, a `[RequireScope]`
  filter. The brief asks for this "ultimately" — the shape that keeps it cheap is
  already here: a grant is a row with room for an owner.
- **`logs.` and `status.` behind the same wall.** One annotation each, once
  OpenSearch Dashboards' and Uptime Kuma's own logins can be told to trust
  `X-Aerie-Label` as a proxy-authenticated user. This is the "weave in our
  observability tier" line in the brief, and it is one manifest edit plus each
  tool's proxy-auth config — not a new integration.
- **Tailnet auto-enrollment.** A request arriving over the tailnet is already
  authenticated by Tailscale; it could mint its own grant instead of asking for
  a code. Attractive, and worth doing only once the base flow is boring.
