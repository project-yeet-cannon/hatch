import { describe, expect, it } from 'vitest';
import {
  assignWallTopHeights,
  buildRoofWedgeLocal,
  ceilingTopHeight,
  computeBuildingLayout,
  effectiveRoomHeight,
  generateBuildingSolids,
  openingFootprint,
  wallFootprint,
} from './solidGeneration';
import { createEmptyProject, createId, withOpeningAdded, withRoomCeilingProfileSet, withRoomNamed, withRoomStairwellVoidSet, withSketchAdded, withWallAdded } from './schema';
import type { Point2, ProjectDocument, WallSegment } from './schema';
import { getManifoldModule } from './manifoldRuntime';
import type { GeneratedMesh } from './solidGeneration';

function wall(start: Point2, end: Point2, thickness = 0.15): WallSegment {
  return { id: createId(), start, end, thickness };
}

function rectangleWalls(x0: number, y0: number, x1: number, y1: number): WallSegment[] {
  const a = { x: x0, y: y0 };
  const b = { x: x1, y: y0 };
  const c = { x: x1, y: y1 };
  const d = { x: x0, y: y1 };
  return [wall(a, b), wall(b, c), wall(c, d), wall(d, a)];
}

function shoelaceArea(points: readonly Point2[]): number {
  let sum = 0;
  for (let i = 0; i < points.length; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    sum += a.x * b.y - b.x * a.y;
  }
  return sum / 2;
}

