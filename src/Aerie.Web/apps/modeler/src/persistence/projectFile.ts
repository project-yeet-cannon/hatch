import { SCHEMA_VERSION, type ProjectDocument } from '../model/schema';

export const PROJECT_FILE_EXTENSION = '.aeriemodel.json';

export class ProjectFileError extends Error {}

export function serializeProject(project: ProjectDocument): string {
  return JSON.stringify(project, null, 2);
}

export function parseProjectFile(raw: string): ProjectDocument {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch (err) {
    throw new ProjectFileError(`Not valid JSON: ${err instanceof Error ? err.message : String(err)}`);
  }

  if (typeof parsed !== 'object' || parsed === null) {
    throw new ProjectFileError('File does not contain a project object.');
  }

  const doc = parsed as Partial<ProjectDocument>;
  if (doc.schemaVersion !== SCHEMA_VERSION) {
    throw new ProjectFileError(
      `Unsupported schema version ${String(doc.schemaVersion)} (this build reads version ${SCHEMA_VERSION}).`,
    );
  }
  if (typeof doc.id !== 'string' || typeof doc.name !== 'string' || !Array.isArray(doc.sketches)) {
    throw new ProjectFileError('File is missing required project fields.');
  }

  return doc as ProjectDocument;
}

export function projectFileName(project: ProjectDocument): string {
  const safeName =
    project.name
      .trim()
      .replace(/[^a-z0-9-_]+/gi, '-')
      .replace(/^-+|-+$/g, '') || 'project';
  return `${safeName}${PROJECT_FILE_EXTENSION}`;
}

// Browser-only from here down: triggers a real file download / reads a real
// File object. Not unit-tested (see README) - exercised by hand in the app.

export function downloadProject(project: ProjectDocument): void {
  const blob = new Blob([serializeProject(project)], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = projectFileName(project);
    anchor.click();
  } finally {
    URL.revokeObjectURL(url);
  }
}

export async function readProjectFile(file: File): Promise<ProjectDocument> {
  const raw = await file.text();
  return parseProjectFile(raw);
}
