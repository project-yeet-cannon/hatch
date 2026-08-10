# Aerie Family

The shell PWA for household micro-apps (`TODO_APPS.md`). One Vite build, one
installed icon on the home screen, N apps inside it as lazily-loaded route
modules. Served by `Aerie.Api` at `/apps/family/`.

Storage Helper is the first module. Its backend lives in
`src/Aerie.Api/Modules/Storage/`.

## Adding a module

The point of the shell is that this is short:

1. `src/modules/<name>/<Name>App.tsx`, default-exporting a component that
   renders its own `<Routes>` for everything under `/apps/family/<name>/`.
2. One entry in `src/modules/registry.ts`.

That's all. The home screen card, the bottom tab, the lazy chunk, and the
route all come from the registry entry. Nothing outside `src/modules/` changes,
and nothing outside this folder changes at all — no `Program.cs`, no
Dockerfile, no CI, no deployment manifest.

Style the module out of the tokens in `src/theme.css` and nothing else. That is
the entire mechanism keeping the suite looking like one product; a module that
introduces its own palette is how ten apps stop matching.

## Development

Requires Node >= 22 (`.nvmrc`).

```bash
npm install
npm run dev
```

`vite.config.ts` proxies `/api` to `Aerie.Api` on `localhost:5197`, so run the
API alongside it (the repo `Makefile`'s `run` target, or `dotnet run`). Note
that the service worker is **not** registered in dev — see below.

```bash
npm run build   # -> ../../../Aerie.Api/wwwroot/apps/family
npm run lint
```

## PWA

`public/manifest.webmanifest` plus the `apple-touch-icon` link in `index.html`
(iOS ignores the manifest's icons for Add to Home Screen and reads that
instead). Installed, it launches standalone with no browser chrome.

### Service worker

`src/sw.js` is the source. It is not imported by the app and Vite does not
process it — the `serviceWorker()` plugin in `vite.config.ts` substitutes the
cache name, the base path and the precache list into it at build time and emits
the result as `sw.js` at the bundle root.

That plugin exists for one reason: the precache list is the set of *hashed*
asset filenames, which only exists after the bundle is built. Without it the
first offline launch fails, because the worker isn't controlling the page
during the very first visit and so nothing that visit loaded gets cached.
Precaching at install time is what makes airplane mode work after one visit
rather than two. (`vite-plugin-pwa` would supply the same list and bring
Workbox along for a policy that is ~40 lines here.)

The caching policy, per `TODO_APPS.md` Phase 2:

| Request | Strategy |
|---|---|
| Navigations | Cache-first, always the shell document — this is what makes a scanned QR open offline |
| `/apps/family/*` assets | Cache-first (precached, and content-hashed, so staleness isn't possible) |
| `/api/*` GETs | Network-first, falling back to the last good response |
| Everything else | Untouched — writes fail offline as they would with no worker at all |

Offline *writes* are deliberately out of scope; they need conflict resolution
that no current use case justifies.

Deploys fence on the cache name, which is a digest of the precache list and the
worker source: a new build means a new cache, and `activate` drops the old one.
The worker itself is registered with `updateViaCache: 'none'`, since `sw.js` is
the one file that isn't content-hashed and a stale copy would pin the app to an
old deploy.

There is deliberately no `skipWaiting()`. A build empties the output folder, so
the hashed assets a running page was loaded against no longer exist on the
server — its only remaining copy is the previous cache. Activating early would
delete that cache out from under it, and the resulting failure is both nasty and
delayed: the shell keeps working, and then tapping into a module fetches a lazy
chunk that is gone from cache *and* server. Waiting until no page is using the
old worker costs one launch of update latency and buys a page that is never
half-upgraded. It works because the precache list covers the lazy chunks too, so
the outgoing cache stays self-sufficient right up until it's dropped.

The worker is registered only in production builds
(`src/lib/registerServiceWorker.ts`) — the plugin is build-only, so `npm run dev`
would 404 on it, and caching unhashed dev modules would be actively harmful.

## Icons

`public/icon.svg` is the source for every raster in `public/`. Regenerate with
ImageMagick after editing it:

```bash
cd public
for s in 192 512; do magick icon.svg -resize ${s}x${s} -depth 8 PNG32:icon-$s.png; done
magick icon.svg -resize 180x180 -alpha remove -alpha off -depth 8 apple-touch-icon.png
```

Flat fills only, no gradients: ImageMagick's built-in MSVG renderer silently
drops them, and requiring a `librsvg` delegate to regenerate an icon is a trap
for whoever picks this up next.

## Logging

`src/lib/clientLogger.ts` and `src/lib/deviceMetadata.ts` are the same
per-app copies the other SPAs carry, shipping to `POST /api/ui-logs`. They're
duplicated because each app is a separate Vite build — the shell is what stops
*future* apps needing their own copy, since a module just imports these. It
matters more here than elsewhere: this app is used from a phone home screen,
where there is no devtools window to open when something goes wrong, so
`console.warn`/`console.error` are mirrored into the shipped log stream too.
