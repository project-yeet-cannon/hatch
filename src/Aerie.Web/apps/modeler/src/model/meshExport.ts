import * as THREE from 'three';
import { GLTFExporter } from 'three/addons/exporters/GLTFExporter.js';
import { OBJExporter } from 'three/addons/exporters/OBJExporter.js';
import type { PatchedMesh } from './exportGeometry';

/**
 * Splits the mesh's triangles by patch into sibling Mesh objects that share
 * the same position buffer (only their index arrays differ), so both
 * exporters below emit one named group per patch ("OBJ (with groups)");
 * GLB's per-mesh nodes are the equivalent for glTF/GLB viewers - see the
 * README's CFD research section, item 5 (general CAD/viz tooling).
 */
function buildPatchGroup(mesh: PatchedMesh): THREE.Group {
  const group = new THREE.Group();
  const positionAttribute = new THREE.BufferAttribute(mesh.positions, 3);

  const indicesByPatch = new Map<string, number[]>();
  const numTri = mesh.indices.length / 3;
  for (let tri = 0; tri < numTri; tri++) {
    const patch = mesh.triPatch[tri];
    let list = indicesByPatch.get(patch);
    if (!list) {
      list = [];
      indicesByPatch.set(patch, list);
    }
    list.push(mesh.indices[tri * 3], mesh.indices[tri * 3 + 1], mesh.indices[tri * 3 + 2]);
  }

  for (const [patch, indices] of indicesByPatch) {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', positionAttribute);
    geometry.setIndex(indices);
    geometry.computeVertexNormals();
    const patchMesh = new THREE.Mesh(geometry, new THREE.MeshStandardMaterial());
    patchMesh.name = patch;
    group.add(patchMesh);
  }

  return group;
}

export function meshToOBJ(mesh: PatchedMesh): string {
  const group = buildPatchGroup(mesh);
  return new OBJExporter().parse(group);
}

export async function meshToGLB(mesh: PatchedMesh): Promise<ArrayBuffer> {
  const group = buildPatchGroup(mesh);
  const result = await new GLTFExporter().parseAsync(group, { binary: true });
  if (!(result instanceof ArrayBuffer)) throw new Error('Expected a binary GLB result from GLTFExporter.');
  return result;
}
