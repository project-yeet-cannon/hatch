# Aerie Modeler

A browser-only tool for sketching a home's floor plans, deriving room geometry from sparse measurements, and exporting a watertight 3D model for CFD/CAD tooling. See `/TODO_MODELING.md` at the repo root for the full spec and step-by-step plan.

Everything runs client-side — there is no API dependency. A project is a single JSON document (sketches, walls, settings) that autosaves to the browser's IndexedDB as you work, with undo/redo and file-based export/import for backup or moving between machines.

## Status

Step 2 of the plan: the 2D floor-plan editor. A canvas sketcher lets you draw wall centerlines (with endpoint/angle/grid snapping and automatic splitting at T-junctions), pan/zoom, select and move corners, delete walls, and name rooms that are auto-detected from closed loops in the wall graph. Steps 3+ (an explicit dimension/constraint solver, openings, multi-floor/elevation views, 3D generation, and exports) haven't started.

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

- `src/model/schema.ts` — the `ProjectDocument` type (versioned) and its factory/mutators. This is the single source of truth persisted to IndexedDB and to exported `.aeriemodel.json` files. `migrateProjectDocument` upgrades documents from older schema versions on load.
- `src/model/geometry.ts` — pure vector math and the snapping logic (endpoint, common-angle, grid) used while drawing.
- `src/model/roomDetection.ts` — derives rooms from the wall graph via planar-face tracing (closed loops = rooms), plus matching a persisted room name back to its re-derived polygon.
- `src/editor/camera.ts` — pure pan/zoom/screen-world transform math for the canvas.
- `src/components/FloorPlanEditor.tsx` — the canvas sketcher: drawing, snapping, pan/zoom, select/move/delete, room naming. Not unit-tested (canvas/pointer interaction — see Testing below); exercised by hand.
- `src/persistence/db.ts` — thin IndexedDB adapter (load/save the one active project).
- `src/persistence/history.ts` — in-memory undo/redo stack over document snapshots.
- `src/persistence/projectFile.ts` — serialize/parse for file export and import, including schema-version migration.
- `src/state/useProjectStore.ts` — React hook wiring the above together: loads on mount, autosaves on every mutation, exposes undo/redo and export/import to the UI.

## Styling

CSS variables for theming (`theme.css`), light/dark via `prefers-color-scheme`, matching the other Aerie SPAs.
