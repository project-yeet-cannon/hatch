import type { PatchedMesh } from '../model/exportGeometry';
import { meshToGLB, meshToOBJ } from '../model/meshExport';
import { meshToBinarySTL, meshToMultiSolidASCIISTL } from '../model/stlExport';
import type { TopologyDocument } from '../model/topology';
import type { ProjectDocument } from '../model/schema';

// Browser-only from here down: triggers real file downloads, same pattern as
// persistence/projectFile.ts's downloadProject. Not unit-tested (see that
// file's own note) - exercised by hand in the app.

function safeFileBase(project: ProjectDocument): string {
  return (
    project.name
      .trim()
      .replace(/[^a-z0-9-_]+/gi, '-')
      .replace(/^-+|-+$/g, '') || 'project'
  );
}

function download(data: BlobPart, mimeType: string, fileName: string): void {
  const blob = new Blob([data], { type: mimeType });
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}

export function downloadBinarySTL(project: ProjectDocument, mesh: PatchedMesh): void {
  download(meshToBinarySTL(mesh), 'model/stl', `${safeFileBase(project)}.stl`);
}

export function downloadMultiSolidSTL(project: ProjectDocument, mesh: PatchedMesh): void {
  download(meshToMultiSolidASCIISTL(mesh), 'model/stl', `${safeFileBase(project)}.patches.stl`);
}

export function downloadOBJ(project: ProjectDocument, mesh: PatchedMesh): void {
  download(meshToOBJ(mesh), 'text/plain', `${safeFileBase(project)}.obj`);
}

export async function downloadGLB(project: ProjectDocument, mesh: PatchedMesh): Promise<void> {
  const glb = await meshToGLB(mesh);
  download(glb, 'model/gltf-binary', `${safeFileBase(project)}.glb`);
}

export function downloadTopologyJSON(project: ProjectDocument, topology: TopologyDocument): void {
  download(JSON.stringify(topology, null, 2), 'application/json', `${safeFileBase(project)}.topology.json`);
}
