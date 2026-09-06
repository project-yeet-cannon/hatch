# Aerie Home

The app picker: every service the house runs, in three tiers, on the shared
design system. It is what `/` and `/apps/` land on, and what the app-switcher
in every other app's top bar goes back to.

## Why it is an app

It used to be a single static file — `src/Aerie.Web/index.html`, CSS inlined in
a `<style>` block, copied into `wwwroot/apps/` by a `CopyAppsIndex` MSBuild
target. That file could not import a component and could not bundle a font, so
it carried its own palette, its own tile styles, and a hand-rolled
`media="print"` + `onload` hedge to keep a `fonts.googleapis.com` request off
the critical path.

All three are gone. Manrope is in the bundle through `@aerie/ui/tokens.css`, so
the first page every tablet in the house opens no longer waits on a
third-party host to draw; the card, the badge and the text tones are the
components admin and the gallery render; and the only CSS left in
[`src/App.css`](src/App.css) is the picker's own furniture — a tile, a tier
heading, and the column they sit in.

## The catalog

[`src/apps.ts`](src/apps.ts) is the list, and nothing else in the app
enumerates the services. An entry is same-origin (`href`) or a sibling on its
own subdomain (`subdomain`); adding a service is one entry.

Two entries behave differently from a plain link, and both are decisions rather
than mechanics:

- **A sibling with no domain to borrow** — `share`, `status`, `metrics`,
  `logs` are each their own container behind their own login, and their
  addresses are derived from the host this page is served on
  ([`lib/siblingOrigin.ts`](src/lib/siblingOrigin.ts)) so that the base domain
  never enters the repo. Served from an IP or from `localhost` there is no
  domain to derive one from, so the tile renders without a link and says **No
  domain**. The old page greyed the tile out with no explanation, which reads
  as broken; the service is there, and only the way to it from here is missing.
- **Admin is withheld, not greyed** — its bundle 404s outright for anyone whose
  device is not linked to an administrator (`docs/auth-architecture.md`, "The
  admin flag"). A tile pointing at that 404 would say the app is there and that
  you are not welcome in it, which is the sentence the 404 was chosen to avoid.
  [`lib/useWithheldApps.ts`](src/lib/useWithheldApps.ts) HEADs it and drops the
  tile on a 404 — and leaves it alone on any other failure, because hiding an
  app over a network blip is worse than a link that 404s once.

## Deliberate differences from the other apps

- **The top bar's app-switcher is in its home state.** `<TopBar atHome>`: the
  mark stays in the corner every other app keeps it in, and stops being a link
  to the page you are already on.
- **No client-side router, and so no `MapFallbackToFile` in `Program.cs`.** The
  picker is one page. A fallback would answer every mistyped path under
  `/apps/home/` with the picker and a 200, which is worse than the 404 that
  path deserves.
- **No `clientLogger.ts`.** Like the design gallery: this is a static bundle of
  links that makes one API call — a `HEAD` whose failure it already ignores.
  Render errors go to the console through
  [`components/ErrorBoundary.tsx`](src/components/ErrorBoundary.tsx), whose
  fallback names the apps directly, since this page is how someone reaches
  everything else.

The emoji icons are carried across from the page this replaced. What Aerie's
iconography should be is the design pass's to answer
([`docs/design-system-architecture.md`](../../../../docs/design-system-architecture.md)).

## Development

### Prerequisites

- Node.js >= 22

### Setup

Aerie.Web is a single npm workspace with one lockfile at `src/Aerie.Web`, so the
install is the workspace's rather than this app's:

```bash
npm install
```

### Run

```bash
npm run dev -w apps/home
```

The four cross-subdomain tiles show as **No domain** on `localhost` — that is
the correct behaviour, not a broken dev environment.

### Test

```bash
npm run test -w apps/home
```

`siblingOrigin` is the one piece of logic here worth asserting; the look is
verified on screen.

### Build

```bash
npm run build -w apps/home
```

Bundles into `src/Aerie.Api/wwwroot/apps/home`, which Aerie.Api serves at
`/apps/home` — and redirects `/` and `/apps/` to.
