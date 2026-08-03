import type { Point2, RoomLabel, WallSegment } from './schema';

export interface RoomPolygon {
  /** Ordered, closed loop of vertices bounding the room (last point does not repeat the first). */
  points: Point2[];
  /** Square meters. */
  area: number;
  centroid: Point2;
}

const VERTEX_EPSILON = 1e-4;
// Below this, a traced loop is either a dangling wall's there-and-back trace
// or float noise, not a real room.
const MIN_ROOM_AREA = 0.01;

function vertexKey(p: Point2): string {
  return `${Math.round(p.x / VERTEX_EPSILON)}:${Math.round(p.y / VERTEX_EPSILON)}`;
}

interface HalfEdge {
  from: Point2;
  to: Point2;
}

function angleOf(edge: HalfEdge): number {
  return Math.atan2(edge.to.y - edge.from.y, edge.to.x - edge.from.x);
}

function normalizeAngle(radians: number): number {
  const twoPi = Math.PI * 2;
  return ((radians % twoPi) + twoPi) % twoPi;
}

function signedArea(points: Point2[]): number {
  let sum = 0;
  for (let i = 0; i < points.length; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    sum += a.x * b.y - b.x * a.y;
  }
  return sum / 2;
}

function polygonCentroid(points: Point2[], area: number): Point2 {
  let cx = 0;
  let cy = 0;
  for (let i = 0; i < points.length; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    const cross = a.x * b.y - b.x * a.y;
    cx += (a.x + b.x) * cross;
    cy += (a.y + b.y) * cross;
  }
  const factor = 1 / (6 * area);
  return { x: cx * factor, y: cy * factor };
}

/**
 * Detects enclosed rooms in a wall centerline graph via planar-face tracing
 * (the standard DCEL/"polygonize" technique for turning a line network into
 * polygons): build both directed half-edges for every wall, then from each
 * half-edge repeatedly continue into the next outgoing edge (in angular
 * order around the shared vertex) immediately after the one just arrived
 * from. That traces the face lying clockwise of each half-edge. Interior
 * rooms come out with positive shoelace area; the unbounded exterior(s) come
 * out negative; a dangling wall stub traces a zero-area there-and-back loop.
 * Filtering to area > MIN_ROOM_AREA keeps only real rooms.
 *
 * Walls that meet at a T (one wall's endpoint landing mid-span of another,
 * not at its corner) are NOT split by this function — callers that want that
 * point treated as a shared vertex must split the underlying wall first (see
 * FloorPlanEditor's edge-snapping), otherwise the two walls are graph-
 * disconnected there and no room will close through it.
 */
export function detectRooms(walls: readonly WallSegment[]): RoomPolygon[] {
  const outgoingByVertex = new Map<string, HalfEdge[]>();
  const seenPairs = new Set<string>();

  function addOutgoing(vertexKeyStr: string, edge: HalfEdge) {
    const list = outgoingByVertex.get(vertexKeyStr);
    if (list) list.push(edge);
    else outgoingByVertex.set(vertexKeyStr, [edge]);
  }

  for (const wall of walls) {
    const fromKey = vertexKey(wall.start);
    const toKey = vertexKey(wall.end);
    if (fromKey === toKey) continue; // zero-length wall

    const pairKey = fromKey < toKey ? `${fromKey}|${toKey}` : `${toKey}|${fromKey}`;
    if (seenPairs.has(pairKey)) continue; // duplicate wall between the same two points
    seenPairs.add(pairKey);

    addOutgoing(fromKey, { from: wall.start, to: wall.end });
    addOutgoing(toKey, { from: wall.end, to: wall.start });
  }

  for (const list of outgoingByVertex.values()) {
    list.sort((a, b) => angleOf(a) - angleOf(b));
  }

  // At the shared vertex, continue into the candidate that is the largest
  // CCW rotation away from the direction we arrived from (i.e. the next one
  // going clockwise). This keeps each bounded face's interior on the same
  // side consistently; picking the smallest rotation instead traces across
  // interior walls and merges rooms together. The one candidate this never
  // picks unless it's the only option is the edge's own twin (turning
  // straight back the way we came), which is exactly right for a dangling
  // wall stub.
  function nextHalfEdge(current: HalfEdge): HalfEdge {
    const candidates = outgoingByVertex.get(vertexKey(current.to));
    if (!candidates || candidates.length === 0) {
      throw new Error('Room detection: vertex with an incoming edge but no outgoing edges (unreachable).');
    }
    const backAngle = normalizeAngle(angleOf({ from: current.to, to: current.from }));
    let best = candidates[0];
    let bestDiff = -1;
    for (const candidate of candidates) {
      const diff = normalizeAngle(angleOf(candidate) - backAngle);
      if (diff > bestDiff) {
        bestDiff = diff;
        best = candidate;
      }
    }
    return best;
  }

  const allHalfEdges = [...outgoingByVertex.values()].flat();
  const visited = new Set<HalfEdge>();
  const rooms: RoomPolygon[] = [];

  for (const start of allHalfEdges) {
    if (visited.has(start)) continue;

    const loop: Point2[] = [];
    let current = start;
    let steps = 0;
    const maxSteps = allHalfEdges.length + 1;
    do {
      visited.add(current);
      loop.push(current.from);
      current = nextHalfEdge(current);
      steps++;
      if (steps > maxSteps) {
        throw new Error('Room detection: face trace failed to close (bug in wall graph traversal).');
      }
    } while (current !== start);

    const area = signedArea(loop);
    if (area > MIN_ROOM_AREA) {
      rooms.push({ points: loop, area, centroid: polygonCentroid(loop, area) });
    }
  }

  return rooms;
}

/** Standard ray-casting point-in-polygon test, used to re-match a persisted room name to its (re-derived) polygon. */
export function pointInPolygon(point: Point2, polygon: readonly Point2[]): boolean {
  let inside = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const pi = polygon[i];
    const pj = polygon[j];
    const intersects = pi.y > point.y !== pj.y > point.y && point.x < ((pj.x - pi.x) * (point.y - pi.y)) / (pj.y - pi.y) + pi.x;
    if (intersects) inside = !inside;
  }
  return inside;
}

/** Finds the persisted name for a freshly-detected room, if any label's seed point still falls inside it. */
export function matchRoomLabel(labels: readonly RoomLabel[], room: RoomPolygon): RoomLabel | undefined {
  return labels.find((label) => pointInPolygon(label.seed, room.points));
}
