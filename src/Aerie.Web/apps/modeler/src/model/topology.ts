import { computeBuildingLayout, effectiveRoomHeight, type FloorLayout } from './solidGeneration';
import type { CeilingProfileKind, OpeningKind, Point2, ProjectDocument } from './schema';

// Matches the rounding epsilon solidGeneration.ts's own edgeKey/vertexKey use
// (assignWallTopHeights) - wall/room points come from the same solved graph,
// but rounding guards against float drift the same way there.
const VERTEX_EPSILON = 1e-4;

function vertexKey(p: Point2): string {
  return `${Math.round(p.x / VERTEX_EPSILON)}:${Math.round(p.y / VERTEX_EPSILON)}`;
}

function edgeKey(a: Point2, b: Point2): string {
  const ka = vertexKey(a);
  const kb = vertexKey(b);
  return ka < kb ? `${ka}|${kb}` : `${kb}|${ka}`;
}

export interface TopologyRoomNode {
  id: string;
  name: string;
  floorIndex: number;
  /** Square meters. */
  floorArea: number;
  /** Cubic meters - a room-scale estimate from the solved footprint and ceiling profile (flat: area x height exactly; vaulted: area x height plus a triangular-prism approximation of the roof rise, not exact for non-rectangular footprints). The exported mesh geometry (STL/OBJ/GLB) is the authoritative figure if precision matters. */
  volume: number;
  ceilingProfile: { kind: CeilingProfileKind; eaveHeight: number; ridgeHeight?: number };
  stairwellVoid: boolean;
}

export interface TopologyOpeningEdge {
  id: string;
  kind: OpeningKind;
  floorIndex: number;
  /** Room ids on either side of this opening's wall (see roomsByWallId below); null means exterior or an as-yet-unenclosed side. */
  roomA: string | null;
  roomB: string | null;
  /** Square meters: width x (headHeight - sillHeight). */
  freeArea: number;
  /** Meters above the floor. Always 0 for doors/archways - the schema has no sill data yet (windows, which would have one, are deferred - see TODO_MODELING.md). */
  sillHeight: number;
  headHeight: number;
}

export const TOPOLOGY_SCHEMA_VERSION = 1;

export interface TopologyDocument {
  schemaVersion: typeof TOPOLOGY_SCHEMA_VERSION;
  projectName: string;
  generatedAt: string;
  units: 'm';
  rooms: TopologyRoomNode[];
  openings: TopologyOpeningEdge[];
  /** Reserved for the later sensor-placement feature (see TODO_MODELING.md's clarified decisions) - always empty for now. */
  sensors: never[];
}

function approxRoomVolume(room: FloorLayout['rooms'][number], height: number): number {
  if (room.kind === 'flat' || room.stairwellVoid) return room.area * height;
  const ridge = room.ridgeHeight ?? room.eaveHeight;
  return room.area * room.eaveHeight + (room.area * (ridge - room.eaveHeight)) / 2;
}

/** Every room (by id) bordering each wall, so an opening on that wall can be reported as connecting them. Unlike assignWallTopHeights (which keeps only the taller room), an opening needs both sides. */
function roomsByWallId(floor: FloorLayout): Map<string, string[]> {
  const byEdge = new Map<string, string>();
  for (const wall of floor.walls) byEdge.set(edgeKey(wall.start, wall.end), wall.id);

  const result = new Map<string, string[]>();
  for (const room of floor.rooms) {
    for (let i = 0; i < room.points.length; i++) {
      const a = room.points[i];
      const b = room.points[(i + 1) % room.points.length];
      const wallId = byEdge.get(edgeKey(a, b));
      if (!wallId) continue;
      const list = result.get(wallId);
      if (list) list.push(room.id);
      else result.set(wallId, [room.id]);
    }
  }
  return result;
}

/**
 * Builds the topology JSON the Aerie climate controller consumes (spec #7's
 * "give it context for the topology of the rooms and sensors" - see
 * TODO_MODELING.md's CFD research section: no standard format fits a home
 * climate controller, so this is Aerie-specific). Pure and WASM-free, unlike
 * exportGeometry.ts's CSG pass, since a structural description of rooms and
 * openings doesn't need the actual solid geometry to exist.
 */
export function buildTopology(project: ProjectDocument): TopologyDocument {
  const layout = computeBuildingLayout(project);
  const rooms: TopologyRoomNode[] = [];
  const openings: TopologyOpeningEdge[] = [];

  for (const floor of layout.floors) {
    const wallToRooms = roomsByWallId(floor);

    for (const room of floor.rooms) {
      const height = effectiveRoomHeight(room, floor.baseZ, layout.topZ);
      rooms.push({
        id: room.id,
        name: room.name,
        floorIndex: floor.floorIndex,
        floorArea: room.area,
        volume: approxRoomVolume(room, height),
        ceilingProfile: { kind: room.kind, eaveHeight: room.eaveHeight, ridgeHeight: room.ridgeHeight },
        stairwellVoid: room.stairwellVoid,
      });
    }

    for (const opening of floor.openings) {
      const [roomA = null, roomB = null] = wallToRooms.get(opening.wallId) ?? [];
      openings.push({
        id: opening.id,
        kind: opening.kind,
        floorIndex: floor.floorIndex,
        roomA,
        roomB,
        freeArea: opening.width * opening.headHeight,
        sillHeight: 0,
        headHeight: opening.headHeight,
      });
    }
  }

  return {
    schemaVersion: TOPOLOGY_SCHEMA_VERSION,
    projectName: project.name,
    generatedAt: new Date().toISOString(),
    units: 'm',
    rooms,
    openings,
    sensors: [],
  };
}
