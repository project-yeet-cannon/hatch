import { describe, expect, it } from 'vitest';
import { meshToBinarySTL, meshToMultiSolidASCIISTL } from './stlExport';
import type { PatchedMesh } from './exportGeometry';

/** A single triangle in the XY plane, z=0, CCW when viewed from +z. */
function singleTriangleMesh(patch = 'walls'): PatchedMesh {
  return {
    positions: new Float32Array([0, 0, 0, 1, 0, 0, 0, 1, 0]),
    indices: new Uint32Array([0, 1, 2]),
    triPatch: [patch],
  };
}

describe('meshToBinarySTL', () => {
  it('writes the 84-byte header plus 50 bytes per triangle', () => {
    const buffer = meshToBinarySTL(singleTriangleMesh());
    expect(buffer.byteLength).toBe(84 + 50);
    const view = new DataView(buffer);
    expect(view.getUint32(80, true)).toBe(1);
  });

  it('writes an outward (+z) normal for a CCW-from-above triangle', () => {
    const buffer = meshToBinarySTL(singleTriangleMesh());
    const view = new DataView(buffer);
    expect(view.getFloat32(84, true)).toBeCloseTo(0, 6);
    expect(view.getFloat32(88, true)).toBeCloseTo(0, 6);
    expect(view.getFloat32(92, true)).toBeCloseTo(1, 6);
  });

  it('scales with triangle count', () => {
    const mesh: PatchedMesh = {
      positions: new Float32Array([0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0]),
      indices: new Uint32Array([0, 1, 2, 1, 3, 2]),
      triPatch: ['walls', 'walls'],
    };
    expect(meshToBinarySTL(mesh).byteLength).toBe(84 + 2 * 50);
  });
});

describe('meshToMultiSolidASCIISTL', () => {
  it('emits one solid/endsolid block per patch, each with its own facet', () => {
    const mesh: PatchedMesh = {
      positions: new Float32Array([0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1]),
      indices: new Uint32Array([0, 1, 2, 1, 3, 2]),
      triPatch: ['walls', 'floor'],
    };
    const ascii = meshToMultiSolidASCIISTL(mesh);
    expect(ascii).toContain('solid walls');
    expect(ascii).toContain('endsolid walls');
    expect(ascii).toContain('solid floor');
    expect(ascii).toContain('endsolid floor');
    expect((ascii.match(/facet normal/g) ?? []).length).toBe(2);
    expect((ascii.match(/endfacet/g) ?? []).length).toBe(2);
  });

  it('groups every triangle of the same patch under one solid block', () => {
    const mesh: PatchedMesh = {
      positions: new Float32Array([0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0]),
      indices: new Uint32Array([0, 1, 2, 1, 3, 2]),
      triPatch: ['walls', 'walls'],
    };
    const ascii = meshToMultiSolidASCIISTL(mesh);
    expect((ascii.match(/^solid walls$/gm) ?? []).length).toBe(1);
    expect((ascii.match(/facet normal/g) ?? []).length).toBe(2);
  });

  it('sanitizes whitespace in patch names for the STL solid name', () => {
    const ascii = meshToMultiSolidASCIISTL(singleTriangleMesh('door kitchen hall'));
    expect(ascii).toContain('solid door_kitchen_hall');
  });
});
