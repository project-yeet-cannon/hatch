import { ANGLE_SNAP_TOLERANCE_DEGREES } from './geometry';
import type { Point2, WallSegment } from './schema';

export type EdgeStatus = 'measured' | 'derived' | 'estimated';

export interface SketchSolution {
  /** Same wall ids/thickness as the input, start/end replaced with solved coordinates. */
  walls: WallSegment[];
  statuses: Map<string, EdgeStatus>;
  /** Solved length in meters, keyed by wall id. */
  lengths: Map<string, number>;
}

const VERTEX_EPSILON = 1e-4;

// Relative constraint strengths (residuals are weighted, cost is sum of
// squares, so "strength" scales roughly with weight^2). Measured lengths
// should dominate everything; auto-inferred angle relationships should hold
// unless a measurement disagrees; the anchor is just enough regularization to
// keep an otherwise-unconstrained sketch from drifting, and the probe weight
// (used only for measured/derived/estimated classification, see
// classifyStatuses below) sits well above the anchor so a genuinely free edge
// visibly follows it, but well below the angle lock so a genuinely pinned
// edge does not.
const WEIGHT_MEASURED = 1000;
const WEIGHT_ANGLE = 50;
const WEIGHT_ANCHOR = 1;
const WEIGHT_PROBE = 12;

const RESPONSIVENESS_DERIVED_MAX = 0.3;

function vertexKey(p: Point2): string {
  return `${Math.round(p.x / VERTEX_EPSILON)}:${Math.round(p.y / VERTEX_EPSILON)}`;
}

interface Residual {
  evaluate(x: readonly number[]): number;
}

function lengthResidual(i0: number, i1: number, target: number, weight: number): Residual {
  return {
    evaluate(x) {
      const dx = x[i1 * 2] - x[i0 * 2];
      const dy = x[i1 * 2 + 1] - x[i0 * 2 + 1];
      return weight * (Math.hypot(dx, dy) - target);
    },
  };
}

function horizontalResidual(i0: number, i1: number, weight: number): Residual {
  return { evaluate: (x) => weight * (x[i1 * 2 + 1] - x[i0 * 2 + 1]) };
}

function verticalResidual(i0: number, i1: number, weight: number): Residual {
  return { evaluate: (x) => weight * (x[i1 * 2] - x[i0 * 2]) };
}

/** Keeps two edges (each defined by a pair of vertex indices) at a fixed relative angle: perpendicular (dot = 0) or collinear (cross = 0), using unit direction vectors so the constraint strength doesn't scale with edge length. */
function angleRelationResidual(a0: number, a1: number, b0: number, b1: number, kind: 'perpendicular' | 'collinear', weight: number): Residual {
  return {
    evaluate(x) {
      const ux = x[a1 * 2] - x[a0 * 2];
      const uy = x[a1 * 2 + 1] - x[a0 * 2 + 1];
      const vx = x[b1 * 2] - x[b0 * 2];
      const vy = x[b1 * 2 + 1] - x[b0 * 2 + 1];
      const uLen = Math.hypot(ux, uy);
      const vLen = Math.hypot(vx, vy);
      if (uLen < 1e-9 || vLen < 1e-9) return 0;
      const uhx = ux / uLen;
      const uhy = uy / uLen;
      const vhx = vx / vLen;
      const vhy = vy / vLen;
      return kind === 'perpendicular' ? weight * (uhx * vhx + uhy * vhy) : weight * (uhx * vhy - uhy * vhx);
    },
  };
}

function anchorResidual(i: number, axis: 0 | 1, target: number, weight: number): Residual {
  return { evaluate: (x) => weight * (x[i * 2 + axis] - target) };
}

function sumSquares(r: readonly number[]): number {
  let s = 0;
  for (const v of r) s += v * v;
  return s;
}

function evaluateAll(residuals: readonly Residual[], x: readonly number[]): number[] {
  return residuals.map((r) => r.evaluate(x));
}

/** Central-difference Jacobian: rows are residuals, columns are parameters. Fine for the small (tens-of-vertices) systems a floor plan produces; not meant for CAD-scale imports. */
function jacobian(residuals: readonly Residual[], x: readonly number[], h = 1e-6): number[][] {
  const n = x.length;
  const m = residuals.length;
  const J: number[][] = Array.from({ length: m }, () => new Array(n).fill(0));
  for (let j = 0; j < n; j++) {
    const xPlus = x.slice();
    xPlus[j] += h;
    const xMinus = x.slice();
    xMinus[j] -= h;
    const rPlus = evaluateAll(residuals, xPlus);
    const rMinus = evaluateAll(residuals, xMinus);
    for (let i = 0; i < m; i++) J[i][j] = (rPlus[i] - rMinus[i]) / (2 * h);
  }
  return J;
}

