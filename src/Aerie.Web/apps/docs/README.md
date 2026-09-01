# Aerie Docs

Browser for the architecture docs in the repo's top-level `docs/` folder. Lists the available documents, renders the selected one as formatted markdown, and lets you click through links between docs.

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

### Running

```bash
npm run dev
```

The dev server serves static assets only — API calls (`fetch('/api/...')`) need a same-origin backend. Build and run `Aerie.Api` (see the repo `Makefile`'s `run` target) and open the built app at `/apps/docs/` to exercise real API calls.

### Building

```bash
npm run build
```

The build output is placed in `../../../Aerie.Api/wwwroot/apps/docs` and served by the ASP.NET Core API at `/apps/docs/`.

### Linting

```bash
npm run lint
```

## API endpoints

- `GET /api/docs` — list of `{ slug, title }` for every markdown file under `docs/`
- `GET /api/docs/{slug}` — raw markdown content for one document

## Architecture

A standalone Vite/React SPA (same conventions as `apps/admin`: no shared component library, plain `fetch` for API calls, `react-router-dom` with `basename="/apps/docs"`). Markdown is rendered client-side with `marked` (+ `marked-gfm-heading-id`, so links like `#some-heading` resolve the same way they do on GitHub) and sanitized with `dompurify` before being injected into the page. `DocPage` intercepts clicks on relative links ending in `.md` and resolves them by filename against the known doc list, so cross-references between docs (e.g. `reverse-proxy-architecture.md`) navigate within the app instead of reloading; external links open in a new tab.

`Aerie.Api`'s `DocsService`/`DocsController` read the actual files under `Docs:Path` (`../../docs` in dev, `docs` in the Docker image — see `Dockerfile.api`) at request time, rather than the SPA shipping its own bundled copy, so the browser always reflects the current state of the docs.
