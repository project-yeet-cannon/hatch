import { cleanupManifoldObjects, getManifoldModule } from './manifoldRuntime';
import { computeBuildingLayout, effectiveRoomHeight, openingFootprint, wallFootprint, type FloorLayout, type RoomLayout } from './solidGeneration';
import type { ProjectDocument } from './schema';
import type { ManifoldToplevel } from 'manifold-3d';

/**
 * Named boundary-patch groups on the exported air-volume surface (CFD
 * meshers like OpenFOAM assign boundary conditions per named patch, e.g.
 * "walls", "door_kitchen_hall" - see the module doc comment below for why
 * doors don't get their own patch here).
 */
export const PATCH_WALLS = 'walls';
export const PATCH_FLOOR = 'floor';
export const PATCH_CEILING = 'ceiling';

export interface PatchedMesh {
  /** xyz per vertex. */
  positions: Float32Array;
  indices: Uint32Array;
  /** One patch name per triangle (length === indices.length / 3). */
  triPatch: string[];
}

export interface ExportAirVolume {
  mesh: PatchedMesh | null;
  warnings: string[];
}

type ManifoldSolid = InstanceType<ManifoldToplevel['Manifold']>;

/**
 * The single watertight solid CFD actually wants (interior-airflow CFD
 * consumes the air volume, not the architectural walls - see the README's
 * CFD research section): unlike the viewer's per-floor air volume in
 * solidGeneration.ts (extruded straight from room centerlines, so adjacent
 * rooms touch/merge at the shared wall with no separation at all), this is
 * real negative space - rooms minus the wall material between them, with
 * each opening's own footprint unioned back in as the only path connecting
 * adjacent rooms. A closed door isn't modeled (the schema has no open/closed
 * state - see schema.ts Opening), so every opening bridges its two rooms
 * with fully open air; that's also why there's no per-door boundary patch in
 * the output (an open passage has no surface to name - it reads as
 * continuous interior air, exactly like a real doorway with no door in it).
 * Adding a closed-door state later would turn that bridge back into a
 * boundary face at the threshold, which is when a `door_<name>` patch would
 * start to mean something.
 *
 * Patches: `walls` comes from Manifold's originalID tracking (every wall
 * extrusion's id is recorded before it's used, and that id survives through
 * arbitrarily deep union/difference chains onto whichever result triangles
 * it produced - see roomSolid's doc comment in solidGeneration.ts for why the
 * id has to be captured pre-transform). `floor`/`ceiling` are whatever's left
 * (room-sourced material), split by triangle normal - mostly-downward faces
 * are a floor, mostly-upward faces are a ceiling/roof; the rare leftover
 * near-vertical face (a modeling gap with no bounding wall) falls back to
 * `walls` and adds a warning, since that's a real boundary a mesher still
 * needs a name for.
 */
export async function computeExportAirVolume(project: ProjectDocument): Promise<ExportAirVolume> {
  const layout = computeBuildingLayout(project);
  const wasm = await getManifoldModule();
  const warnings: string[] = [];
  const wallOriginalIds = new Set<number>();
  const roomOriginalIds = new Set<number>();

  try {
    const floorSolids: ManifoldSolid[] = [];

    for (const floor of layout.floors) {
      const solid = buildFloorAirVolume(wasm, floor, layout.topZ, wallOriginalIds, roomOriginalIds, warnings);
      if (solid) floorSolids.push(floor.baseZ !== 0 ? solid.translate(0, 0, floor.baseZ) : solid);
    }

    if (floorSolids.length === 0) return { mesh: null, warnings };

    const building = floorSolids.length > 1 ? wasm.Manifold.union(floorSolids) : floorSolids[0];
    if (building.status() !== 'NoError') warnings.push(`Whole-building air volume reported ${building.status()}.`);

    const mesh = building.getMesh();
    const patched = tagPatches(mesh, wallOriginalIds, roomOriginalIds, warnings);
    return { mesh: patched, warnings };
  } finally {
    cleanupManifoldObjects();
  }
}

function buildFloorAirVolume(
  wasm: ManifoldToplevel,
  floor: FloorLayout,
  buildingTopZ: number,
  wallOriginalIds: Set<number>,
  roomOriginalIds: Set<number>,
  warnings: string[],
): ManifoldSolid | null {
  const roomSolids: ManifoldSolid[] = floor.rooms.map((room) => trackedRoomSolid(wasm, room, floor.baseZ, buildingTopZ, roomOriginalIds));
  if (roomSolids.length === 0) return null;
  const roomsUnion = roomSolids.length > 1 ? wasm.Manifold.union(roomSolids) : roomSolids[0];

  const wallSolids: ManifoldSolid[] = floor.walls.map((wall) => {
    const raw = wasm.Manifold.extrude(
      wallFootprint(wall).map((p): [number, number] => [p.x, p.y]),
      wall.topHeight,
    );
    wallOriginalIds.add(raw.originalID());
    return raw;
  });

  const bridgeSolids: ManifoldSolid[] = [];
  const wallById = new Map(floor.walls.map((w) => [w.id, w]));
  for (const opening of floor.openings) {
    const wall = wallById.get(opening.wallId);
    if (!wall) continue;
    const headHeight = Math.min(opening.headHeight, wall.topHeight);
    const raw = wasm.Manifold.extrude(
      openingFootprint(wall, opening).map((p): [number, number] => [p.x, p.y]),
      headHeight,
    );
    roomOriginalIds.add(raw.originalID());
    bridgeSolids.push(raw);
  }

  if (wallSolids.length === 0) return roomsUnion;

  const wallsUnion = wallSolids.length > 1 ? wasm.Manifold.union(wallSolids) : wallSolids[0];
  const carved = wasm.Manifold.difference(roomsUnion, wallsUnion);
  if (carved.status() !== 'NoError') warnings.push(`Floor "${floor.name}": carved air volume reported ${carved.status()}.`);

  const withBridges = bridgeSolids.length > 0 ? wasm.Manifold.union([carved, ...bridgeSolids]) : carved;
  return withBridges;
}

