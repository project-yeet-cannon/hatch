import type { PatchedMesh } from './exportGeometry';

export interface TriangleMesh {
  positions: Float32Array;
  indices: Uint32Array;
}

function faceNormal(positions: Float32Array, ia: number, ib: number, ic: number): [number, number, number] {
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
  const nx = uy * vz - uz * vy;
  const ny = uz * vx - ux * vz;
  const nz = ux * vy - uy * vx;
  const len = Math.hypot(nx, ny, nz) || 1;
  return [nx / len, ny / len, nz / len];
}

/**
 * Binary STL of the whole air volume as one unnamed solid - what SimScale and
 * most generic viewers expect (see the README's CFD research section:
 * OpenFOAM needs the named-patch ASCII variant below, but a plain binary STL
 * is the lowest-common-denominator format everything else reads).
 */
export function meshToBinarySTL(mesh: TriangleMesh): ArrayBuffer {
  const numTri = mesh.indices.length / 3;
  const buffer = new ArrayBuffer(84 + numTri * 50);
  const view = new DataView(buffer);
  // 80-byte header is left zeroed; STL doesn't standardize its content.
  view.setUint32(80, numTri, true);

  let offset = 84;
  for (let tri = 0; tri < numTri; tri++) {
    const ia = mesh.indices[tri * 3];
    const ib = mesh.indices[tri * 3 + 1];
    const ic = mesh.indices[tri * 3 + 2];
    const [nx, ny, nz] = faceNormal(mesh.positions, ia, ib, ic);
    view.setFloat32(offset, nx, true);
    view.setFloat32(offset + 4, ny, true);
    view.setFloat32(offset + 8, nz, true);
    offset += 12;
    for (const vi of [ia, ib, ic]) {
      view.setFloat32(offset, mesh.positions[vi * 3], true);
      view.setFloat32(offset + 4, mesh.positions[vi * 3 + 1], true);
      view.setFloat32(offset + 8, mesh.positions[vi * 3 + 2], true);
      offset += 12;
    }
    view.setUint16(offset, 0, true); // attribute byte count, unused
    offset += 2;
  }

  return buffer;
}

/** A valid STL solid name has no whitespace; patch names here are already simple identifiers (see exportGeometry.ts), but sanitize defensively since a room/sketch-derived name could end up in a future patch. */
function sanitizeSolidName(name: string): string {
  return name.trim().replace(/\s+/g, '_') || 'unnamed';
}

/**
 * Multi-solid ASCII STL: one `solid NAME ... endsolid NAME` block per
 * boundary patch (walls / floor / ceiling - see exportGeometry.ts), which is
 * the standard way to hand snappyHexMesh named boundary patches without a
 * separate config file describing which facets belong to which patch.
 */
export function meshToMultiSolidASCIISTL(mesh: PatchedMesh): string {
  const numTri = mesh.indices.length / 3;
  const triByPatch = new Map<string, number[]>();
  for (let tri = 0; tri < numTri; tri++) {
    const patch = mesh.triPatch[tri];
    let list = triByPatch.get(patch);
    if (!list) {
      list = [];
      triByPatch.set(patch, list);
    }
    list.push(tri);
  }

  const lines: string[] = [];
  for (const [patch, triangles] of triByPatch) {
    const name = sanitizeSolidName(patch);
    lines.push(`solid ${name}`);
    for (const tri of triangles) {
      const ia = mesh.indices[tri * 3];
      const ib = mesh.indices[tri * 3 + 1];
      const ic = mesh.indices[tri * 3 + 2];
      const [nx, ny, nz] = faceNormal(mesh.positions, ia, ib, ic);
      lines.push(`facet normal ${nx} ${ny} ${nz}`);
      lines.push('outer loop');
      for (const vi of [ia, ib, ic]) {
        lines.push(`vertex ${mesh.positions[vi * 3]} ${mesh.positions[vi * 3 + 1]} ${mesh.positions[vi * 3 + 2]}`);
      }
      lines.push('endloop');
      lines.push('endfacet');
    }
    lines.push(`endsolid ${name}`);
  }

  return lines.join('\n') + '\n';
}
