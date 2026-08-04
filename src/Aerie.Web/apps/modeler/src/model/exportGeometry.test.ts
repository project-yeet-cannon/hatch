import { describe, expect, it } from 'vitest';
import { computeExportAirVolume, PATCH_CEILING, PATCH_FLOOR, PATCH_WALLS } from './exportGeometry';
import { validateExportMesh } from './exportValidator';
import { getManifoldModule } from './manifoldRuntime';
import { createEmptyProject, createId, withOpeningAdded, withRoomCeilingProfileSet, withRoomNamed, withWallAdded } from './schema';
import type { Point2, ProjectDocument, WallSegment } from './schema';

function wall(start: Point2, end: Point2, thickness = 0.15): WallSegment {
  return { id: createId(), start, end, thickness };
}

/** Two 4m x 3m rooms sharing a partition wall at x=4, with a 0.9m x 2.0m door through it. */
function twoRoomProject(): { project: ProjectDocument; wallAB: WallSegment } {
  let project = createEmptyProject();
  const sketch = project.sketches[0];

  const wallAB = wall({ x: 4, y: 0 }, { x: 4, y: 3 });
  const walls = [
    wall({ x: 0, y: 0 }, { x: 4, y: 0 }),
    wallAB,
    wall({ x: 4, y: 3 }, { x: 0, y: 3 }),
    wall({ x: 0, y: 3 }, { x: 0, y: 0 }),
    wall({ x: 4, y: 0 }, { x: 8, y: 0 }),
    wall({ x: 8, y: 0 }, { x: 8, y: 3 }),
    wall({ x: 8, y: 3 }, { x: 4, y: 3 }),
  ];
  for (const w of walls) project = withWallAdded(project, sketch.id, w);

  project = withRoomNamed(project, sketch.id, null, { x: 2, y: 1.5 }, 'Living Room');
  project = withRoomNamed(project, sketch.id, null, { x: 6, y: 1.5 }, 'Kitchen');
  for (const label of project.sketches[0].roomLabels) {
    project = withRoomCeilingProfileSet(project, sketch.id, label.id, label.seed, { kind: 'flat', wallHeight: 2.5 });
  }

  project = withOpeningAdded(project, sketch.id, { id: createId(), wallId: wallAB.id, offset: 1, width: 0.9, headHeight: 2.0, kind: 'door' });

  return { project, wallAB };
}

describe('computeExportAirVolume', () => {
  it('produces null with a warning-free empty result for a project with no rooms', async () => {
    const project = createEmptyProject();
    const result = await computeExportAirVolume(project);
    expect(result.mesh).toBeNull();
  });

  it('produces a watertight, non-degenerate whole-building air volume for two rooms and a door', async () => {
    const { project } = twoRoomProject();
    const result = await computeExportAirVolume(project);
    expect(result.mesh).not.toBeNull();

    const validation = validateExportMesh(result.mesh!);
    expect(validation.issues).toEqual([]);
    expect(validation.ok).toBe(true);
  }, 20000);

  it('carves the wall material out of the naive room volume, leaving less air than two uncarved rooms', async () => {
    const { project } = twoRoomProject();
    const result = await computeExportAirVolume(project);
    const wasm = await getManifoldModule();
    const solid = wasm.Manifold.ofMesh(new wasm.Mesh({ numProp: 3, vertProperties: result.mesh!.positions, triVerts: result.mesh!.indices }));
    const volume = solid.volume();

    // Two 4x3x2.5m rooms extruded straight from centerlines (no wall
    // subtraction) would be exactly 60 m^3 - see solidGeneration.test.ts's
    // equivalent viewer-mesh assertion. The real negative-space volume here
    // must be strictly less (walls now take up some of that space) but still
    // positive and well above zero (the door bridge keeps the two halves
    // connected instead of leaving two disjoint slivers).
    expect(volume).toBeLessThan(60);
    expect(volume).toBeGreaterThan(40);
  }, 20000);

  it('tags boundary triangles as walls, floor, and ceiling', async () => {
    const { project } = twoRoomProject();
    const result = await computeExportAirVolume(project);
    const patches = new Set(result.mesh!.triPatch);
    expect(patches.has(PATCH_WALLS)).toBe(true);
    expect(patches.has(PATCH_FLOOR)).toBe(true);
    expect(patches.has(PATCH_CEILING)).toBe(true);
  }, 20000);
});
