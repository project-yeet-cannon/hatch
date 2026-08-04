import { resolveCeilingProfile } from './elevation';
import { detectRooms, matchRoomLabel } from './roomDetection';
import { DEFAULT_CEILING_PROFILE, floorLabel } from './schema';
import type { CeilingProfileKind, OpeningKind, Point2, ProjectDocument, WallSegment } from './schema';
import { solveSketch } from './solver';
import { cleanupManifoldObjects, getManifoldModule } from './manifoldRuntime';
import type { ManifoldToplevel } from 'manifold-3d';

// --- Pure layout computation (no WASM - unit-testable in isolation) -------

export interface RoomLayout {
  /** The matched RoomLabel's id, or a synthesized `floor{N}-room{i}` for an unnamed room (stable only within one computeBuildingLayout call - see exportGeometry.ts/topology.ts, the two consumers that need room identity). */
  id: string;
  points: Point2[];
  area: number;
  name: string;
  kind: CeilingProfileKind;
  /** Meters above this room's floor, to the eave line (flat ceilings only have this height). */
  eaveHeight: number;
  /** Meters above this room's floor, to the ridge; only present for shed/gable. */
  ridgeHeight: number | undefined;
  /** ceilingTopHeight(profile): the highest point of this room's own ceiling. */
  nominalTopHeight: number;
  stairwellVoid: boolean;
}

export interface WallLayout {
  id: string;
  start: Point2;
  end: Point2;
  thickness: number;
  /** Resolved from the tallest room bordering this wall (see assignWallTopHeights). */
  topHeight: number;
}

export interface OpeningLayout {
  id: string;
  wallId: string;
  offset: number;
  width: number;
  headHeight: number;
  kind: OpeningKind;
}

export interface FloorLayout {
  floorIndex: number;
  name: string;
  baseZ: number;
  /** This floor's own ceiling height (max non-void room), used to stack the floor above. */
  ownHeight: number;
  rooms: RoomLayout[];
  walls: WallLayout[];
  openings: OpeningLayout[];
}

export interface BuildingLayout {
  floors: FloorLayout[];
  /** baseZ + ownHeight of the topmost floor - stairwell voids extrude up to this. */
  topZ: number;
}

const FALLBACK_WALL_TOP_HEIGHT = DEFAULT_CEILING_PROFILE.wallHeight;
const VERTEX_EPSILON = 1e-4;

/** The highest point of a ceiling: wallHeight for flat, otherwise the taller of the eave/ridge. */
export function ceilingTopHeight(profile: { kind: CeilingProfileKind; wallHeight: number; ridgeHeight?: number }): number {
  if (profile.kind === 'flat') return profile.wallHeight;
  return Math.max(profile.wallHeight, profile.ridgeHeight ?? profile.wallHeight);
}

function vertexKey(p: Point2): string {
  return `${Math.round(p.x / VERTEX_EPSILON)}:${Math.round(p.y / VERTEX_EPSILON)}`;
}

function edgeKey(a: Point2, b: Point2): string {
  const ka = vertexKey(a);
  const kb = vertexKey(b);
  return ka < kb ? `${ka}|${kb}` : `${kb}|${ka}`;
}

/**
 * Resolves each wall's top height from the room(s) it borders: the taller of
 * any adjacent room's own ceiling (a shared partition wall rises to match
 * the taller side). Walls bordering no detected room (dangling stubs, or an
 * exterior side with nothing enclosed yet) fall back to `fallback`. Rooms
 * marked stairwellVoid still contribute their *nominal* height here - the
 * void's extra height is an air-volume-only effect (see effectiveRoomHeight),
 * not something that grows its bounding walls.
 */
export function assignWallTopHeights(walls: readonly WallSegment[], rooms: readonly { points: readonly Point2[]; nominalTopHeight: number }[], fallback: number): Map<string, number> {
  const wallByEdge = new Map<string, string>();
  for (const wall of walls) wallByEdge.set(edgeKey(wall.start, wall.end), wall.id);

  const heights = new Map<string, number>();
  for (const room of rooms) {
    for (let i = 0; i < room.points.length; i++) {
      const a = room.points[i];
      const b = room.points[(i + 1) % room.points.length];
      const wallId = wallByEdge.get(edgeKey(a, b));
      if (!wallId) continue;
      const existing = heights.get(wallId);
      if (existing === undefined || room.nominalTopHeight > existing) heights.set(wallId, room.nominalTopHeight);
    }
  }
  for (const wall of walls) if (!heights.has(wall.id)) heights.set(wall.id, fallback);
  return heights;
}

