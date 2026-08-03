import { describe, expect, it } from 'vitest';
import { createEmptyProject } from '../model/schema';
import { ProjectFileError, parseProjectFile, projectFileName, serializeProject } from './projectFile';

describe('serializeProject / parseProjectFile', () => {
  it('round-trips a project unchanged', () => {
    const project = createEmptyProject('My House');
    const parsed = parseProjectFile(serializeProject(project));
    expect(parsed).toEqual(project);
  });

  it('rejects malformed JSON', () => {
    expect(() => parseProjectFile('{not json')).toThrow(ProjectFileError);
  });

  it('rejects non-object JSON', () => {
    expect(() => parseProjectFile('42')).toThrow(ProjectFileError);
    expect(() => parseProjectFile('null')).toThrow(ProjectFileError);
  });

  it('rejects an unsupported schema version', () => {
    const project = { ...createEmptyProject(), schemaVersion: 999 };
    expect(() => parseProjectFile(JSON.stringify(project))).toThrow(/schema version/i);
  });

  it('rejects a document missing required fields', () => {
    expect(() => parseProjectFile(JSON.stringify({ schemaVersion: 1 }))).toThrow(ProjectFileError);
  });
});

describe('projectFileName', () => {
  it('slugifies the project name', () => {
    expect(projectFileName(createEmptyProject('My  Cozy House!'))).toBe('My-Cozy-House.aeriemodel.json');
  });

  it('falls back to "project" for an empty/unsafe name', () => {
    expect(projectFileName(createEmptyProject('   '))).toBe('project.aeriemodel.json');
  });
});