function transpose(A: readonly number[][]): number[][] {
  const rows = A.length;
  const cols = rows > 0 ? A[0].length : 0;
  const T: number[][] = Array.from({ length: cols }, () => new Array(rows).fill(0));
  for (let i = 0; i < rows; i++) for (let j = 0; j < cols; j++) T[j][i] = A[i][j];
  return T;
}

function matMul(A: readonly number[][], B: readonly number[][]): number[][] {
  const rows = A.length;
  const inner = B.length;
  const cols = inner > 0 ? B[0].length : 0;
  const C: number[][] = Array.from({ length: rows }, () => new Array(cols).fill(0));
  for (let i = 0; i < rows; i++) {
    for (let k = 0; k < inner; k++) {
      const a = A[i][k];
      if (a === 0) continue;
      for (let j = 0; j < cols; j++) C[i][j] += a * B[k][j];
    }
  }
  return C;
}

function matVec(A: readonly number[][], v: readonly number[]): number[] {
  return A.map((row) => row.reduce((sum, a, j) => sum + a * v[j], 0));
}

/** Solves Ax = b via Gaussian elimination with partial pivoting. Returns null if A is (numerically) singular. */
function solveLinearSystem(A: readonly number[][], b: readonly number[]): number[] | null {
  const n = b.length;
  const M = A.map((row, i) => [...row, b[i]]);
  for (let col = 0; col < n; col++) {
    let pivotRow = col;
    let pivotVal = Math.abs(M[col][col]);
    for (let r = col + 1; r < n; r++) {
      if (Math.abs(M[r][col]) > pivotVal) {
        pivotVal = Math.abs(M[r][col]);
        pivotRow = r;
      }
    }
    if (pivotVal < 1e-12) return null;
    if (pivotRow !== col) [M[col], M[pivotRow]] = [M[pivotRow], M[col]];
    for (let r = col + 1; r < n; r++) {
      const factor = M[r][col] / M[col][col];
      if (factor === 0) continue;
      for (let c = col; c <= n; c++) M[r][c] -= factor * M[col][c];
    }
  }
  const x = new Array(n).fill(0);
  for (let row = n - 1; row >= 0; row--) {
    let sum = M[row][n];
    for (let c = row + 1; c < n; c++) sum -= M[row][c] * x[c];
    x[row] = sum / M[row][row];
  }
  return x;
}

/** Damped Gauss-Newton (Levenberg-Marquardt): repeatedly solves (JᵀJ + λI)Δ = -Jᵀr, accepting a step only if it reduces total cost and adapting λ accordingly. */
function levenbergMarquardt(x0: readonly number[], residuals: readonly Residual[], maxIterations: number): number[] {
  let x = [...x0];
  let r = evaluateAll(residuals, x);
  let cost = sumSquares(r);
  let lambda = 1e-3;

  for (let iter = 0; iter < maxIterations; iter++) {
    const J = jacobian(residuals, x);
    const Jt = transpose(J);
    const JtJ = matMul(Jt, J);
    const Jtr = matVec(Jt, r);
    const negJtr = Jtr.map((v) => -v);

    let improved = false;
    for (let attempt = 0; attempt < 12; attempt++) {
      const damped = JtJ.map((row, i) => row.map((v, j) => (i === j ? v + lambda : v)));
      const delta = solveLinearSystem(damped, negJtr);
      if (!delta) {
        lambda *= 10;
        continue;
      }
      const xNext = x.map((v, i) => v + delta[i]);
      const rNext = evaluateAll(residuals, xNext);
      const costNext = sumSquares(rNext);
      if (costNext < cost) {
        const improvement = cost - costNext;
        x = xNext;
        r = rNext;
        cost = costNext;
        lambda = Math.max(lambda / 3, 1e-12);
        improved = true;
        if (improvement < 1e-12) return x;
        break;
      }
      lambda *= 5;
    }
    if (!improved) break;
  }
  return x;
}

interface WallGraph {
  vertexIds: string[];
  positions: Point2[];
  /** Wall id -> [startVertexIndex, endVertexIndex]. */
  wallVertices: Map<string, [number, number]>;
}