/**
 * Builds the per-floor layout (solved rooms/walls/openings, resolved ceiling
 * and wall heights, and floor-to-floor Z stacking) that 3D generation
 * consumes. Floors are stacked directly on top of each other by floorIndex,
 * each one's height taken from its tallest non-void room (there's no
 * separate floor-slab thickness in this model yet - see TODO_MODELING.md).
 * All floorPlan sketches sharing a floorIndex are combined as-drawn (in the
 * "shared origin" they're already meant to share - see Sketch.floorIndex);
 * unmerged partial sketches just produce disconnected wall-graph components,
 * which detectRooms already handles.
 */
export function computeBuildingLayout(project: ProjectDocument): BuildingLayout {
  const floorPlans = project.sketches.filter((s) => s.kind === 'floorPlan');
  const floorIndexes = [...new Set(floorPlans.map((s) => s.floorIndex))].sort((a, b) => a - b);

  const floors: FloorLayout[] = [];
  let baseZ = 0;
  for (const floorIndex of floorIndexes) {
    const sketches = floorPlans.filter((s) => s.floorIndex === floorIndex);
    const walls = sketches.flatMap((sketch) => solveSketch(sketch.walls).walls);
    const roomLabels = sketches.flatMap((s) => s.roomLabels);
    const openings: OpeningLayout[] = sketches.flatMap((s) =>
      s.openings.map((o) => ({ id: o.id, wallId: o.wallId, offset: o.offset, width: o.width, headHeight: o.headHeight, kind: o.kind })),
    );

    const rooms: RoomLayout[] = detectRooms(walls).map((room, roomIndex) => {
      const label = matchRoomLabel(roomLabels, room);
      const profile = resolveCeilingProfile(project, label?.id ?? '__none__');
      return {
        id: label?.id ?? `floor${floorIndex}-room${roomIndex}`,
        points: room.points,
        area: room.area,
        name: label?.name || 'Room',
        kind: profile.kind,
        eaveHeight: profile.wallHeight,
        ridgeHeight: profile.kind === 'flat' ? undefined : (profile.ridgeHeight ?? profile.wallHeight),
        nominalTopHeight: ceilingTopHeight(profile),
        stairwellVoid: label?.stairwellVoid ?? false,
      };
    });

    const nonVoidHeights = rooms.filter((r) => !r.stairwellVoid).map((r) => r.nominalTopHeight);
    const ownHeight = nonVoidHeights.length > 0 ? Math.max(...nonVoidHeights) : FALLBACK_WALL_TOP_HEIGHT;

    const wallTopHeights = assignWallTopHeights(walls, rooms, FALLBACK_WALL_TOP_HEIGHT);
    const wallLayouts: WallLayout[] = walls.map((wall) => ({
      id: wall.id,
      start: wall.start,
      end: wall.end,
      thickness: wall.thickness,
      topHeight: wallTopHeights.get(wall.id) ?? FALLBACK_WALL_TOP_HEIGHT,
    }));

    floors.push({
      floorIndex,
      name: sketches.map((s) => s.name).join(' + ') || floorLabel(floorIndex),
      baseZ,
      ownHeight,
      rooms,
      walls: wallLayouts,
      openings,
    });

    baseZ += ownHeight;
  }

  return { floors, topZ: baseZ };
}

/** A stairwellVoid room's air volume ignores its own ceiling and rises to the roofline, reading as an open shaft; other rooms just use their own ceiling. */
export function effectiveRoomHeight(room: Pick<RoomLayout, 'nominalTopHeight' | 'stairwellVoid'>, floorBaseZ: number, buildingTopZ: number): number {
  return room.stairwellVoid ? buildingTopZ - floorBaseZ : room.nominalTopHeight;
}

