# Aerie Auth

The sign-in shell. One screen that turns an invite code into a session cookie
and sends the browser back where it was going. Served by `Aerie.Api` at
`/apps/auth/`; the backend is `AuthController` and `AuthService`
(`src/Aerie.Api/Services/Auth/`), and the design it implements is
`docs/auth-architecture.md`.

It is the one app in the suite that is reached involuntarily: a gated request
with no cookie is redirected here by `AuthChallenge`, carrying where it was
going in `?r=`.

## Routes

| Route | What it is |
|---|---|
| `/apps/auth/` | The form: a code, a name for this device, one button. |
| `/apps/auth/r/:code` | What a scanned QR resolves to. Redeems on arrival, nothing to type. |

The deep link is why in-page QR scanning is optional: the admin's QR is an
ordinary URL, so any camera app on any phone opens it. Only the kiosk tablets
would ever need a scanner of their own, because GeckoView has no
`BarcodeDetector`.

## The three things to be careful with

**The return URL.** This is the page everybody in the house is trained to
follow, so an open redirect through it is the classic own-goal.
`src/lib/returnTo.ts` keeps `?r=` to a rooted, same-origin path — rejecting
absolute URLs, `javascript:`, `//evil.example`, and the `/\evil.example` form
browsers fold into it. `AuthChallenge.SafeReturnTo` applies the same rule when
the gate *writes* the parameter; this re-applies it on arrival, because by then
the query string is whatever the address bar says.

**The code in the URL.** On `/apps/auth/r/<code>`, `location.href` is
credential material. `src/lib/redactUrl.ts` strips it before anything is shipped
to `/api/ui-logs` (and `index.html` repeats the rule inline, since it logs
before this bundle is fetched). Nothing may put a live invite code into
OpenSearch, where it would outlive its fifteen minutes by the retention period.

**Agreement with the server.** `src/lib/inviteCode.ts` is the client half of
`AuthTokens.NormalizeInviteCode` — same alphabet, same Crockford folding, same
prefix-before-fold order. The two disagreeing means a code the field accepts and
the server refuses, which reads as "the code doesn't work" while both halves
behave exactly as written. Both are covered by tests; change them together.

## Development

Requires Node >= 22 (`src/Aerie.Web/.nvmrc`). Aerie.Web is a single npm
workspace with one lockfile at `src/Aerie.Web`, so the install is the
workspace's rather than this app's — run it once and every app is installed:

```bash
npm install
npm run dev
```

`vite.config.ts` proxies `/api` to `Aerie.Api` on `localhost:5197`, so run the
API alongside it (the repo `Makefile`'s `run` target, or `dotnet run`).

```bash
npm run build   # -> ../../../Aerie.Api/wwwroot/apps/auth
npm run lint
npm run test
```

The gate is off by default, including in Development, so the shell is reachable
but nothing redirects to it. To see the whole loop, run the API with
`Auth__Enabled=true` and open a gated page: it bounces here, and redeeming a
code minted from the admin app (or `psql`) lands back on it.

## Logging

`src/lib/clientLogger.ts` and `src/lib/deviceMetadata.ts` are the same per-app
copies the other SPAs carry, shipping to `POST /api/ui-logs` — with the URL
redaction described above, which is this app's alone. `/api/ui-logs` is on the
gate's allow-list precisely so this page can report its own failures: a gated
log endpoint means the failures you most want to see are the ones that cannot
report.
