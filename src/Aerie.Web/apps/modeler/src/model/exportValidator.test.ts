import { describe, expect, it } from 'vitest';
import { validateExportMesh } from './exportValidator';
import type { PatchedMesh } from './exportGeometry';

/** A unit cube, 12 triangles, watertight by construction. */
function cubeMesh(): PatchedMesh {
  const positions = new Float32Array([
    0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, // bottom (z=0)
    0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1, // top (z=1)
  ]);
  const indices = new Uint32Array([
    0, 1, 2, 0, 2, 3, // bottom
    4, 6, 5, 4, 7, 6, // top
    0, 4, 5, 0, 5, 1, // front
    1, 5, 6, 1, 6, 2, // right
    2, 6, 7, 2, 7, 3, // back
    3, 7, 4, 3, 4, 0, // left
  ]);
  const triPatch = new Array<string>(indices.length / 3).fill('walls');
  return { positions, indices, triPatch };
}

describe('validateExportMesh', () => {
  it('passes a watertight cube with no degenerate triangles', () => {
    const result = validateExportMesh(cubeMesh());
    expect(result.ok).toBe(true);
    expect(result.issues).toEqual([]);
    expect(result.triangleCount).toBe(12);
    expect(result.vertexCount).toBe(8);
    expect(result.surfaceArea).toBeCloseTo(6, 6);
  });

  it('flags a mesh with a missing face as not watertight', () => {
    const mesh = cubeMesh();
    // Drop the last two triangles (the "left" face) - its two edges shared
    // with the front/top/bottom faces become open (used by only one triangle).
    const openMesh: PatchedMesh = {
      positions: mesh.positions,
      indices: mesh.indices.slice(0, mesh.indices.length - 6),
      triPatch: mesh.triPatch.slice(0, mesh.triPatch.length - 2),
    };
    const result = validateExportMesh(openMesh);
    expect(result.ok).toBe(false);
    expect(result.issues.some((i) => i.message.includes('boundary edge'))).toBe(true);
  });

  it('flags a zero-area triangle as degenerate', () => {
    const mesh = cubeMesh();
    const positions = new Float32Array([...mesh.positions, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
    const indices = new Uint32Array([...mesh.indices, 8, 9, 10]);
    const triPatch = [...mesh.triPatch, 'walls'];
    const result = validateExportMesh({ positions, indices, triPatch });
    expect(result.ok).toBe(false);
    expect(result.issues.some((i) => i.message.includes('degenerate'))).toBe(true);
  });

  it('flags an empty mesh', () => {
    const result = validateExportMesh({ positions: new Float32Array(), indices: new Uint32Array(), triPatch: [] });
    expect(result.ok).toBe(false);
    expect(result.issues.some((i) => i.message.includes('no geometry'))).toBe(true);
  });
});