function boundsOfPoints(points: readonly Point2[]): { minX: number; maxX: number; minY: number; maxY: number } {
  const xs = points.map((p) => p.x);
  const ys = points.map((p) => p.y);
  return { minX: Math.min(...xs), maxX: Math.max(...xs), minY: Math.min(...ys), maxY: Math.max(...ys) };
}

/** The wall centerline offset by its thickness into a 4-point rectangle, wound CCW (verified via the shoelace formula - Manifold's extrude expects CCW-outer polygons). */
export function wallFootprint(wall: { start: Point2; end: Point2; thickness: number }): [Point2, Point2, Point2, Point2] {
  const dx = wall.end.x - wall.start.x;
  const dy = wall.end.y - wall.start.y;
  const len = Math.hypot(dx, dy);
  const half = wall.thickness / 2;
  if (len < 1e-9) {
    return [
      { x: wall.start.x - half, y: wall.start.y - half },
      { x: wall.start.x + half, y: wall.start.y - half },
      { x: wall.start.x + half, y: wall.start.y + half },
      { x: wall.start.x - half, y: wall.start.y + half },
    ];
  }
  const nx = (-dy / len) * half;
  const ny = (dx / len) * half;
  return [
    { x: wall.start.x - nx, y: wall.start.y - ny },
    { x: wall.end.x - nx, y: wall.end.y - ny },
    { x: wall.end.x + nx, y: wall.end.y + ny },
    { x: wall.start.x + nx, y: wall.start.y + ny },
  ];
}

/** A little wider than the wall's own thickness so subtracting it fully punches through both faces instead of leaving a hairline sliver at floating-point precision. */
const OPENING_CLEARANCE = 0.05;

/** A door/archway's cutting box footprint, wound CCW like wallFootprint. */
export function openingFootprint(wall: { start: Point2; end: Point2; thickness: number }, opening: { offset: number; width: number }): [Point2, Point2, Point2, Point2] {
  const dx = wall.end.x - wall.start.x;
  const dy = wall.end.y - wall.start.y;
  const len = Math.hypot(dx, dy) || 1;
  const dirX = dx / len;
  const dirY = dy / len;
  const nx = -dirY;
  const ny = dirX;
  const halfWidth = Math.min(opening.width / 2, len / 2);
  const halfThickness = wall.thickness / 2 + OPENING_CLEARANCE;
  const cx = wall.start.x + dirX * opening.offset;
  const cy = wall.start.y + dirY * opening.offset;
  const at = (along: number, across: number): Point2 => ({ x: cx + dirX * along + nx * across, y: cy + dirY * along + ny * across });
  return [at(-halfWidth, -halfThickness), at(halfWidth, -halfThickness), at(halfWidth, halfThickness), at(-halfWidth, halfThickness)];
}

export interface RoofWedgeMesh {
  /** Local (u, v, w) mapped onto world (x, y, z); z is height above the room's floor. */
  positions: [number, number, number][];
  triangles: [number, number, number][];
}

/**
 * A vaulted (shed/gable) ceiling, approximated as a triangular-prism "wedge"
 * spanning the room's axis-aligned bounding box, ridge running along the
 * box's longer dimension (gable: peak centered; shed: full-height edge at
 * one side, by convention - the schema doesn't yet record shed orientation).
 * Callers clip this to the room's actual polygon via a boolean intersection,
 * since real rooms aren't always rectangular; the bbox just needs to fully
 * contain the polygon, which it always does by definition.
 *
 * Winding: the (span, z) cross-section below is CCW, which is a
 * positive-volume prism in the local (u, v, w) frame. Mapping to world (x,
 * y, z) swaps u/v when the ridge runs along X, which mirrors the frame and
 * flips orientation, so the triangle winding is flipped to compensate.
 * Verified against the divergence-theorem mesh-volume formula
 * (V = 1/6 * sum(p0 . (p1 x p2))) for both branches and both roof kinds.
 */