function trackedRoomSolid(wasm: ManifoldToplevel, room: RoomLayout, floorBaseZ: number, buildingTopZ: number, roomOriginalIds: Set<number>): ManifoldSolid {
  const polygon = room.points.map((p): [number, number] => [p.x, p.y]);
  const height = effectiveRoomHeight(room, floorBaseZ, buildingTopZ);
  // The export air volume doesn't model vaulted-ceiling wedges separately -
  // it extrudes straight to each room's nominal top height (eave or ridge,
  // whichever is taller). That slightly over-includes the corners under a
  // sloped ceiling as air, which is conservative (more air, not less) and
  // avoids re-deriving the wedge-clip CSG a second time for this pass; worth
  // refining once Step 7's real mesher run shows whether it matters.
  const raw = wasm.Manifold.extrude(polygon, height);
  roomOriginalIds.add(raw.originalID());
  return raw;
}

interface Mesh3 {
  numProp: number;
  vertProperties: Float32Array;
  triVerts: Uint32Array;
  runIndex: Uint32Array;
  runOriginalID: Uint32Array;
}

function triangleNormalZ(positions: Float32Array, numProp: number, ia: number, ib: number, ic: number): number {
  const ax = positions[ia * numProp];
  const ay = positions[ia * numProp + 1];
  const az = positions[ia * numProp + 2];
  const bx = positions[ib * numProp];
  const by = positions[ib * numProp + 1];
  const bz = positions[ib * numProp + 2];
  const cx = positions[ic * numProp];
  const cy = positions[ic * numProp + 1];
  const cz = positions[ic * numProp + 2];
  const ux = bx - ax;
  const uy = by - ay;
  const uz = bz - az;
  const vx = cx - ax;
  const vy = cy - ay;
  const vz = cz - az;
  const nx = uy * vz - uz * vy;
  const ny = uz * vx - ux * vz;
  const nz = ux * vy - uy * vx;
  const len = Math.hypot(nx, ny, nz) || 1;
  return nz / len;
}

/** Faces within this many degrees of horizontal count as floor/ceiling; steeper faces fall back to `walls`. */
const HORIZONTAL_NORMAL_THRESHOLD = Math.cos((45 * Math.PI) / 180);

function tagPatches(mesh: Mesh3, wallOriginalIds: Set<number>, roomOriginalIds: Set<number>, warnings: string[]): PatchedMesh {
  const numProp = mesh.numProp;
  const positions =
    numProp === 3
      ? mesh.vertProperties
      : (() => {
          const numVert = mesh.vertProperties.length / numProp;
          const out = new Float32Array(numVert * 3);
          for (let v = 0; v < numVert; v++) {
            out[v * 3] = mesh.vertProperties[v * numProp];
            out[v * 3 + 1] = mesh.vertProperties[v * numProp + 1];
            out[v * 3 + 2] = mesh.vertProperties[v * numProp + 2];
          }
          return out;
        })();

  const numTri = mesh.triVerts.length / 3;
  const triPatch = new Array<string>(numTri);
  let unresolvedCount = 0;

  for (let run = 0; run < mesh.runOriginalID.length; run++) {
    const id = mesh.runOriginalID[run];
    const triStart = mesh.runIndex[run] / 3;
    const triEnd = mesh.runIndex[run + 1] / 3;
    const isWall = wallOriginalIds.has(id);
    const isRoom = roomOriginalIds.has(id);

    for (let tri = triStart; tri < triEnd; tri++) {
      if (isWall) {
        triPatch[tri] = PATCH_WALLS;
        continue;
      }
      if (!isRoom) unresolvedCount++;

      const ia = mesh.triVerts[tri * 3];
      const ib = mesh.triVerts[tri * 3 + 1];
      const ic = mesh.triVerts[tri * 3 + 2];
      const nz = triangleNormalZ(positions, 3, ia, ib, ic);
      if (nz <= -HORIZONTAL_NORMAL_THRESHOLD) triPatch[tri] = PATCH_FLOOR;
      else if (nz >= HORIZONTAL_NORMAL_THRESHOLD) triPatch[tri] = PATCH_CEILING;
      else triPatch[tri] = PATCH_WALLS;
    }
  }

  if (unresolvedCount > 0) {
    warnings.push(`${unresolvedCount} triangle(s) in the exported air volume couldn't be traced to a room or wall (classified by surface orientation instead).`);
  }

  return { positions, indices: mesh.triVerts, triPatch };
}