function buildWallGraph(walls: readonly WallSegment[]): WallGraph {
  const indexByKey = new Map<string, number>();
  const positions: Point2[] = [];
  const vertexIds: string[] = [];

  function indexOf(p: Point2): number {
    const key = vertexKey(p);
    const existing = indexByKey.get(key);
    if (existing !== undefined) return existing;
    const index = positions.length;
    indexByKey.set(key, index);
    positions.push(p);
    vertexIds.push(key);
    return index;
  }

  const wallVertices = new Map<string, [number, number]>();
  for (const wall of walls) {
    wallVertices.set(wall.id, [indexOf(wall.start), indexOf(wall.end)]);
  }
  return { vertexIds, positions, wallVertices };
}

function normalizeAngle(radians: number): number {
  const twoPi = Math.PI * 2;
  return ((radians % twoPi) + twoPi) % twoPi;
}

const TOLERANCE_RAD = (ANGLE_SNAP_TOLERANCE_DEGREES * Math.PI) / 180;

function angleWithin(angle: number, target: number, tolerance: number): boolean {
  const diff = Math.abs(normalizeAngle(angle - target));
  return Math.min(diff, Math.PI * 2 - diff) <= tolerance;
}

/** Builds the auto-inferred constraint set: axis locks for near-axis walls, plus perpendicular/collinear locks between wall pairs that meet at a near-90°/near-180° corner. */
function buildAutoConstraints(walls: readonly WallSegment[], graph: WallGraph): Residual[] {
  const residuals: Residual[] = [];
  const byVertex = new Map<number, { wallId: string; from: number; to: number }[]>();

  for (const wall of walls) {
    const [a, b] = graph.wallVertices.get(wall.id)!;
    const dx = graph.positions[b].x - graph.positions[a].x;
    const dy = graph.positions[b].y - graph.positions[a].y;
    const len = Math.hypot(dx, dy);
    if (len < 1e-9) continue;

    // Axis lock: the wall's line angle, mod π (a line has no direction), is
    // close to horizontal or vertical.
    const lineAngle = normalizeAngle(Math.atan2(dy, dx)) % Math.PI;
    if (angleWithin(lineAngle, 0, TOLERANCE_RAD) || angleWithin(lineAngle, Math.PI, TOLERANCE_RAD)) {
      residuals.push(horizontalResidual(a, b, WEIGHT_ANGLE));
    } else if (angleWithin(lineAngle, Math.PI / 2, TOLERANCE_RAD)) {
      residuals.push(verticalResidual(a, b, WEIGHT_ANGLE));
    }

    for (const [v, other] of [[a, b] as const, [b, a] as const]) {
      const list = byVertex.get(v);
      const entry = { wallId: wall.id, from: v, to: other };
      if (list) list.push(entry);
      else byVertex.set(v, [entry]);
    }
  }

  const seenPairs = new Set<string>();
  for (const [, edges] of byVertex) {
    for (let i = 0; i < edges.length; i++) {
      for (let j = i + 1; j < edges.length; j++) {
        const e1 = edges[i];
        const e2 = edges[j];
        const pairKey = e1.wallId < e2.wallId ? `${e1.wallId}|${e2.wallId}` : `${e2.wallId}|${e1.wallId}`;
        if (seenPairs.has(pairKey)) continue;
        seenPairs.add(pairKey);

        const p1From = graph.positions[e1.from];
        const p1To = graph.positions[e1.to];
        const p2From = graph.positions[e2.from];
        const p2To = graph.positions[e2.to];
        const angle1 = Math.atan2(p1To.y - p1From.y, p1To.x - p1From.x);
        const angle2 = Math.atan2(p2To.y - p2From.y, p2To.x - p2From.x);
        // Angle between the two "pointing away from the shared vertex" directions, in [0, π]:
        // ~π means the walls continue straight through the vertex (collinear); ~π/2 means a square corner.
        const rawDiff = normalizeAngle(angle1 - angle2);
        const between = Math.min(rawDiff, Math.PI * 2 - rawDiff);
        if (angleWithin(between, Math.PI, TOLERANCE_RAD)) {
          residuals.push(angleRelationResidual(e1.from, e1.to, e2.from, e2.to, 'collinear', WEIGHT_ANGLE));
        } else if (angleWithin(between, Math.PI / 2, TOLERANCE_RAD)) {
          residuals.push(angleRelationResidual(e1.from, e1.to, e2.from, e2.to, 'perpendicular', WEIGHT_ANGLE));
        }
      }
    }
  }

  return residuals;
}

function buildAnchors(graph: WallGraph, weight: number): Residual[] {
  const residuals: Residual[] = [];
  graph.positions.forEach((p, i) => {
    residuals.push(anchorResidual(i, 0, p.x, weight));
    residuals.push(anchorResidual(i, 1, p.y, weight));
  });
  return residuals;
}

