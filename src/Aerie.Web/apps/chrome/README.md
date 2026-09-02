# Aerie Chrome

The shared top bar, built for pages that are **not** Vite/React apps.

Two pages in the house are in that position:

- **Swagger UI** — Swashbuckle's own document. Aerie reaches it only through
  the `<head>` content `UseSwaggerUI` lets it inject (see `Program.cs`).
- **`apps/logo`** — a static Claude Design export, whose runtime loads its own
  React from a CDN.

Both were one-way trips from the app picker: no bar, no way back but the
browser's Back button. This build puts the same bar on them that admin, home,
docs and the modeler render.

## What it produces

`npm run build -w apps/chrome` emits into `Aerie.Api/wwwroot/apps/chrome/`:

| File | What it is |
|---|---|
| `topbar.js` | The bar. Auto-mounts from `window.aerieTopBar`. |
| `topbar.css` | `@aerie/ui`'s tokens plus the bar's own three stylesheets. |
| `swagger.css` | Dresses Swagger UI from those tokens, so its dark mode is the house's. Copied verbatim from `public/`. |
| `assets/*.woff2` | The Manrope face, content-hashed by Vite. |

The names are fixed rather than content-hashed because `Program.cs` and
`apps/logo/index.html` reference them by URL. See the note in `vite.config.ts`
for why that is safe to serve.

## Using it

Set the global, then load the script:

```html
<link rel="stylesheet" href="/apps/chrome/topbar.css">
<script>window.aerieTopBar = { appName: 'Aerie Logo', theme: 'light' };</script>
<script src="/apps/chrome/topbar.js"></script>
```

`theme` is `switch` (the page is themed — render the Auto/Light/Dark control),
or `light`/`dark` (the page has one appearance — pin it and render no control).
The options are documented in `packages/ui/src/standalone/topbar.ts`.

## Where the code is

Not here. `src/main.ts` is a single import; the bar is
`packages/ui/src/standalone/topbar.ts`, so it sits beside the React `<TopBar>`
whose stylesheet it shares. This workspace is only the build.
