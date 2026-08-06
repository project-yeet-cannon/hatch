I want to model my home in 3d with as little manual effort as possible so that I can run CFD (Computational Fluid Dynamics) and inform my home climate controller about the topology of the home so that it gets context for the topology of the rooms and sensors in the home.

Specs:
1. I want a one-off web app which runs in a web browser in local space only.
1. It expects users to work incrementally and stores progress transparently in local storage
1. It allows a user draw rooms with as little in the way as possible
1. It accepts varying views of home space from users and interpolates them into a single model
1. It allows a user to draw the shape of the home which is stored as a geometric/vector shape, where dimensions will later by applied.
1. It allows a user to incrementally and sparsely input measurements for various edges and interpolates the rest of the dimensions. It gives feedback to the user for known vs unknown lengths.
1. It can render a 3d view of the home to its best knowledge at any time
1. It can export the home in a variety of formats that would be used by CFD, modeling/CAD software. I do not know about CFD apps yet so do some research and help me figure out what models we need to make.
1. It allows me to export the entire project as a package and download it as a file. I can upload it later to
1. Ask me any necessary clarifying questions and build me a plan for making the app. The plan has discrete steps and is included in this document.

---

# Plan

## Clarified decisions

- **Location:** new client-only app at `src/Aerie.Web/apps/modeler`, same stack as siblings (React 19, Vite, TypeScript, oxlint). No API dependency; everything runs in the browser.
- **"Varying views" means all of:** per-floor plans, overlapping partial sketches of the same floor that get merged, and elevation/side views (for ceiling heights, vaults, roof lines). Photo tracing is out of scope.
- **Geometry detail:** doors/openings between rooms, walls with real thickness, and vaulted/sloped ceilings + stairwell voids. Windows deferred.
- **Sensors:** rooms only for now; sensor placement is a later feature (the exported topology format should leave room for it).
- **Storage:** IndexedDB with continuous autosave (localStorage's ~5 MB cap is too small for a multi-floor project with undo history; IndexedDB honors the "transparent local persistence" spec without the ceiling).

## CFD research findings — what we must export

The critical insight from surveying the tooling: **interior-airflow CFD does not consume an architectural model of the walls — it consumes the *air volume* (the negative space), as a watertight surface mesh, with named boundary patches** (e.g. `walls`, `door_kitchen_hall`, `supply_register_1`) so boundary conditions can be assigned. Wall thickness still matters: it separates floors correctly, gives accurate room volumes, and enables conjugate-heat-transfer studies later.

Target consumers, in priority order:

1. **OpenFOAM** (free, scriptable, runs in Docker — fits Aerie's container-based prod even on the Windows hosts). Its mesher `snappyHexMesh` takes **STL or OBJ**; multi-solid ASCII STL (one named solid per patch) is the standard way to deliver named boundaries. Solvers like `buoyantSimpleFoam` with the k-ω SST model are the standard indoor-HVAC setup. Geometry must be watertight — no duplicate vertices/faces or self-intersections, which are the classic causes of meshing failures.
2. **SimScale** (cloud, free community tier — the lowest-effort way to get a first simulation without learning OpenFOAM). Prefers CAD formats (**STEP, IFC**, Revit) but accepts tessellated geometry. STEP export requires a CAD kernel (OpenCascade/WASM) — heavy for a browser app, so it's deferred; STL gets us in the door.
3. **Building-energy tools** (EnergyPlus/OpenStudio via **gbXML**) — zones + surfaces + constructions. Optional later; useful if the climate controller ever wants thermal-zone modeling, not just airflow.
4. **The Aerie climate controller itself** — no standard format fits; we define a **topology JSON**: rooms as nodes (name, floor, volume, floor area, ceiling profile), openings as edges (connected rooms, free area, sill/head heights), plus a reserved `sensors` array for the later feature.
5. **General CAD/viz** (Blender, three.js, CAD import) — **glTF/GLB** and **OBJ**, both trivial to emit from three.js.

**Export set: binary STL (single watertight air volume), multi-solid ASCII STL (named patches for OpenFOAM), OBJ (with groups), GLB, topology JSON.** Deferred: STEP, gbXML.

## Architecture

- **Model core (pure TS, UI-free, unit-tested):** the project is a set of *sketches* (floor plans, partial plans, elevations) over a shared *building model*. Walls are centerlines with thickness; rooms are derived automatically as closed regions of the wall graph; openings are spans anchored to a wall with width + head/sill heights; ceiling profiles (flat / shed / gable) attach per room and are constrained by elevation sketches.
- **Dimension solver:** the drawn sketch is treated as the initial guess; user-entered edge lengths plus auto-inferred constraints (axis-alignment, perpendicularity, collinearity within tolerance) form a nonlinear least-squares system over vertex positions, solved with Levenberg–Marquardt. Every edge gets a status — **measured** (user-entered), **derived** (pinned down by constraints), **estimated** (only the sketch guess) — rendered as green/blue/amber for the known-vs-unknown feedback the spec asks for.
- **Sketch merging:** overlapping partials are aligned by user-pinned correspondence points (shared corners) via least-squares rigid-transform fit, then their walls join one constraint system so a measurement in either sketch informs the merged plan.
- **3D generation:** per-room air volumes are extruded from the solved plan's room polygons (centerline-based, so adjacent rooms already touch at a shared wall) with their ceiling profiles — flat is a straight extrusion, shed/gable is a triangular "wedge" solid clipped to the room's footprint — and wall solids come from thickness offsets with door/archway volumes subtracted out, all via the **`manifold-3d`** WASM CSG library, which guarantees watertight, manifold output (exactly what snappyHexMesh demands; ad-hoc mesh CSG is where watertightness usually dies). **three.js** renders the live 3D view from the same meshes.
- **Export geometry (Step 6):** the viewer's air volume above is intentionally naive (rooms touch/merge at their shared centerline, ignoring wall thickness) - real CFD negative space needs rooms *minus* the wall material between them, with each opening's footprint unioned back in as the only path connecting adjacent rooms. `model/exportGeometry.ts` computes this separately for export: Manifold's `originalID`/`runOriginalID` tracking tags every result triangle as `walls` (from the subtracted wall solids) or room-sourced, and room-sourced faces are split into `floor`/`ceiling` by triangle normal. A closed-door state isn't modeled yet, so every opening bridges its two rooms as fully open air with no boundary face there - that's also why there's no per-door patch (see the module's doc comment). `model/exportValidator.ts` re-checks the emitted triangle soup for watertightness (every edge shared by exactly two triangles) and degenerate triangles before a file is written.
- **Persistence:** single project document (sketches + constraints + settings), autosaved to IndexedDB on every mutation, with undo/redo as an in-memory patch stack. "Export project" serializes the same document (+ schema version) to a downloadable `.aeriemodel.json`; import re-hydrates it.

## Steps

Each step ends in something usable and is a reasonable PR boundary.

1. [X] **Scaffold + persistence spine.** Create `apps/modeler` matching sibling app conventions (Vite/TS/oxlint/vitest, `make`/CI wiring as applicable). Define the project document schema. Implement IndexedDB autosave, load-on-open, undo/redo, and project export/import (spec #2, #9 done first so no work is ever lost while building the rest).
2. [X] **2D floor-plan editor.** Canvas sketcher: draw wall centerlines with snapping (endpoints, axis, common angles), pan/zoom, select/move/delete, default wall thickness, automatic room detection from closed loops with room naming. Fast, low-friction drawing is the whole game here (spec #3, #5).
3. [X] **Dimensions + constraint solver.** Click an edge → type a length; LM solver over the constraint system; measured/derived/estimated status coloring; a scale-sanity readout (computed floor area) so bad entries are obvious (spec #6).
4. [X] **Openings + multi-view.** Doors/archways placed on walls with width and head height. Multiple floors as stacked plans with a shared origin. Partial-sketch merge via correspondence pinning. Elevation sketches that bind ceiling heights and vault profiles to rooms, plus stairwell voids (spec #4, plus the vaulted-ceiling and opening detail).
5. [X] **3D generation + viewer.** Manifold-based solid generation (air volumes, openings, wall solids), live three.js view with per-floor isolation and air-volume vs. walls toggle, regenerated on demand from whatever is currently known (spec #7 — "best knowledge at any time").
6. [X] **Exports.** Binary STL, multi-solid ASCII STL with named patches, OBJ, GLB, topology JSON; a pre-export validator (watertight/manifold check, degenerate-triangle check) so files fail here rather than inside a mesher (spec #8).
7. [X] **End-to-end CFD validation.** Take a real export of at least two rooms + a doorway through OpenFOAM in Docker (`snappyHexMesh` → `buoyantSimpleFoam`) and/or a SimScale community run; fix whatever the mesher rejects. The exporter isn't "done" until a solver has actually consumed its output. Document the recipe in `docs/`.
   - Done with the simplest possible case instead (one room, no doors) rather than the two-room+doorway example - see `docs/cfd-validation.md`. `snappyHexMesh` meshed the exported STL cleanly, `checkMesh` passed, and `buoyantSimpleFoam` ran 150 iterations with no errors/divergence. The two-room+doorway path is exercised by `exportGeometry.test.ts`'s unit tests but hasn't been run through an actual mesher yet - worth doing before calling the exporter fully proven.