function positionsFromX(graph: WallGraph, x: readonly number[]): Point2[] {
  return graph.vertexIds.map((_, i) => ({ x: x[i * 2], y: x[i * 2 + 1] }));
}

function wallsFromPositions(walls: readonly WallSegment[], graph: WallGraph, positions: readonly Point2[]): WallSegment[] {
  return walls.map((wall) => {
    const [a, b] = graph.wallVertices.get(wall.id)!;
    return { ...wall, start: positions[a], end: positions[b] };
  });
}

/**
 * Solves the dimension/constraint system for a sketch: the drawn walls are
 * the initial guess, user-entered lengths (WallSegment.measuredLength) are
 * hard-ish targets, and near-axis/near-square relationships detected in the
 * initial guess are held as auto-inferred constraints (nonlinear
 * least-squares via Levenberg-Marquardt over vertex positions).
 * Returns the same walls with solved positions, plus a measured/derived/
 * estimated status and solved length per wall for UI feedback.
 */
export function solveSketch(walls: readonly WallSegment[]): SketchSolution {
  if (walls.length === 0) return { walls: [], statuses: new Map(), lengths: new Map() };

  const graph = buildWallGraph(walls);
  const x0 = graph.positions.flatMap((p) => [p.x, p.y]);

  const measuredResiduals: Residual[] = [];
  for (const wall of walls) {
    if (wall.measuredLength === undefined) continue;
    const [a, b] = graph.wallVertices.get(wall.id)!;
    measuredResiduals.push(lengthResidual(a, b, wall.measuredLength, WEIGHT_MEASURED));
  }
  const angleResiduals = buildAutoConstraints(walls, graph);
  const anchors = buildAnchors(graph, WEIGHT_ANCHOR);

  const baseResiduals = [...measuredResiduals, ...angleResiduals, ...anchors];
  const xSolved = levenbergMarquardt(x0, baseResiduals, 30);
  const solvedPositions = positionsFromX(graph, xSolved);

  const lengths = new Map<string, number>();
  for (const wall of walls) {
    const [a, b] = graph.wallVertices.get(wall.id)!;
    lengths.set(wall.id, Math.hypot(solvedPositions[b].x - solvedPositions[a].x, solvedPositions[b].y - solvedPositions[a].y));
  }

  const statuses = classifyStatuses(walls, graph, xSolved, measuredResiduals, angleResiduals, anchors, lengths);

  return { walls: wallsFromPositions(walls, graph, solvedPositions), statuses, lengths };
}

/**
 * Classifies each wall as measured / derived / estimated. Measured is
 * trivial (an explicit length was entered). For the rest, this "probes" the
 * solved system: add a soft nudge pulling the edge's length away from its
 * solved value and re-solve (warm-started, so this converges in a couple of
 * iterations). If the system barely moves, other constraints are pinning
 * that length down (derived); if it moves close to the full nudge, nothing
 * is holding it beyond the initial sketch guess (estimated). This avoids
 * needing a full rigidity/degrees-of-freedom analysis of the constraint
 * graph, at the cost of one extra small solve per unmeasured wall.
 */
function classifyStatuses(
  walls: readonly WallSegment[],
  graph: WallGraph,
  xSolved: readonly number[],
  measuredResiduals: readonly Residual[],
  angleResiduals: readonly Residual[],
  anchors: readonly Residual[],
  lengths: ReadonlyMap<string, number>,
): Map<string, EdgeStatus> {
  const statuses = new Map<string, EdgeStatus>();
  const baseResiduals = [...measuredResiduals, ...angleResiduals, ...anchors];

  for (const wall of walls) {
    if (wall.measuredLength !== undefined) {
      statuses.set(wall.id, 'measured');
      continue;
    }
    const [a, b] = graph.wallVertices.get(wall.id)!;
    const solvedLength = lengths.get(wall.id) ?? 0;
    const delta = Math.max(0.3, solvedLength * 0.15);
    const target = solvedLength + delta;

    const probed = levenbergMarquardt(xSolved, [...baseResiduals, lengthResidual(a, b, target, WEIGHT_PROBE)], 10);
    const probedPositions = positionsFromX(graph, probed);
    const probedLength = Math.hypot(probedPositions[b].x - probedPositions[a].x, probedPositions[b].y - probedPositions[a].y);
    const responsiveness = Math.abs(probedLength - solvedLength) / delta;

    statuses.set(wall.id, responsiveness <= RESPONSIVENESS_DERIVED_MAX ? 'derived' : 'estimated');
  }

  return statuses;
}
