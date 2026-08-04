import type { PatchedMesh } from './exportGeometry';

export interface ExportValidationIssue {
  severity: 'error' | 'warning';
  message: string;
}

export interface ExportValidationResult {
  ok: boolean;
  issues: ExportValidationIssue[];
  triangleCount: number;
  vertexCount: number;
  surfaceArea: number;
}

/** Triangles thinner than this (in m^2) are treated as degenerate - a mesher chokes on them long before they'd be visible. */
const DEGENERATE_AREA_EPSILON = 1e-9;

/** Vertices closer together than this (in meters) are treated as duplicates when checking for a watertight boundary (every edge used by exactly two triangles). */
const VERTEX_MERGE_EPSILON = 1e-5;

function vertexKey(x: number, y: number, z: number): string {
  const scale = 1 / VERTEX_MERGE_EPSILON;
  return `${Math.round(x * scale)}:${Math.round(y * scale)}:${Math.round(z * scale)}`;
}

/**
 * Pre-export sanity checks that a mesher would otherwise discover the hard
 * way (spec #8's "pre-export validator (watertight/manifold check,
 * degenerate-triangle check) so files fail here rather than inside a
 * mesher"). manifold-3d's own `.status()` already caught structural CSG
 * failures upstream (see exportGeometry.ts's warnings) - this is a second,
 * independent pass over the plain triangle soup that's actually about to be
 * written to disk, since a bug in the STL/OBJ/GLB writer itself wouldn't
 * show up in Manifold's status. Every non-degenerate edge should be shared
 * by exactly two triangles for the mesh to be watertight; a boundary edge
 * (used by only one) is exactly what snappyHexMesh rejects as "not closed".
 */
export function validateExportMesh(mesh: PatchedMesh): ExportValidationResult {
  const issues: ExportValidationIssue[] = [];
  const { positions, indices } = mesh;
  const numTri = indices.length / 3;
  const numVert = positions.length / 3;

  let degenerateCount = 0;
  let surfaceArea = 0;
  const edgeUsers = new Map<string, number>();

  for (let tri = 0; tri < numTri; tri++) {
    const ia = indices[tri * 3];
    const ib = indices[tri * 3 + 1];
    const ic = indices[tri * 3 + 2];
    const ax = positions[ia * 3];
    const ay = positions[ia * 3 + 1];
    const az = positions[ia * 3 + 2];
    const bx = positions[ib * 3];
    const by = positions[ib * 3 + 1];
    const bz = positions[ib * 3 + 2];
    const cx = positions[ic * 3];
    const cy = positions[ic * 3 + 1];
    const cz = positions[ic * 3 + 2];

    const ux = bx - ax;
    const uy = by - ay;
    const uz = bz - az;
    const vx = cx - ax;
    const vy = cy - ay;
    const vz = cz - az;
    const cxp = uy * vz - uz * vy;
    const cyp = uz * vx - ux * vz;
    const czp = ux * vy - uy * vx;
    const area = Math.hypot(cxp, cyp, czp) / 2;
    surfaceArea += area;
    if (area < DEGENERATE_AREA_EPSILON) {
      degenerateCount++;
      continue;
    }

    const keys = [vertexKey(ax, ay, az), vertexKey(bx, by, bz), vertexKey(cx, cy, cz)];
    for (let e = 0; e < 3; e++) {
      const from = keys[e];
      const to = keys[(e + 1) % 3];
      const edgeKey = from < to ? `${from}|${to}` : `${to}|${from}`;
      edgeUsers.set(edgeKey, (edgeUsers.get(edgeKey) ?? 0) + 1);
    }
  }

  if (degenerateCount > 0) {
    issues.push({ severity: 'error', message: `${degenerateCount} degenerate (zero-area) triangle(s) found - a mesher will reject these.` });
  }

  let openEdges = 0;
  let overusedEdges = 0;
  for (const count of edgeUsers.values()) {
    if (count === 1) openEdges++;
    else if (count > 2) overusedEdges++;
  }
  if (openEdges > 0) {
    issues.push({ severity: 'error', message: `${openEdges} boundary edge(s) are only shared by one triangle - the mesh isn't watertight.` });
  }
  if (overusedEdges > 0) {
    issues.push({ severity: 'error', message: `${overusedEdges} edge(s) are shared by more than two triangles - the mesh is non-manifold there.` });
  }
  if (numTri === 0) {
    issues.push({ severity: 'error', message: 'The export contains no geometry - draw and close at least one room first.' });
  }

  return {
    ok: !issues.some((i) => i.severity === 'error'),
    issues,
    triangleCount: numTri,
    vertexCount: numVert,
    surfaceArea,
  };
}
