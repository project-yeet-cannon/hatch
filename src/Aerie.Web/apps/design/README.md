# Aerie Design

The design gallery: every token and component in `@aerie/ui`, on a page you can
browse, in both themes.

This app is a workbench rather than a product surface. It is the artifact the
design pass (`docs/plans/design-system-mvp.md`, Phase 6) is handed, and it
exists so that every component built afterwards is developed here — against its
own states, in both themes — rather than inside a page of admin where only the
happy path is visible.

## What is in it

- **Foundations** — color, type, spacing, radius, elevation & motion, and the
  categorical series ramp. Each page reads the values the browser actually
  resolved rather than restating them, so a swatch can never disagree with the
  token it claims to show.
- **A theme switch in the rail**, always in the same place, so any page is one
  click from being read in the other theme.

Components join the nav as they land in `@aerie/ui`. Adding one is a single
entry in [`src/sections.ts`](src/sections.ts) — nothing else in the app
enumerates the pages.

## Two deliberate differences from the other apps

- **No `clientLogger.ts`.** The gallery makes no API calls; it is a static
  bundle a designer browses with devtools open. A seventh copy of that module
  would add to the duplication the plan is chartered to reduce and buy nothing.
  Render errors go to the console via `components/ErrorBoundary.tsx`.
- **No component CSS of its own.** The gallery shows a component by rendering
  it, never by restating its styles. A gallery whose copy of a component's CSS
  can drift from the component is a gallery that lies. The only styles in
  [`src/App.css`](src/App.css) are the gallery's own chrome and the frames its
  specimens sit in.

## Development

### Prerequisites

- Node.js >= 22

### Setup

Aerie.Web is a single npm workspace with one lockfile at `src/Aerie.Web`, so the
install is the workspace's rather than this app's — run it once, from anywhere
in the tree, and every app is installed:

```bash
npm install
```

### Run

```bash
npm run dev -w apps/design
```

`@aerie/ui` is a workspace link, so editing a token or a component in
`packages/ui` hot-reloads here with no publish step.

### Build

```bash
npm run build -w apps/design
```

Bundles into `src/Aerie.Api/wwwroot/apps/design`, which Aerie.Api serves at
`/apps/design`.
