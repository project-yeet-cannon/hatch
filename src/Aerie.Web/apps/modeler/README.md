# Aerie Modeler

A browser-only tool for sketching a home's floor plans, deriving room geometry from sparse measurements, and exporting a watertight 3D model for CFD/CAD tooling. See `/TODO_MODELING.md` at the repo root for the full spec and step-by-step plan.

Everything runs client-side — there is no API dependency. A project is a single JSON document (sketches, walls, settings) that autosaves to the browser's IndexedDB as you work, with undo/redo and file-based export/import for backup or moving between machines.

## Status

Step 1 of the plan: the persistence spine. There's no drawing surface yet (that's step 2) — the current UI is a minimal harness (add/undo/redo a wall, export/import the project file) that exercises the document schema and IndexedDB autosave end to end.

## Development

### Prerequisites

- Node.js >= 22

### Setup

```bash
npm install
```

### Running

```bash
npm run dev
```

Open the printed local URL. No backend is required.

### Building

```bash
npm run build
```

The build output is placed in `../../../Aerie.Api/wwwroot/apps/modeler` and served by the ASP.NET Core API at `/apps/modeler/` (purely as a static file host — the app itself never calls back into the API for its own data).

### Testing

```bash
npm run test
```

Covers the model core and persistence logic (schema factories, undo/redo history, project file serialization) — the parts that are pure TS and worth unit-testing. UI/browser behavior is verified manually.

### Linting

```bash
npm run lint
```

## Architecture

- `src/model/schema.ts` — the `ProjectDocument` type (versioned) and its factory. This is the single source of truth persisted to IndexedDB and to exported `.aeriemodel.json` files.
- `src/persistence/db.ts` — thin IndexedDB adapter (load/save the one active project).
- `src/persistence/history.ts` — in-memory undo/redo stack over document snapshots.
- `src/persistence/projectFile.ts` — serialize/parse for file export and import, including schema-version validation.
- `src/state/useProjectStore.ts` — React hook wiring the above together: loads on mount, autosaves on every mutation, exposes undo/redo and export/import to the UI.

## Styling

CSS variables for theming (`theme.css`), light/dark via `prefers-color-scheme`, matching the other Aerie SPAs.
