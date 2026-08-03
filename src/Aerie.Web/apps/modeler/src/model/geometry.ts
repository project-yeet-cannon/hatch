import type { Point2 } from './schema';

export function addV(a: Point2, b: Point2): Point2 {
  return { x: a.x + b.x, y: a.y + b.y };
}

export function subV(a: Point2, b: Point2): Point2 {
  return { x: a.x - b.x, y: a.y - b.y };
}

export function scaleV(a: Point2, s: number): Point2 {
  return { x: a.x * s, y: a.y * s };
}

export function lengthV(a: Point2): number {
  return Math.hypot(a.x, a.y);
}

export function distance(a: Point2, b: Point2): number {
  return lengthV(subV(b, a));
}

/** World units are meters throughout the model core. */
export const GRID_SIZE = 0.1;
export const ANGLE_SNAP_DEGREES = 15;
export const ANGLE_SNAP_TOLERANCE_DEGREES = 6;

export function snapToGrid(point: Point2, gridSize = GRID_SIZE): Point2 {
  return { x: Math.round(point.x / gridSize) * gridSize, y: Math.round(point.y / gridSize) * gridSize };
}

/** Closest of `candidates` to `point`, or null if none fall within `toleranceWorld`. */
export function nearestPoint(point: Point2, candidates: readonly Point2[], toleranceWorld: number): Point2 | null {
  let best: Point2 | null = null;
  let bestDist = toleranceWorld;
  for (const candidate of candidates) {
    const d = distance(point, candidate);
    if (d <= bestDist) {
      bestDist = d;
      best = candidate;
    }
  }
  return best;
}

/**
 * Snaps `point` to the nearest multiple of `incrementDegrees` around `origin`,
 * preserving distance from origin — used to lock a wall-in-progress to axis
 * (0/90/...) or other common angles while drawing. Returns `point` unchanged
 * (same reference) if no increment is within tolerance.
 */
export function snapToAngle(
  origin: Point2,
  point: Point2,
  incrementDegrees = ANGLE_SNAP_DEGREES,
  toleranceDegrees = ANGLE_SNAP_TOLERANCE_DEGREES,
): Point2 {
  const delta = subV(point, origin);
  const dist = lengthV(delta);
  if (dist < 1e-9) return point;

  const angle = Math.atan2(delta.y, delta.x);
  const incrementRad = (incrementDegrees * Math.PI) / 180;
  const nearestIncrement = Math.round(angle / incrementRad) * incrementRad;
  const diffDegrees = Math.abs(((angle - nearestIncrement) * 180) / Math.PI);
  if (diffDegrees > toleranceDegrees) return point;

  return { x: origin.x + Math.cos(nearestIncrement) * dist, y: origin.y + Math.sin(nearestIncrement) * dist };
}

export type SnapKind = 'vertex' | 'angle' | 'grid';

export interface SnapResult {
  point: Point2;
  kind: SnapKind;
}

/**
 * Resolves where a wall endpoint being drawn should actually land: snap to an
 * existing vertex first (so walls connect and rooms close), then to a common
 * angle off `origin` (rounding the distance along that angle to the grid so
 * the result doesn't drift off-axis), then to the plain grid.
 */
export function snapDrawPoint(
  raw: Point2,
  origin: Point2 | null,
  existingVertices: readonly Point2[],
  vertexToleranceWorld: number,
): SnapResult {
  const vertex = nearestPoint(raw, existingVertices, vertexToleranceWorld);
  if (vertex) return { point: vertex, kind: 'vertex' };

  if (origin) {
    const angled = snapToAngle(origin, raw);
    if (angled !== raw) {
      const delta = subV(angled, origin);
      const dist = lengthV(delta);
      const snappedDist = Math.round(dist / GRID_SIZE) * GRID_SIZE;
      const dir = dist > 1e-9 ? scaleV(delta, 1 / dist) : delta;
      return { point: addV(origin, scaleV(dir, snappedDist)), kind: 'angle' };
    }
  }

  return { point: snapToGrid(raw), kind: 'grid' };
}

export interface NearestOnSegment {
  point: Point2;
  /** 0 at `segStart`, 1 at `segEnd`. */
  t: number;
  distance: number;
}

export function nearestPointOnSegment(point: Point2, segStart: Point2, segEnd: Point2): NearestOnSegment {
  const seg = subV(segEnd, segStart);
  const segLenSq = seg.x * seg.x + seg.y * seg.y;
  const t = segLenSq < 1e-12 ? 0 : Math.max(0, Math.min(1, (subV(point, segStart).x * seg.x + subV(point, segStart).y * seg.y) / segLenSq));
  const projected = addV(segStart, scaleV(seg, t));
  return { point: projected, t, distance: distance(point, projected) };
}