export function buildRoofWedgeLocal(
  bounds: { minX: number; maxX: number; minY: number; maxY: number },
  eaveHeight: number,
  ridgeHeight: number,
  kind: 'shed' | 'gable',
): RoofWedgeMesh {
  const ridgeAlongX = bounds.maxX - bounds.minX >= bounds.maxY - bounds.minY;
  const spanMin = ridgeAlongX ? bounds.minY : bounds.minX;
  const spanMax = ridgeAlongX ? bounds.maxY : bounds.maxX;
  const vStart = ridgeAlongX ? bounds.minX : bounds.minY;
  const vEnd = ridgeAlongX ? bounds.maxX : bounds.maxY;

  const peakSpan = kind === 'gable' ? (spanMin + spanMax) / 2 : spanMax;
  const crossSection: [number, number][] = [
    [spanMin, eaveHeight],
    [spanMax, eaveHeight],
    [peakSpan, ridgeHeight],
  ];

  const local: [number, number, number][] = [];
  for (const v of [vStart, vEnd]) for (const [u, w] of crossSection) local.push([u, v, w]);
  const positions: [number, number, number][] = local.map(([u, v, w]) => (ridgeAlongX ? [v, u, w] : [u, v, w]));

  const baseTriangles: [number, number, number][] = [
    [0, 1, 2],
    [3, 5, 4],
    [0, 3, 4],
    [0, 4, 1],
    [1, 4, 5],
    [1, 5, 2],
    [2, 5, 3],
    [2, 3, 0],
  ];
  const triangles: [number, number, number][] = baseTriangles.map(([a, b, c]) => (ridgeAlongX ? [a, c, b] : [a, b, c]));

  return { positions, triangles };
}

// --- WASM-backed mesh generation ------------------------------------------

export interface GeneratedMesh {
  /** xyz per vertex. */
  positions: Float32Array;
  indices: Uint32Array;
}

export interface FloorSolids {
  floorIndex: number;
  name: string;
  baseZ: number;
  topZ: number;
  airVolume: GeneratedMesh | null;
  walls: GeneratedMesh | null;
}

export interface BuildingSolids {
  floors: FloorSolids[];
  warnings: string[];
}

type ManifoldSolid = InstanceType<ManifoldToplevel['Manifold']>;

/**
 * Converts a Manifold's mesh to plain typed arrays. Exported for
 * exportGeometry.ts, which needs the same conversion for its own
 * whole-building solid (a separate CSG pass from the per-floor viewer solids
 * built below - see that module for why).
 */
export function meshFromManifold(manifold: ManifoldSolid): GeneratedMesh {
  const mesh = manifold.getMesh();
  const numProp = mesh.numProp;
  if (numProp === 3) return { positions: mesh.vertProperties, indices: mesh.triVerts };

  const positions = new Float32Array(mesh.numVert * 3);
  for (let v = 0; v < mesh.numVert; v++) {
    positions[v * 3] = mesh.vertProperties[v * numProp];
    positions[v * 3 + 1] = mesh.vertProperties[v * numProp + 1];
    positions[v * 3 + 2] = mesh.vertProperties[v * numProp + 2];
  }
  return { positions, indices: mesh.triVerts };
}

/**
 * Builds one room's solid. `trackOriginal`, if given, is called with the
 * Manifold `originalID()` of every leaf primitive as it's created (before
 * any transform is applied to it - `.originalID()` only reports a real id on
 * an untransformed, unbooleaned Manifold, but that id remains recoverable
 * from a *result* mesh's `runOriginalID` no matter how much CSG happens to it
 * afterward). exportGeometry.ts uses this to tag which boundary faces of the
 * final whole-building solid came from room material vs. wall material.
 */
function roomSolid(wasm: ManifoldToplevel, room: RoomLayout, height: number, trackOriginal?: (id: number) => void): ManifoldSolid {
  const polygon = room.points.map((p): [number, number] => [p.x, p.y]);
  if (room.kind === 'flat' || room.stairwellVoid) {
    const solid = wasm.Manifold.extrude(polygon, height);
    trackOriginal?.(solid.originalID());
    return solid;
  }

  const ridgeHeight = room.ridgeHeight ?? room.eaveHeight;
  const flatPart = wasm.Manifold.extrude(polygon, room.eaveHeight);
  trackOriginal?.(flatPart.originalID());
  const wedge = buildRoofWedgeLocal(boundsOfPoints(room.points), room.eaveHeight, ridgeHeight, room.kind);
  const wedgeManifold = wasm.Manifold.ofMesh(
    new wasm.Mesh({
      numProp: 3,
      vertProperties: Float32Array.from(wedge.positions.flat()),
      triVerts: Uint32Array.from(wedge.triangles.flat()),
    }),
  );
  trackOriginal?.(wedgeManifold.originalID());
  const clipPrismRaw = wasm.Manifold.extrude(polygon, Math.max(ridgeHeight - room.eaveHeight, 0.01));
  trackOriginal?.(clipPrismRaw.originalID());
  const clipPrism = clipPrismRaw.translate(0, 0, room.eaveHeight);
  const roofPart = wasm.Manifold.intersection(wedgeManifold, clipPrism);
  return wasm.Manifold.union(flatPart, roofPart);
}

