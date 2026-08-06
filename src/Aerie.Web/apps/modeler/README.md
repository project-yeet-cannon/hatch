# Aerie Modeler

A browser-only tool for sketching a home's floor plans, deriving room geometry from sparse measurements, and exporting a watertight 3D model for CFD/CAD tooling. It exists to feed two consumers: interior-airflow CFD solvers (OpenFOAM, SimScale) and the Aerie climate controller, which wants room/opening topology for sensor context.

Everything runs client-side — there is no API dependency. A project is a single JSON document (sketches, walls, settings) that autosaves to the browser's IndexedDB as you work, with undo/redo and file-based export/import for backup or moving between machines.

## Status

Feature-complete against the original spec: persistence (IndexedDB autosave, undo/redo, project export/import), the 2D floor-plan editor (wall drawing/snapping, automatic room detection), the dimension/constraint solver (measured/derived/estimated status), openings/multi-floor/elevation views with sketch merging, `manifold-3d`-based 3D generation with a three.js viewer, and the full export set (binary STL, multi-solid ASCII STL, OBJ, GLB, topology JSON) with a pre-export watertightness validator.

The export pipeline has been validated end-to-end through a real OpenFOAM run (`snappyHexMesh` → `buoyantSimpleFoam`) for the simplest case (one room, no doors) — see `docs/cfd-validation.md` at the repo root. Not yet exercised: a multi-room case with an open doorway through an actual mesher (the geometry logic for it is covered by `exportGeometry.test.ts` unit tests, just not run through Docker/OpenFOAM). Windows are out of scope; sensor placement is deferred but the topology JSON's schema leaves room for a future `sensors` array.

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

## CFD research: why these export formats

Interior-airflow CFD doesn't consume an architectural model of the walls — it consumes the *air volume* (the negative space) as a watertight surface mesh with named boundary patches (e.g. `walls`, `floor`, `ceiling`, eventually per-door patches) so boundary conditions can be assigned. Wall thickness still matters for separating floors, correct room volumes, and future conjugate-heat-transfer studies. Target consumers, in priority order:

1. **OpenFOAM** (free, scriptable, runs in Docker) — its mesher `snappyHexMesh` takes STL/OBJ; multi-solid ASCII STL (one named solid per patch) delivers named boundaries. Geometry must be watertight (no duplicate/self-intersecting faces), which is what `exportValidator.ts` checks before a file is written.
2. **SimScale** (cloud, free community tier) — prefers CAD formats (STEP/IFC/Revit) but accepts tessellated geometry, so plain binary STL gets us in the door. STEP export would need a CAD kernel (OpenCascade/WASM) — deferred as too heavy for a browser app.
3. **Building-energy tools** (EnergyPlus/OpenStudio via gbXML) — zones + surfaces + constructions. Not implemented; would matter if the climate controller ever wants thermal-zone modeling instead of just airflow.
4. **The Aerie climate controller** — no standard format fits, so `topology.ts` emits an Aerie-specific JSON: rooms as nodes (name, floor, volume, floor area, ceiling profile), openings as edges (connected rooms, free area, sill/head heights), plus a reserved `sensors` array for a later feature.
5. **General CAD/viz** (Blender, three.js, other CAD import) — glTF/GLB and OBJ, both straightforward to emit from three.js/Manifold output.

This is why the export set is binary STL + multi-solid ASCII STL + OBJ + GLB + topology JSON, with STEP and gbXML deliberately deferred.

## Architecture

- `src/model/schema.ts` — the `ProjectDocument` type (versioned) and its factory/mutators. This is the single source of truth persisted to IndexedDB and to exported `.aeriemodel.json` files. `migrateProjectDocument` upgrades documents from older schema versions on load.
- `src/model/geometry.ts` — pure vector math and the snapping logic (endpoint, common-angle, grid) used while drawing.
- `src/model/roomDetection.ts` — derives rooms from the wall graph via planar-face tracing (closed loops = rooms), plus matching a persisted room name back to its re-derived polygon.
- `src/model/solidGeneration.ts` — pure, unit-tested per-floor layout computation (room/wall/ceiling resolution, Z stacking) plus the `manifold-3d`-backed solid generation that turns that layout into air-volume and wall meshes for the live viewer (centerline-based, ignoring wall thickness — see `exportGeometry.ts` for the export-accurate version).
- `src/model/manifoldRuntime.ts` — loads and caches the `manifold-3d` WASM module, wrapped for automatic CSG object cleanup (the WASM heap isn't garbage-collected by JS).
- `src/model/exportGeometry.ts` — computes the export-accurate air volume (rooms minus wall material, openings unioned back in as the only path between adjacent rooms) and tags each output triangle as `walls`/`floor`/`ceiling` via Manifold's `originalID` tracking. See its doc comment for why openings currently always bridge fully open (no closed-door state modeled yet, so no per-door boundary patch).
- `src/model/exportValidator.ts` — re-checks exported triangle soup for watertightness (every edge shared by exactly two triangles) and degenerate triangles before a file is written.
- `src/model/stlExport.ts` / `src/model/meshExport.ts` — binary/multi-solid-ASCII STL, OBJ, and GLB serialization from the validated mesh.
- `src/model/topology.ts` — the room/opening topology JSON export for the climate controller (nodes = rooms, edges = openings, reserved `sensors` array for a later feature).
- `src/components/ExportPanel.tsx` — UI for triggering the export formats above and surfacing validator failures.
- `src/editor/camera.ts` — pure pan/zoom/screen-world transform math for the 2D canvas.
- `src/components/FloorPlanEditor.tsx` — the canvas sketcher: drawing, snapping, pan/zoom, select/move/delete, room naming. Not unit-tested (canvas/pointer interaction — see Testing below); exercised by hand.
- `src/components/Viewer3D.tsx` — the three.js 3D view: regenerates the solids on demand, per-floor visibility and air-volume/walls toggles. Not unit-tested for the same reason as FloorPlanEditor.
- `src/persistence/db.ts` — thin IndexedDB adapter (load/save the one active project).
- `src/persistence/history.ts` — in-memory undo/redo stack over document snapshots.
- `src/persistence/projectFile.ts` — serialize/parse for file export and import, including schema-version migration.
- `src/state/useProjectStore.ts` — React hook wiring the above together: loads on mount, autosaves on every mutation, exposes undo/redo and export/import to the UI.

## Styling

CSS variables for theming (`theme.css`), light/dark via `prefers-color-scheme`, matching the other Aerie SPAs.
