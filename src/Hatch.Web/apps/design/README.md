# Hatch Design

The design gallery: every token and component in `@hatch/ui`, on a page you can
browse, in both themes.

This app is a workbench rather than a product surface. It is the artifact the
design pass
([`docs/design-system-architecture.md`](../../../../docs/design-system-architecture.md))
is handed, and it exists so that every component built afterwards is developed
here — against its own states, in both themes — rather than inside a page of
admin where only the happy path is visible.

## What is in it

- **Foundations** — color, type, spacing, radius, elevation & motion, and the
  categorical series ramp. Each page reads the values the browser actually
  resolved rather than restating them, so a swatch can never disagree with the
  token it claims to show.
- **Components** — each one in its states and contexts, rendered rather than
  described: the top bar, the page header, and the nine primitives admin is
  built out of. Every page shows the states that are not the happy one —
  disabled, loading, error, empty, long content — because those are the ones
  a designer never sees by clicking through a working app.
  The specimens are live. The buttons press, the fields take typing, the
  modals take over the page and give focus back. A specimen that mocks its own
  behaviour is a specimen that can be wrong about it.
- **A theme switch in the bar**, always in the same place — the same place
  every other Hatch app keeps it — so any page is one click from being read in
  the other theme.

The gallery wears the bar it documents. That is deliberate: the one context a
top bar is never shown in on a specimen page is a real app, and this is the app
that can show both at once.

Components join the nav as they land in `@hatch/ui`. Adding one is a single
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
  specimens sit in — and the two stand-ins it once held for a button and a
  badge are gone, replaced by the real ones the moment those landed.

## Development

### Prerequisites

- Node.js >= 22

### Setup

Hatch.Web is a single npm workspace with one lockfile at `src/Hatch.Web`, so the
install is the workspace's rather than this app's — run it once, from anywhere
in the tree, and every app is installed:

```bash
npm install
```

### Run

```bash
npm run dev -w apps/design
```

`@hatch/ui` is a workspace link, so editing a token or a component in
`packages/ui` hot-reloads here with no publish step. That is the loop the
gallery is for: change a value, see it in both themes across every component
that uses it, without a build.

### Build

```bash
npm run build -w apps/design
```

Bundles into `src/Hatch.Api/wwwroot/apps/design`, which Hatch.Api serves at
`/apps/design`.
