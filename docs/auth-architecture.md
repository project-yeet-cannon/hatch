# Auth Architecture

## Summary

Every app Aerie serves sits behind **one wall**, and the thing that gets past it
is a **grant**: a long-lived, revocable credential belonging to a *device*
rather than to a person. There are no accounts, no passwords, and nothing for a
family member to remember. The operator enrolls a device once — a QR code or an
eight-character invite read across the room — and that device never asks again.

The design goal was *magic for the family, administrative overhead for the
operator*. Everything a conventional identity system carries — roles, scopes,
passkeys — is deferred behind a model with room for it, and the sections below
say exactly where each one would attach.

People are the first of those to have arrived, and arriving changed nothing
about the sentence above: a `Person` is a name you hang on a grant, not a thing
that signs in. See [Whose device is this](#whose-device-is-this).

What a person *is* to an authorization decision is a separate question, and the
answer is that they are an input to one. Quill's notes belong to a person, and
sharing one with somebody — read, or write — is the next step in that direction.
What does not exist yet is a permission model to express it in. See
[A person is an authorization input](#a-person-is-an-authorization-input).

One global role does exist: `Person.IsAdmin` decides who is served the admin app
and who may take the operator verbs behind it, behind a config switch that is
off until an operator turns it on. It is one boolean and it is not the beginning
of a role system. See [The admin flag](#the-admin-flag).

Two properties are worth stating up front because most of the rest follows from
them:

- **One credential, several ceremonies.** An invite code, a wait-for-approval
  queue, a passkey, and a tailnet-authenticated auto-enrollment are all just
  different ways to *acquire* the same row. The ceremony is a seam
  ([`AuthService`](../src/Aerie.Api/Services/Auth/AuthService.cs)); the grant is
  the thing.
- **One decision, two enforcers.** Traefik's `forwardAuth` and the in-process
  middleware both call the same [`AuthGate`](../src/Aerie.Api/Services/Auth/AuthGate.cs).
  Two gates with two allow-lists is one allow-list too many.

Operator-facing instructions — how to enroll a device, how to revoke one, what
to do when nobody can get in — live in [`README.md`](../README.md#enrolling-a-device).
This document is the why.

## What is behind the wall, and what isn't

| Host | Gated | Notes |
|---|---|---|
| `home.${DOMAIN}` | Yes, at `/` | Every family app, the admin app, the docs browser. The allow-list below is what stays reachable through it, in one place, rather than a second list of unannotated paths that could disagree. The admin app has a second boundary inside this one — see [The admin flag](#the-admin-flag) — which is about *which* enrolled person, not about being enrolled. |
| `kiosk.${DOMAIN}` | Yes, at `/` | The dashboard the tablets load. Its Ingress carries auth *first* in the middleware list, ahead of the root rewrite: rewriting a request that is about to be refused wastes the work and puts the rewritten path into the return-to. |
| `files.${DOMAIN}` | No | The kiosk APK and its signing checksum are public by design. This host simply never gets the annotation, and `Auth:ExemptHosts` repeats it so the in-process gate agrees with the proxy. |
| `share.${DOMAIN}` | No | dufs holds its own credential ([`file-share.md`](file-share.md)). Deliberately left alone: the share is slated for a rebuild, and annotating an Ingress in front of something that is about to be replaced buys nothing. Its Ingress is the one line to add when that changes. |
| `logs.${DOMAIN}`, `status.${DOMAIN}` | No | OpenSearch Dashboards and Uptime Kuma keep their own logins. See [Deferred on purpose](#deferred-on-purpose) — putting them behind this wall is one annotation each plus each tool's proxy-auth config, once they can be told to trust `X-Aerie-Label`. |

**One consequence the operator chose**, worth knowing before someone reports it
as a bug: a printed storage-bin QR label scanned by an un-enrolled phone lands
on sign-in rather than on the bin. The return-to redirect carries the phone
through to the bin after redemption, so it is one extra tap — but a guest
holding a labeled bin can no longer scan it.

## The grant model

Two tables in the core `AerieContext`, `public` schema
([`Ef/Auth.cs`](../src/Aerie.Api/Ef/Auth.cs)). Auth is infrastructure for every
module rather than a family app, so it does not live in a module schema — a
`Modules/` home would make every module depend on one module, which
[`Modules/README.md`](../src/Aerie.Api/Modules/README.md) forbids.

**`AuthGrants`** — one enrolled device. `TokenHash` (unique index), `Label`,
`Kind` (`Interactive` for a person's browser, `Device` for a tablet),
`CreatedAt`, `LastSeenAt`/`LastSeenIp`, `UserAgent`, `ExpiresAt` (null means
until revoked, which is the default and the point), and `CookieIssuedAt`.
Revocation is a `DELETE`.

**`AuthInvites`** — one outstanding enrollment code. `CodeHash` (unique index),
an optional `Label` to pre-fill on the grant it becomes, `ExpiresAt`,
`RedeemedAt` (concurrency-checked, so two replicas racing one code produce one
grant and one `DbUpdateConcurrencyException`), `RedeemedGrantId`, and
`IsBootstrap`. Modeled on `EfOAuthState`: same shape, same opportunistic sweep
of expired rows when a new one is created, no job.

**`People`** — one human the household knows about
([`Ef/People.cs`](../src/Aerie.Api/Ef/People.cs)). `Name`, an unenforced
`IsAdmin`, `CreatedAt`/`UpdatedAt`, and an optional `PersonPhotos` row hanging
off it. `AuthGrants.PersonId` is the nullable FK this section used to promise
was deliberately absent; `AuthInvites.PersonId` is the same value carried
through the enrollment ceremony. Both are covered in
[Whose device is this](#whose-device-is-this).

### The two secrets

Both are generated, hashed, formatted and parsed in exactly one place
([`AuthTokens`](../src/Aerie.Api/Services/Auth/AuthTokens.cs)), and neither is
ever stored or logged in the clear.

| | Grant token | Invite code |
|---|---|---|
| Shape | 256 bits, base64url | 8 Crockford base32 chars, shown `AERIE-K3M9-P2QT` |
| Read by | A browser, and nothing else | A person, aloud, across a room |
| Lifetime | Until revoked | 15 minutes, single use |
| At rest | SHA-256 | SHA-256 |

Plain SHA-256 rather than a password KDF is correct for the token — the input is
full-entropy random, so there is nothing for an iteration count to defend — and
is a bounded, deliberate compromise for the 40-bit code, which is protected by
its TTL, its single use, an IP-partitioned rate limiter on redemption, and a
Warning log per refusal instead.

The code's alphabet drops `I`, `L`, `O` and `U`, and parsing folds `I`/`L` → `1`
and `O` → `0`, strips dashes and separators, and uppercases — so a code heard
wrong and typed back still lands. The prefix comes off *before* the fold, since
`AERIE` contains an `I` and folding first would turn it into `AER1E` and stop
matching itself. [`apps/auth`](../src/Aerie.Web/apps/auth/README.md) carries the
client half of that rule in `src/lib/inviteCode.ts`; the two disagreeing is a
code the field accepts and the server refuses, so they change together.

### Why an opaque token and not a JWT or an ASP.NET auth cookie

There is no persisted DataProtection key ring across the three API replicas, so
a cookie encrypted on one replica fails to decrypt on another. A JWT brings a
signing key to distribute and rotate, clock skew, and no revocation short of a
deny-list — which is a database read on every request, i.e. exactly the thing
the JWT was supposed to avoid. A hashed opaque token has no key ring, no skew,
and is revoked by deleting a row. The verify path is a single index seek on a
unique hash column, with `LastSeenAt` writes throttled to once a minute per
grant so the hottest path in the app stays a read.

## The two gates

**Traefik `forwardAuth`** ([`middleware-auth.yaml`](../charts/aerie/templates/middleware-auth.yaml))
is the outer half. Any Ingress that carries the annotation asks
`GET /api/auth/verify` about every request and serves it only on a 2xx. One
annotation puts a whole host behind the wall, including services we did not
write — which is what makes `share.`, `logs.` and `status.` a one-line change
each rather than an integration.

`Verify` reconstructs the original request from Traefik's `X-Forwarded-Method` /
`-Proto` / `-Host` / `-Uri`, because the proxied request's own path is always
`/api/auth/verify`. It **fails closed** when those headers are missing: that
path is itself on the allow-list, so falling back to the request's own path
would answer `204` to everything.

**`AuthMiddleware`** ([`Common/AuthMiddleware.cs`](../src/Aerie.Api/Common/AuthMiddleware.cs))
is the inner half, and is not redundant. A pod reached directly inside the
cluster — by another workload, or a `kubectl port-forward` — never passes
through Traefik at all, and under `make run` there is no proxy in the picture
whatsoever, which makes this the only gate in local development. It also does
the one thing `forwardAuth` structurally cannot: **re-issue the cookie**.
Traefik copies back only the headers named in `authResponseHeaders`, and
`Set-Cookie` is not among them.

It registers after `UseForwardedHeaders` (a refusal logs the client IP) and
before the `/apps` static file handlers (otherwise the SPA bundles serve to
anyone). Unconditionally, before anything else, it strips inbound
`X-Aerie-Grant`/`X-Aerie-Label`: Traefik sets those on the proxied request from
its own `authResponseHeaders`, so a client reaching a pod directly could
otherwise hand itself an identity that downstream code has every reason to
believe.

### `auth.mode` is the whole rollback

One value in [`values.yaml`](../charts/aerie/values.yaml), supplied as the
`AUTH_MODE` operator variable, sets both switches and decides which Ingresses
are annotated:

| `auth.mode` | `Auth__Enabled` | `Auth__EnforceInProcess` | Middleware rendered | Annotated |
|---|---|---|---|---|
| `none` | `false` | — | no | nothing |
| `canary` | `true` | `false` | yes | the `docs` Ingress only |
| `full` | `true` | `true` | yes | `home` + `kiosk` |

`none` returns the deployment to exactly its pre-auth behavior, which is what
makes it a complete rollback rather than a half-off state.

`canary` is a rung on that ladder rather than a branch off it, and it is worth
keeping. It renders a second Ingress on `home.${DOMAIN}` covering `/apps/docs`
and `/api/docs` alone, with the in-process gate deliberately passive so Traefik
is the only enforcer — the wall in front of one app that nothing in the house
depends on, which is how the wall first went up and how the next change to the
gate should be rehearsed. Note the honest cost while it is set: the pod enforces
nothing on its own, so anything reaching the Service directly is as open as it
was before auth existed.

Two things decide whether an annotated route actually gates, and neither is
visible in a `helm diff`:

- **Router priority.** The `home` Ingress claims `/` on that host. Traefik ranks
  routers by rule length, so `PathPrefix('/apps/docs')` outranks
  `PathPrefix('/')` and the annotated route wins. If that ever goes the other
  way, the request is served **unauthenticated** — a failure that looks exactly
  like success.
- **The `<namespace>-<name>@kubernetescrd` naming rule.** A bare middleware name
  is silently not found and, again, the route serves unauthenticated.

Only an actual `302` from an un-enrolled client proves either one.

> **The vocabulary is `none | canary | full`, and never `off | on`.** Found live
> on 2026-08-21: `AUTH_MODE=on` reached Helm as the boolean `true` and failed
> the chart's guard on every reconcile while the cluster went on quietly serving
> the previous mode. The value is substituted textually into a manifest parsed
> as YAML 1.1, where `on` and `off` are booleans — and quoting cannot save it,
> because Flux substitutes `${AUTH_MODE}` *after* `kustomize build` has
> re-serialized the manifest, and kustomize normalizes double quotes, single
> quotes and even an explicit `!!str` tag down to a plain scalar. The quotes an
> author writes are erased before substitution happens, so the type is decided
> entirely by the word. These three words are not YAML scalars of any other
> type. Any rung added later has to clear the same bar: avoid the YAML 1.1
> boolean set in any casing, and anything that reads as a number or a null.
>
> The `fail` guard in `middleware-auth.yaml` is what turned that into a named,
> failed reconcile instead of a cluster reporting Ready with the wall down.
> Keep it — the next bad value will be a typo.

### `auth.admin` is the second axis

`auth.mode` says *where the wall stands*. It does not say anything about who is
behind it, because for the wall's whole first life everyone behind it was the
same. `auth.admin` — `ADMIN_MODE`, `none | enforced` — is the value that
changes that, and it is a second axis rather than a fourth rung for a reason
worth stating: the two answer different questions and compose freely. A ladder
with `admin` on the top rung would say enforcement implies `full`, which is
false, and would make the two impossible to roll back independently.

| `auth.admin` | `Auth__EnforceAdmin` | Effect |
|---|---|---|
| `none` | `false` | every enrolled device can do everything — the pre-flag posture |
| `enforced` | `true` | the admin app and its verbs want a person carrying `IsAdmin` |

It is meaningless without a wall, and
[`AdminGate`](../src/Aerie.Api/Services/Auth/AdminGate.cs) enforces that pairing
rather than trusting it: `Enabled` reads *both* switches, so `enforced` on an
install at `auth.mode: none` stays dormant. That combination is not an error the
chart can refuse — it renders fine — so `Test-AppTier.ps1` names it instead, in
the auth.5 checks. An operator who asked for enforcement and silently did not
get it is precisely the half-state those checks exist for.

The vocabulary rule from the box above applies here unchanged, and this value is
where it would have bitten next: the obvious pair for a yes/no is `off | on`,
which is exactly the pair that broke a reconcile on 2026-08-21. Neither `none`
nor `enforced` is a YAML scalar of any other type, and `middleware-auth.yaml`
carries the same `fail` guard for it.

### Where the wall is, and is not, deployed

The wall is a k3s-only feature. `Auth:Enabled` is `false` in
`appsettings.json`, and `false` again in `appsettings.Development.json` so
`make run` stays frictionless — the local flip is
`Auth__Enabled=true dotnet run`. Nothing but the chart turns it on.

That once included the legacy Windows/Caddy host, which passed no `Auth__*` at
all and ran covered by the LAN and tailnet boundary alone. It was never
targeted — Caddy `forward_auth` labels would have been written to be deleted —
and the [cluster cutover](plans/swarm/phase-7-cutover.md) has since retired the
host and the compose files that described it. The wall is now the only path
into the app there is.

## The cookie

`__Secure-aerie_grant`, `Domain=.${DOMAIN}`, `Path=/`, `HttpOnly`, `Secure`,
`SameSite=Lax` ([`AuthCookie`](../src/Aerie.Api/Services/Auth/AuthCookie.cs)).

- **The `Domain` attribute is the entire single-sign-on story.** One enrollment
  covers `home.`, `kiosk.`, and anything else under the domain, and it costs one
  attribute. This is also why the prefix is `__Secure-` and not `__Host-`:
  `__Host-` forbids `Domain`.
- **`SameSite=Lax`, not `Strict`.** A grant that a printed QR label or a link
  from someone's messages cannot carry looks revoked every time it is used the
  way this app is meant to be used. `Lax` still withholds the cookie from
  cross-site POSTs, which is the CSRF case `Strict` is actually for.
- **"Permanent" is a server-side property only.** Chrome clamps cookie `Max-Age`
  to 400 days regardless of what is sent. The middleware re-issues the same
  token with a fresh `Max-Age` once `CookieIssuedAt` is older than
  `Auth:GrantRenewAfterDays` (30), so an in-use device never lapses and a device
  untouched for over a year re-enrolls. Nothing is invalidated by a re-issue, so
  there is no rotation window to race.
- **Local dev overrides the name and the domain.** A browser rejects a
  `__Secure-` cookie that arrives without TLS, and an empty `CookieDomain` is
  the only sane value on localhost.

> **A browser can hold several cookies of one name, and the stale one wins.**
> Cookie identity is (name, domain, path), so a host-only `home.${DOMAIN}`
> cookie and a domain-wide `.${DOMAIN}` one are two different cookies, both sent
> in one header. `HttpRequest.Cookies` is a dictionary and silently collapses
> them.
>
> Found live on 2026-08-22, the day after the wall went up: a phone that had
> signed in while `Auth__CookieDomain` was not yet supplied held exactly that
> pair. It authenticated on `kiosk.` and was refused `unknown_grant` on `home.`
> — and because every fresh sign-in only added another cookie that was then
> ignored, redeeming a code produced a **sign-in loop with no exit**. The
> giveaway in the database was a grant row whose `LastSeenAt` was frozen at
> `CreatedAt`.
>
> Fixed in three places, because any one alone leaves a hole: `AuthCookie.ReadAll`
> parses the raw header and `VerifyAsync` accepts if *any* presented token
> verifies; `AuthCookie.Issue` sends a host-only tombstone alongside every
> cookie it writes, clearing the landmine rather than merely stepping over it;
> and `Auth__CookieDomain` renders at every `auth.mode`, including `none`, so no
> more are made — **redemption is not gated by `Auth:Enabled`**, and cannot be,
> or there is no way to enroll the first device.
>
> The tombstone is `Append`ed rather than sent through `Cookies.Delete`, which
> first strips same-named `Set-Cookie` headers already on the response and would
> therefore sign the device out on the request that just signed it in.

## What a refusal looks like

Shared by both gates ([`AuthChallenge`](../src/Aerie.Api/Services/Auth/AuthChallenge.cs))
so the two cannot drift:

- A **document request** — `Sec-Fetch-Mode: navigate`, or a `GET`/`HEAD` whose
  `Accept` contains `text/html` — gets a `302` to the sign-in shell carrying
  `?r=<where it was going>`.
- **Everything else** — `fetch`, XHR, a Sonos GET, a probe — gets a bare `401`.

A `302` returned to a `fetch` is invisible to the caller: the browser follows
it, the shell's HTML comes back with a `200`, and the calling code reports a
JSON parse error or a CORS failure somewhere unrelated to authentication. That
is the single most common way a gate like this wastes an afternoon. Only safe
methods are ever bounced, since a redirect a POST cannot follow without dropping
its body is worse than a 401 it can report.

**The return URL is the one place an open redirect would hurt most.** `?r=`
stays a rooted, same-origin path — rejecting absolute URLs, `javascript:`,
`//evil.example` and the `/\evil.example` form browsers fold into it — and the
rule is applied twice: by `AuthChallenge.SafeReturnTo` when the gate *writes* the
parameter, and again by the shell on arrival, because by then the query string
is whatever the address bar says.

> **The `Location` out of `/api/auth/verify` must be absolute; everywhere else
> it must be relative.** Traefik never hands the browser what that endpoint
> writes — it resolves the auth server's `Location` against its *own* request to
> that server. A relative `/apps/auth/?r=…` therefore left the cluster as
> `http://api.aerie.svc.cluster.local:8080/apps/auth/?r=%2F`, an address nothing
> on the LAN can reach.
>
> Found live on 2026-08-21, minutes after the wall reached the house. It is a
> nasty one because every cheap check passes: the 302 is on time, it carries the
> right `?r=`, `curl` with `Accept: */*` still gets its clean 401, and an
> already-enrolled browser sails through and notices nothing. Only an
> un-enrolled *browser* sees it.
>
> `Verify` rebuilds the origin from `X-Forwarded-Proto`/`-Host`, so each host
> still bounces to itself, and trusts that header **only inside
> `Auth:CookieDomain`** — a `Host` naming anywhere else falls back to the
> relative form. The in-process middleware keeps the relative form, which is
> right for it: the browser resolves it against whichever host it was going to.

**A 401 has to be somebody's job on the client, too.** The gate answers a fetch
with a bare 401 on purpose, but for a while nothing did anything with it: the
kiosk tablets sat for hours rendering hours-old data, because the dashboard's
data source threw and the last good snapshot stayed on screen, while
`appVersion` returned `null`, which its poller reads as "offline, the next poll
covers it" — so the self-update reload that would have rescued them never fired
either. Their only remaining escape was `MainActivity`'s 12-hour backstop
reload. This is not migration cleanup: a revoked grant, or one lapsing past the
browser's 400-day cap, produces the same silence forever. Each SPA carries
`lib/signIn.ts`, and every fetch choke point calls `handledUnauthorized(res)`,
which navigates to the shell carrying `?r=`.

## The allow-list is load-bearing

Exempt paths live in exactly one place —
[`AuthGate`](../src/Aerie.Api/Services/Auth/AuthGate.cs) — and are matched by
path *segment*, so `/media` is exempt and `/mediafoo` is not. Two entries fail
silently and confusingly if they are ever dropped.

| Path | Why it stays open |
|---|---|
| `/health/live`, `/health/ready` | Kubernetes probes present no cookie. Gating these fails readiness on every pod and the Deployment never becomes available — a total outage whose cause looks nothing like auth. |
| **`/media/*`** | **The one that bites.** Sonos speakers fetch the stream themselves and cannot hold a cookie. Gating it stops all music with no error that mentions authentication. Read from `MediaLibrary:RequestPath` rather than hardcoded, because the prefix is deploy-time config and the two drifting apart is exactly the silent failure this list exists to prevent. |
| `/api/ui-logs` | Browser log shipping, including from the sign-in shell itself. A gated log endpoint means the failures you most want to see are the ones that cannot report. |
| `/api/vm-console-logs` | Server-to-server from Hyper-V scheduled tasks, which hold no cookie — and it carries its own `X-Vm-Log-Token` gate, which is strictly stronger than a cookie would be. |
| `/api/kiosk/provisioning-info` | Tablet provisioning stays friction-free. Nothing behind it is more sensitive than an APK URL and the Wi-Fi credentials the tablet is about to join with anyway. |
| `/api/sun-events` | The kiosk *shell* drives the tablet backlight from the day's sun events and calls this over plain `HttpURLConnection`, which shares no cookie jar with the GeckoView the page runs in — the page's grant cannot cover it. It exposes sunrise/sunset times, from which the site's approximate latitude is inferable; strictly less than the row above already hands out, and it takes `lat`/`lon` overrides, so it is a solar calculator far more than it is a location. |
| `/apps/auth/*`, `/api/auth/verify`, `/api/auth/redeem` | The sign-in shell and the two endpoints it calls. Gating these is an infinite redirect loop. |
| `/auth` and `/auth/`, **exactly** | The shell's short alias — short enough to read out over the phone to someone holding a new tablet — which `Program.cs` redirects into `/apps/auth/`. Exempt *exactly* rather than by prefix, so nothing later mounted underneath it inherits the exemption. It has to be exempt in `AuthGate` rather than by reordering the rewriter, because in production the decision is Traefik's, asking about the original URI, and never reaches this app's rewrite rules. |

### Exempt is not the same as anonymous

One entry on that list is let through unconditionally *and* has its caller
identified: `/api/ui-logs`. `AuthGate.IdentifiesWithoutEnforcing` names it, and
`AuthMiddleware` responds by resolving the cookie and attaching the grant
before calling `next` — no challenge, and no cookie re-issue either, since the
sliding window belongs to requests that actually go through the wall.

This is a fix rather than a feature. `UiLogsController` had always read the
authenticated grant to stamp an `actor` field on each relayed browser line, and
the middleware had always returned before attaching one on an exempt path — so
that field was silently null on every line the app ever shipped. Conflating "we
will not refuse this" with "we will not look at this" is what produced it, and
the two lists are now separate.

Deliberately one entry, not a blanket. Identification costs a credential
lookup, and the reason the allow-list exists at all is that the health probes
and every byte Sonos streams through `/media` must cost nothing.

`Auth:ExemptHosts` does the same job for whole hosts, repeating what the chart
expresses by not annotating an Ingress, so the in-process gate agrees with the
proxy. Host comparison drops the port: an exemption written as
`files.example.com` would otherwise stop applying the moment a request arrives
on `:8080`.

**Every refusal logs at Warning with the reason and the client IP.** Those flow
to `logs.${DOMAIN}` through the existing fluent-bit pipeline, which is how a
brute-force attempt becomes visible without building alerting for it first.

## Enrollment

The ceremony that exists today is an admin-generated invite. It is the least
machinery of the schemes considered — no pending queue, no notification path, no
polling screen — and it covers the common household case, where you are standing
next to the person.

1. The admin app's **Sessions** page mints an invite
   (`POST /api/auth/invites`) and shows it two ways: a QR encoding
   `${Apps:PublicBaseUrl}/apps/auth/r/{code}`, and the formatted code in large
   type for reading aloud, with a live countdown.
2. The device opens the QR (any camera app on any phone — the deep link is an
   ordinary URL) or types the code at `https://home.${DOMAIN}/auth`.
3. [`apps/auth`](../src/Aerie.Web/apps/auth/README.md) redeems it
   (`POST /api/auth/redeem`), the response sets the cookie, and the shell
   `location.replace`s to `?r=` — back to whatever the device was trying to
   reach.

The device-name field is pre-filled from the user agent so the Sessions list is
legible instead of a wall of `Mozilla/5.0`. Label devices, not people: "kitchen
tablet" is what you will be reading a year from now when deciding what to
revoke.

The wall remembering where you were going is what makes this feel magical rather
than merely short — it turns a printed storage-bin label scanned by a phone that
has never authenticated into one extra tap instead of a dead end.

Sessions are also viewed and deleted from that page (`GET /api/auth/grants`,
`DELETE /api/auth/grants/{id}`), which refuses to revoke the caller's own grant
so nobody revokes their way out of the room.

## Whose device is this

A **person** is a row in `People` with a name, an optional photo, and an
unenforced admin flag. A grant may point at one; a person may be pointed at by
any number of grants, including none.

That is the whole model, and the restraint is the point. A person is not an
account: there is nothing to sign in as, no password, and no scope. The wall
still authenticates a device exactly as it did before. What a person adds is a
human for a session list and a log line to name, which is the difference between
"Kitchen tablet loaded the dashboard at 6am" and "Adam did" — and, in exactly
one module, an owner for a row.

### Which end the link is edited from

The FK lives on the grant, and so does the only write path
(`PUT /api/auth/grants/{id}/person`). The admin app follows: the **Sessions**
page has the dropdown, the **People** page shows each person's devices
read-only.

A session has at most one person, so on Sessions it is one select on a row that
already exists. A person has any number of sessions, so the inverse would be a
multi-picker — on the page nobody is looking at during the one moment the
question comes up, which is while enrolling a device. That is also why an invite
can carry a `personId`: the operator generating the code already knows whose
phone they are about to hand it to, and the link is set by the redemption rather
than by a second step somebody has to remember afterwards.

`AuthInvites.PersonId` is deliberately **not** a foreign key, matching
`RedeemedGrantId`. Someone standing in the hall with a code read out five
minutes ago must still get in after an unrelated tidy-up in the admin app; a
stale id links nothing and the device still enrolls.

### A person is an authorization input

For four modules, they were not. A crate is in the garage whoever opens the app,
and the household's data is the household's. Then Quill arrived
([`quill.md`](quill.md)) — private notes, one person's own — and a person became
something a query reads.

That is the direction, not an exception to be contained. **A person identifies
an individual, and who someone is belongs in an authorization decision.** The
next feature in this direction is already visible: sharing a note with a named
person, with read or write, which is per-resource authorization keyed on a
person and nothing else. Contextual authorization — *this* person, on *this*
row, for *this* verb — is where this goes, and the model has always had room
for it.

So the thing to be careful about is not whether a person may decide something.
It is that there is not yet a permission *model*, and the shapes below are the
two ways one gets built badly:

- **A global role bolted on early.** `IsAdmin` is now read — by exactly one
  boundary, behind a config switch, and the care it was owed is spent in
  [The admin flag](#the-admin-flag) rather than skipped. It remains the coarsest
  possible answer, and it is deliberately not the shape the *next* question gets
  answered in.
- **Per-module rules invented in a corner.** Quill's ownership is the first
  clause of every query in the module — a `WHERE`, not a check some code path
  can forget to consult — and every refusal is a blank `404`, because a `403`
  would confirm that the id names a real note belonging to a real person. When
  sharing arrives, "the notes you may read" is still one expression in one
  place; it just stops being `PersonId == me`. A second module wanting the same
  thing is the signal to promote that expression, the way `ICallerIdentity` was
  promoted, rather than to write a second version of it.

What holds today, as a description of the code rather than a rule for all time:
there is one global role and no scope or permission table, and outside the
handful of operator verbs it guards, no endpoint behaves differently for one
person than for another. Ownership scoping is ordinary and needs no ceremony.
Changing what a *verb* does based on who is asking is the thing that should
arrive with a design attached — which is what the section below is.

The mechanism is [`ICallerIdentity`](../src/Aerie.Api/Services/Auth/CallerIdentity.cs),
the one way anything outside `Services/Auth/` asks who is calling. It resolves
the grant once per request and hides two things deliberately: the cookie, so a
second implementation of "who is this" cannot drift into existence for the day
the ceremony changes; and `Auth:Enabled`, since the browser still holds a
perfectly good cookie when the wall is off and a person-scoped feature that went
dark under `AUTH_MODE=none` would be dark for every developer. `AuthController`
had carried that fallback privately since the wall landed — the second asker is
what turned it into a seam.

### The admin flag

`Person.IsAdmin` had been carried for one release and read by nothing, on the
explicit condition that whatever eventually read it would design the lockout
path first. This is that design.

**What it guards.** Two boundaries, and neither is a permission model:

- **The admin app's bundle.** `/apps/admin` and everything under it — the
  assets *and* the client-side deep links, which is why
  [`AdminAppMiddleware`](../src/Aerie.Api/Common/AdminAppMiddleware.cs) is a
  path check in the pipeline rather than a filter on a route. Guarding only the
  static files would leave `/apps/admin/devices` serving `index.html` through
  `MapFallbackToFile` to anyone.
- **The verbs behind it.** Roughly forty actions carrying
  [`[RequireAdmin]`](../src/Aerie.Api/Common/RequireAdminAttribute.cs), listed
  and justified in `AdminSurfaceTests`.

**The shape of the audit**, which is the part worth carrying forward:

- **Shaping the house is guarded; operating it is not.** Creating a zone,
  importing a device, wiring a panel — guarded. Turning a lamp on, nudging a
  thermostat, triggering a routine, playing music — open, and it has to stay
  open, because the dashboard runs on a hallway tablet nobody signs in to and
  the whole promise is that the family never has to.
- **Reads stay open, with two exceptions.** Both are inventories of credentials
  rather than facts about the house: the session lists
  (`AuthController.ListGrants`, `PeopleController.GetSessions`) and the
  connection settings (`SettingsController` whole, and a camera's host/port/user
  on `DevicesController`). Watching a camera feed is a different question and
  stays open.
- **The family modules are untouched.** Quill, Gather, Storage and Game are the
  household's own apps. Their authorization is ownership, expressed as a `WHERE`
  clause inside the module, and a global role has nothing to say about them.

**Two refusal codes, deliberately different.** The bundle 404s; the API 403s.
A 404 on `/apps/admin` is indistinguishable from an install that never built
that SPA — several installs have not, since `Program.cs` mounts each one only
if its directory exists — so it conceals something real. A 404 on
`DELETE /api/zones/{id}` conceals nothing, because the unguarded `GET` already
listed the zone, and it would cost every future debugging session the
difference between "gone" and "not yours".

**Why enforcement is a config switch and not inferred from the data.** Every
tempting alternative — *enforce once somebody is flagged*, *enforce once a
flagged person holds a device* — makes a checkbox on the People page the thing
that turns enforcement on. That is a trap with the exact shape of the lockout
this column was carried early to avoid: an operator ticks the box for the wrong
person and loses the page they would have fixed it from. `ADMIN_MODE` cannot be
tripped over. Turning it on is a deploy; turning it off again is the same
deploy; and neither is reachable from inside a browser session that is about to
lose its access.

**The order of operations, which the switch does not enforce for you.** Flag at
least one person *and* link their device on the Sessions page, then set
`ADMIN_MODE=enforced`. Doing it the other way locks the household out of the
admin app until the next deploy, and the recovery is
[the rollback](#bootstrap-and-lockout-recovery) rather than anything reachable
from a browser. Note the sharpest edge: `POST /api/auth/invites` is itself
guarded, so an install that enforces with nobody flagged can no longer enrol
anybody at all. The migrate Job's bootstrap invite still fires — it runs
server-side, on an install with no live access — but that is a floor, not a
plan.

**What it is not.** It is not a permission model, and adopting it as one would
be the mistake this section is trying to prevent. `IsAdmin` answers "may this
person operate the house", which is a question with two answers. The question
the household is actually walking toward — *this* person, on *this* row, for
*this* verb, which is what "share this note with Ada, read only" means — is not
a coarser version of it and will not be built by adding flags next to this one.
See [Deferred on purpose](#deferred-on-purpose).

### The rules that must not quietly change

- **`SetNull`, never `Cascade`.** Deleting a person must not revoke their
  devices. Cascade there turns an administrative tidy-up into a lockout whose
  symptom is a wall tablet that stopped working for no visible reason, and it is
  one word in `AerieContext.OnModelCreating`.
- **The label is the device's, the name is the person's.** A grant's `Label` is
  free text an operator typed, so only its *id* reaches a log line — free text
  in a field people will filter on is a field that cannot be filtered on. A
  person's `Name` went through `PersonName`, so it is a value rather than a
  note, and it travels in full.
- **Who may read a row is one expression, in one place.** Quill's is the
  `PersonId` clause on every query in the module
  ([A person is an authorization input](#a-person-is-an-authorization-input)).
  Sharing will widen that expression; it must not add a second one beside it,
  and it must not become a check that runs after the rows are already loaded.
- **`IsAdmin` is enforced from config, never from the data.** The switch is
  `ADMIN_MODE`, and the reason is in [The admin flag](#the-admin-flag): every
  scheme that infers enforcement from whether anyone is flagged turns a checkbox
  on the People page into the thing that enables it, and hands an operator a
  lockout they did not ask for. If a future change wants enforcement to follow
  the data, it needs a recovery path that is not "wait for the next deploy"
  first.
- **The write path for `IsAdmin` is guarded by `IsAdmin`.** `PeopleController`'s
  `PUT` is where the flag is set, so leaving it open would let any enrolled
  device promote itself and make the whole boundary a formality. Anything that
  ever adds a second way to write that column inherits this.

### What a name may be

`Common/PersonName.cs` is the one place request text becomes a storable name.
It is permissive on purpose — emoji, any script, a name that is one dinosaur —
because this is a home, not a directory. What it removes is the set of
characters that are not names but instructions to whatever renders them:
control characters, and Unicode `Cf` format characters, which is the Trojan
Source class (`U+202E` reverses everything after it, so a name could rearrange
the log line it appears inside). The zero-width joiner is the one deliberate
exception, since dropping it turns one family emoji into four separate people.
Names are NFC-normalized so the two spellings of "José" are one string.

There is **no SQL or HTML escaping here**, and that is deliberate rather than an
oversight. EF Core parameterizes every write, and React escapes at the point of
render; an escaping pass would be a second, weaker defence in front of a
structural one, and its real failure mode is that someone later concatenates a
query believing this class made it safe. A pre-escaped name also grows
ampersands every time somebody opens the edit form.

Length is counted in **grapheme clusters** — 60 of them — rather than in the
UTF-16 code units the column is measured in. A flag is one character to a reader
and two to .NET; a family emoji is one and eleven. Refusing a name that visibly
fits is unexplainable to whoever is standing at the form. `MaxChars` (240) is
the column's own bound and can only be reached deliberately.

### The photo

Bytes in Postgres, in a `PersonPhotos` table keyed by `PersonId`, not a path
into a volume. A row and a file on a PVC can disagree — a restored database
pointing at photos that are not there fails with no obvious symptom — whereas
bytes in the row ride the existing CNPG backup and
[`disaster-recovery.md`](disaster-recovery.md) unchanged. Its own table so that
listing people never drags the blobs along.

The type is **sniffed from the magic bytes** and the request's `Content-Type` is
ignored, because the point of validating an upload is that the uploader may be
lying; what is stored is what is served. PNG, JPEG, GIF and WebP. SVG is refused
and is the exclusion worth stating: it is a document that can carry script, and
"an image format that executes", served from the install's own origin, is stored
XSS wearing an avatar.

Nothing re-encodes. That avoids an imaging library — the obvious one is
split-licensed in a way a repo headed for open-source release should not inherit
([`ethos.md`](ethos.md)) — to solve a problem this design does not have: the
bytes are never interpreted server-side, they are capped at 2 MB during the read
by `[RequestSizeLimit]`, and they are served back with a sniffed type under
`nosniff`. The admin app downscales in a canvas before uploading, which is a
courtesy rather than a control and fails open on every browser capability it
touches.

### Bootstrap and lockout recovery

The invite generator lives behind the wall, so an install with no enrolled
device has no way in through the front door. Two recoveries, in order:

- **The bootstrap invite.** In the `AERIE_MIGRATE=1` branch of
  [`Program.cs`](../src/Aerie.Api/Program.cs), after the seeders: if there are no
  grants and no unredeemed bootstrap invite, one is minted with a longer TTL (an
  hour, because nobody is standing at the tablet when a deploy finishes) and
  logged at Warning with a banner. This is the only place a code is ever written
  to a log, and it is correct there — it is reachable only when the install has
  no way in at all. It runs in the migrate Job rather than at replica startup
  because the Job runs exactly once per deploy, ahead of any replica; doing it
  at startup would mint three invites and race. A DR restore-to-empty recovers
  for free.
- **The whole wall, off.** `AUTH_MODE=none`, re-run Provision 4, let Flux
  reconcile. See [`README.md`](../README.md#if-nobody-can-get-in) for the
  commands.

A tablet that lost its cookie is a physical visit either way; turning the wall
off gets the house back, not the tablet's enrollment.

**The admin flag adds a third lockout, with its own smaller rollback.** Everyone
is enrolled and the house works, but the admin app 404s and nobody can enrol
anyone new, because `POST /api/auth/invites` is guarded too
([The admin flag](#the-admin-flag)). This is what an install that set
`ADMIN_MODE=enforced` before flagging anybody looks like. Recover in this order:

- **`ADMIN_MODE=none`**, re-run Provision 4, let Flux reconcile. Strictly
  smaller than turning the wall off — every device stays enrolled and the wall
  stays up — so reach for it first and re-enable once somebody is flagged and
  linked.
- **The bootstrap invite still fires** if the install also has no grants at all,
  since that branch asks `HasAnyAccessAsync` rather than anything about people.
  It is a floor under the worst case, not the recovery to plan around.

The one recovery that is deliberately *not* here is a database edit. Setting
`IsAdmin` in Postgres by hand would work, and every time it is described as a
supported step it becomes the step people take instead of fixing the config that
caused it.

## Verifying it

`Test-AppTier.ps1` asserts the wall two-sidedly and mode-aware: at `none`
nothing is walled, at `full` an unauthenticated `home.${DOMAIN}/` is a `302` to
`/apps/auth/`, while `/health/ready` and a `/media/...` HEAD still return `200`
and `files.`/`status.` are unchanged. With `AERIE_TEST_GRANT_TOKEN` set
([`secrets-architecture.md`](secrets-architecture.md)) it also proves the wall
**serves** an enrolled device, which is the half a refusal check cannot cover.

The admin flag is checked from the same auth.5 block, and what it can assert
from outside the cluster is narrow on purpose: that `ADMIN_MODE` is a value the
chart knows, and that `enforced` was not asked for on an install at
`AUTH_MODE=none`, where it renders happily and then stays dormant. The rest of
the boundary is unit-tested rather than probed — `AdminGateTests`,
`AdminAppMiddlewareTests`, `RequireAdminAttributeTests`, and `AdminSurfaceTests`,
which is the endpoint audit written down as an assertion so that an endpoint
added later cannot join the open set by nobody noticing.

**Test a wall with a browser, not `curl -I`.** `curl` with `Accept: */*` gets
the 401 path and never exercises the redirect, the `Location` it carries, or
anything the client does with either — which is precisely where both of the
live failures above were hiding. Send `Sec-Fetch-Mode: navigate` and read the
`Location`, or open a private window.

Per repo convention the UI is not browser-tested in CI; the hand list that
belongs to a change to this gate is: Sonos plays a track, an enrolled browser
notices nothing, every kiosk tablet survives a reboot still signed in, and the
dashboard's `/api/app-version` poll still works from an enrolled tablet.

## Alternatives, and why not

| Option | Verdict |
|---|---|
| Third-party IdP (Authelia, Pocket ID, Keycloak) | **Rejected.** Standards-compliant and self-hosted, but it brings a new HelmRelease and its secrets, its own admin UI instead of ours, and the invite ceremony we actually wanted does not exist there — it would get bolted on anyway. Revisit if OIDC is ever needed *for* something rather than as an end. |
| mTLS client certificates | **Rejected.** The purest permanent grant and the worst UX on the platforms that matter: installing a client cert on iOS is a profile download plus three Settings screens, and revocation means a CRL nobody will operate. |
| Tailscale identity headers as the gate | **Rejected as the primary gate.** The tailnet authenticates remote access and does security work today, but devices on the house LAN are not on the tailnet — and the family devices are exactly those, so it gates the wrong population. Kept in mind as an auto-enrollment signal; see below. |
| A shared house password | **Rejected.** One secret, no revocation without rotating everyone, no per-device list. It is the thing the Sessions page exists to avoid. |

## Deferred on purpose

Each of these is additive against the grant model. None requires revisiting a
decision above.

- **Wait-for-approval ceremony.** A `PendingRequest` row, a polling screen in
  the shell, a queue on the Sessions page. Genuinely better for the case an
  invite cannot reach — they are not standing next to you — and it wants a
  notification path so you notice, which the existing Uptime Kuma / Home
  Assistant plumbing could carry.
- **Passkeys (WebAuthn).** The right long-term answer to "auth without a
  username and password", and the largest first slice: `Fido2NetLib`, a
  `Credential` table hanging off a grant, two ceremony endpoints. It solves
  *re-authentication* — a lapsed device proves itself with Face ID instead of
  finding the operator — which a permanent grant means you rarely do. It is also
  the point at which a grant stops being a device and starts being a person.
- **A permission model.** People shipped (see
  [Whose device is this](#whose-device-is-this)), are read by one module, and
  now carry one global role ([The admin flag](#the-admin-flag)). What is still
  missing is the vocabulary for saying any of it generally. The near-term shape
  is *contextual* rather than global — a person, a resource, and a verb, which
  is what "share this note with Ada, read only" is — and the model that answers
  that also answers most of what a `[RequireScope]` filter would, from the other
  end. The admin flag does not get it started: it is the coarsest possible
  answer, deliberately taken as far as one boolean goes and no further, and the
  temptation it creates is to add a second boolean beside it. A second one is
  the signal that this work is due, not that the shape was right.
- **`logs.` and `status.` behind the same wall.** One annotation each, once
  OpenSearch Dashboards' and Uptime Kuma's own logins can be told to trust
  `X-Aerie-Label` as a proxy-authenticated user.
- **`share.` behind the same wall.** One annotation, guarded on `auth.mode`
  being `full` like the other two; dufs needs no changes, since Traefik refuses
  before it proxies, and its own credential stays as a second gate. Waiting on
  the share's rebuild.
- **Kiosk tablets scanning instead of typing.** The tablets take a typed code
  today — eight characters on a soft keyboard, once per tablet, maybe once a
  year. Making the tablet's own camera read the admin's QR means a `CAMERA`
  permission, a GeckoView `PermissionDelegate` for both
  `onAndroidPermissionsRequest` and `onMediaPermissionRequest`, a silent runtime
  grant via `DevicePolicyManager.setPermissionGrantState` (which Device Owner
  makes genuinely elegant), a QR decoder in JS because Gecko ships no
  `BarcodeDetector`, and a new signed APK on every tablet. Sequence it after the
  tablets are enrolled, since they need a grant to load the dashboard that tells
  them to update.
- **Tailnet auto-enrollment.** A request arriving over the tailnet is already
  authenticated by Tailscale and could mint its own grant instead of asking for
  a code. Worth doing once the base flow is boring.