/** Divergence-theorem mesh volume, used to sanity-check winding/closure of generated meshes without needing the WASM module. */
function meshVolume(positions: readonly [number, number, number][], triangles: readonly [number, number, number][]): number {
  const cross = (a: readonly number[], b: readonly number[]): [number, number, number] => [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
  const dot = (a: readonly number[], b: readonly number[]) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  let v = 0;
  for (const [a, b, c] of triangles) v += dot(positions[a], cross(positions[b], positions[c]));
  return v / 6;
}

describe('ceilingTopHeight', () => {
  it('is just wallHeight for a flat ceiling', () => {
    expect(ceilingTopHeight({ kind: 'flat', wallHeight: 2.4 })).toBe(2.4);
  });

  it('is the taller of eave/ridge for a vaulted ceiling', () => {
    expect(ceilingTopHeight({ kind: 'gable', wallHeight: 2.4, ridgeHeight: 3.6 })).toBe(3.6);
  });

  it('falls back to wallHeight if a vaulted ceiling has no ridgeHeight set', () => {
    expect(ceilingTopHeight({ kind: 'shed', wallHeight: 2.4 })).toBe(2.4);
  });
});

describe('assignWallTopHeights', () => {
  it("gives every wall of a single room that room's height", () => {
    const walls = rectangleWalls(0, 0, 4, 3);
    const room = { points: walls.map((w) => w.start), nominalTopHeight: 2.7 };
    const heights = assignWallTopHeights(walls, [room], 2.4);
    for (const w of walls) expect(heights.get(w.id)).toBe(2.7);
  });

  it('falls back for a wall bordering no detected room', () => {
    const dangling = wall({ x: 10, y: 10 }, { x: 12, y: 10 });
    const heights = assignWallTopHeights([dangling], [], 2.4);
    expect(heights.get(dangling.id)).toBe(2.4);
  });

  it('takes the taller side for a shared partition wall', () => {
    const shared = wall({ x: 2, y: 0 }, { x: 2, y: 3 });
    const roomA = { points: [{ x: 0, y: 0 }, { x: 2, y: 0 }, { x: 2, y: 3 }, { x: 0, y: 3 }], nominalTopHeight: 2.4 };
    const roomB = { points: [{ x: 2, y: 0 }, { x: 4, y: 0 }, { x: 4, y: 3 }, { x: 2, y: 3 }], nominalTopHeight: 3.6 };
    const heights = assignWallTopHeights([shared], [roomA, roomB], 2.4);
    expect(heights.get(shared.id)).toBe(3.6);
  });
});

describe('wallFootprint', () => {
  it('is a CCW rectangle centered on the centerline', () => {
    const footprint = wallFootprint({ start: { x: 0, y: 0 }, end: { x: 4, y: 0 }, thickness: 1 });
    expect(shoelaceArea(footprint)).toBeGreaterThan(0);
    expect(shoelaceArea(footprint)).toBeCloseTo(4, 6);
    for (const p of footprint) expect(Math.abs(p.y)).toBeCloseTo(0.5, 6);
  });
});

describe('openingFootprint', () => {
  it('is a CCW rectangle centered on the opening offset, wider than the wall', () => {
    const w = { start: { x: 0, y: 0 }, end: { x: 4, y: 0 }, thickness: 0.2 };
    const footprint = openingFootprint(w, { offset: 2, width: 0.9 });
    expect(shoelaceArea(footprint)).toBeGreaterThan(0);
    const xs = footprint.map((p) => p.x);
    expect(Math.min(...xs)).toBeCloseTo(2 - 0.45, 6);
    expect(Math.max(...xs)).toBeCloseTo(2 + 0.45, 6);
    for (const p of footprint) expect(Math.abs(p.y)).toBeGreaterThan(0.1); // wider than the 0.2 wall
  });
});

describe('buildRoofWedgeLocal', () => {
  const bounds = { minX: 0, maxX: 4, minY: 0, maxY: 10 };

  it.each(['gable', 'shed'] as const)('produces a closed, correctly-wound %s prism with the ridge along the longer axis', (kind) => {
    const { positions, triangles } = buildRoofWedgeLocal(bounds, 2, 3, kind);
    expect(positions).toHaveLength(6);
    expect(triangles).toHaveLength(8);
    // Ridge runs along Y here (the longer span), so the prism's cross-section
    // area (0.5 * base * height, base = the shorter span) times its length
    // along Y is the expected volume regardless of gable vs shed shape.
    const expectedVolume = 0.5 * (bounds.maxX - bounds.minX) * (3 - 2) * (bounds.maxY - bounds.minY);
    expect(meshVolume(positions, triangles)).toBeCloseTo(expectedVolume, 5);
  });

  it('also winds correctly when the ridge runs along X', () => {
    const wideBounds = { minX: 0, maxX: 10, minY: 0, maxY: 4 };
    const { positions, triangles } = buildRoofWedgeLocal(wideBounds, 2, 3, 'gable');
    const expectedVolume = 0.5 * (wideBounds.maxY - wideBounds.minY) * (3 - 2) * (wideBounds.maxX - wideBounds.minX);
    expect(meshVolume(positions, triangles)).toBeCloseTo(expectedVolume, 5);
  });
});

/** Builds a project with a single named 4m x 3m room and returns its room-label id alongside it (withRoomNamed doesn't hand the id back directly, and every other room setter needs it - passing labelId: null again would create a second, orphaned label instead of updating this one). */
function projectWithSingleRoom(): { project: ProjectDocument; labelId: string } {
  let project = createEmptyProject();
  const sketch = project.sketches[0];
  for (const w of rectangleWalls(0, 0, 4, 3)) project = withWallAdded(project, sketch.id, w);
  const seed = { x: 2, y: 1.5 };
  project = withRoomNamed(project, sketch.id, null, seed, 'Living Room');
  const labelId = project.sketches[0].roomLabels[0].id;
  return { project, labelId };
}

describe('computeBuildingLayout', () => {
  it('stacks a second floor on top of the first using the first floor room height', () => {
    const built = projectWithSingleRoom();
    let project = built.project;
    const groundSketch = project.sketches[0];
    project = withRoomCeilingProfileSet(project, groundSketch.id, built.labelId, { x: 2, y: 1.5 }, { kind: 'flat', wallHeight: 2.5 });

    const added = withSketchAdded(project, 'Upper Floor', 1, 'floorPlan');
    project = added.project;
    for (const w of rectangleWalls(0, 0, 4, 3)) project = withWallAdded(project, added.sketchId, w);

    const layout = computeBuildingLayout(project);
    expect(layout.floors).toHaveLength(2);
    expect(layout.floors[0].baseZ).toBe(0);
    expect(layout.floors[0].ownHeight).toBe(2.5);
    expect(layout.floors[1].baseZ).toBe(2.5);
    expect(layout.topZ).toBeCloseTo(2.5 + layout.floors[1].ownHeight, 6);
  });

  it('extends a stairwell void room to the building top for effectiveRoomHeight without changing its own ceiling height used for stacking', () => {
    const built = projectWithSingleRoom();
    let project = built.project;
    const sketch = project.sketches[0];
    project = withRoomStairwellVoidSet(project, sketch.id, built.labelId, { x: 2, y: 1.5 }, true);

    const layout = computeBuildingLayout(project);
    const floor = layout.floors[0];
    expect(floor.rooms[0].stairwellVoid).toBe(true);
    // No other rooms on this (only) floor, so stacking falls back to the default rather than 0.
    expect(floor.ownHeight).toBeGreaterThan(0);
    expect(effectiveRoomHeight(floor.rooms[0], floor.baseZ, layout.topZ)).toBeCloseTo(layout.topZ - floor.baseZ, 6);
  });
});

describe('generateBuildingSolids', () => {
  it('produces a watertight air-volume and wall mesh for a single flat-roofed room with a door', async () => {
    const built = projectWithSingleRoom();
    let project = built.project;
    const sketch = project.sketches[0];
    project = withRoomCeilingProfileSet(project, sketch.id, built.labelId, { x: 2, y: 1.5 }, { kind: 'flat', wallHeight: 2.5 });
    const doorWall = sketch.walls[0];
    project = withOpeningAdded(project, sketch.id, { id: createId(), wallId: doorWall.id, offset: 1, width: 0.9, headHeight: 2.0, kind: 'door' });

    const result = await generateBuildingSolids(project);
    expect(result.warnings).toEqual([]);
    expect(result.floors).toHaveLength(1);

    const floor = result.floors[0];
    expect(floor.airVolume).not.toBeNull();
    expect(floor.walls).not.toBeNull();

    const air = floor.airVolume!;
    expect(air.indices.length % 3).toBe(0);
    expect(air.positions.length % 3).toBe(0);
    const triangles: [number, number, number][] = [];
    for (let i = 0; i < air.indices.length; i += 3) triangles.push([air.indices[i], air.indices[i + 1], air.indices[i + 2]]);
    const positions: [number, number, number][] = [];
    for (let i = 0; i < air.positions.length; i += 3) positions.push([air.positions[i], air.positions[i + 1], air.positions[i + 2]]);
    // 4m x 3m x 2.5m room -> 30 m^3, extruded straight from the centerline polygon (see
    // generateBuildingSolids doc comment) - the door only cuts the wall mesh, it doesn't
    // add to the air volume (see the next test).
    expect(Math.abs(meshVolume(positions, triangles))).toBeCloseTo(30, 1);

    const walls = floor.walls!;
    expect(walls.indices.length % 3).toBe(0);
    expect(walls.positions.length).toBeGreaterThan(0);
  }, 20000);

  it('cuts the door out of the wall solid without changing the air volume', async () => {
    const built = projectWithSingleRoom();
    let project = built.project;
    const sketch = project.sketches[0];
    project = withRoomCeilingProfileSet(project, sketch.id, built.labelId, { x: 2, y: 1.5 }, { kind: 'flat', wallHeight: 2.5 });
    const doorWall = sketch.walls[0];

    const wasm = await getManifoldModule();
    const solidVolume = (mesh: GeneratedMesh): number =>
      wasm.Manifold.ofMesh(new wasm.Mesh({ numProp: 3, vertProperties: mesh.positions, triVerts: mesh.indices })).volume();

    const withoutDoor = await generateBuildingSolids(project);
    const wallVolumeWithoutDoor = solidVolume(withoutDoor.floors[0].walls!);
    const airVolumeWithoutDoor = solidVolume(withoutDoor.floors[0].airVolume!);

    project = withOpeningAdded(project, sketch.id, { id: createId(), wallId: doorWall.id, offset: 1, width: 0.9, headHeight: 2.0, kind: 'door' });
    const withDoor = await generateBuildingSolids(project);
    const wallVolumeWithDoor = solidVolume(withDoor.floors[0].walls!);
    const airVolumeWithDoor = solidVolume(withDoor.floors[0].airVolume!);

    // The door removes a 0.9m x 2.0m hole through the wall's own 0.15m thickness (the cutting
    // box itself is padded wider than that - see OPENING_CLEARANCE - but the wall solid it's
    // subtracted from doesn't extend past its own thickness, so that's all that comes out).
    expect(wallVolumeWithoutDoor - wallVolumeWithDoor).toBeCloseTo(0.9 * 0.15 * 2.0, 2);
    // ...but rooms are built from the centerline polygon regardless of doors (see
    // generateBuildingSolids doc comment), so the air volume is untouched by it.
    expect(airVolumeWithDoor).toBeCloseTo(airVolumeWithoutDoor, 5);
  }, 20000);
});