/**
 * Builds the air-volume and wall solids for every floor via manifold-3d CSG,
 * from whatever is currently drawn/measured (spec #7: "best knowledge at any
 * time" - floors with no closed rooms yet still get a walls mesh, and floors
 * with no walls at all just come back with everything null). Loads the WASM
 * module on first call; subsequent calls reuse it.
 *
 * Room air volumes are extruded straight from each room's centerline polygon
 * (see roomDetection.ts) rather than offset in by half the bounding walls'
 * thickness, so adjacent rooms already touch/merge at their shared wall
 * regardless of doors - reconciling that into true watertight negative-space
 * geometry (at which point door volumes become the only path connecting
 * rooms, and are worth unioning in) is Step 6/7's job, not this viewer.
 * Openings only cut the wall solid here, so doors/archways read visually as
 * gaps in the walls.
 */
export async function generateBuildingSolids(project: ProjectDocument): Promise<BuildingSolids> {
  const layout = computeBuildingLayout(project);
  const wasm = await getManifoldModule();
  const warnings: string[] = [];
  const floors: FloorSolids[] = [];

  try {
    for (const floor of layout.floors) {
      const roomSolids: ManifoldSolid[] = [];
      for (const room of floor.rooms) {
        const height = effectiveRoomHeight(room, floor.baseZ, layout.topZ);
        const solid = roomSolid(wasm, room, height);
        if (solid.status() !== 'NoError') warnings.push(`Floor "${floor.name}", room "${room.name}": solid generation reported ${solid.status()}.`);
        roomSolids.push(solid);
      }
      const airVolume = roomSolids.length > 0 ? wasm.Manifold.union(roomSolids) : null;
      if (airVolume && airVolume.status() !== 'NoError') warnings.push(`Floor "${floor.name}": air volume reported ${airVolume.status()}.`);

      const wallSolids = floor.walls.map((wall) =>
        wasm.Manifold.extrude(
          wallFootprint(wall).map((p): [number, number] => [p.x, p.y]),
          wall.topHeight,
        ),
      );
      let walls = wallSolids.length > 0 ? wasm.Manifold.union(wallSolids) : null;
      if (walls) {
        const wallById = new Map(floor.walls.map((w) => [w.id, w]));
        const cuts: ManifoldSolid[] = [];
        for (const opening of floor.openings) {
          const wall = wallById.get(opening.wallId);
          if (!wall) continue;
          const headHeight = Math.min(opening.headHeight, wall.topHeight);
          cuts.push(
            wasm.Manifold.extrude(
              openingFootprint(wall, opening).map((p): [number, number] => [p.x, p.y]),
              headHeight,
            ),
          );
        }
        if (cuts.length > 0) walls = wasm.Manifold.difference([walls, ...cuts]);
        if (walls.status() !== 'NoError') warnings.push(`Floor "${floor.name}": wall solid reported ${walls.status()}.`);
      }

      floors.push({
        floorIndex: floor.floorIndex,
        name: floor.name,
        baseZ: floor.baseZ,
        topZ: floor.baseZ + floor.ownHeight,
        airVolume: airVolume ? meshFromManifold(floor.baseZ !== 0 ? airVolume.translate(0, 0, floor.baseZ) : airVolume) : null,
        walls: walls ? meshFromManifold(floor.baseZ !== 0 ? walls.translate(0, 0, floor.baseZ) : walls) : null,
      });
    }
  } finally {
    cleanupManifoldObjects();
  }

  return { floors, warnings };
}
