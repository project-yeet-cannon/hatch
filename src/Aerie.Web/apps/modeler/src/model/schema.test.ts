import { describe, expect, it } from 'vitest';
import { createEmptyProject, createEmptySketch, SCHEMA_VERSION, withLastWallRemoved, withWallAdded } from './schema';
import type { WallSegment } from './schema';

const WALL: WallSegment = { id: 'w1', start: { x: 0, y: 0 }, end: { x: 3, y: 0 }, thickness: 0.15 };

describe('createEmptyProject', () => {
  it('stamps the current schema version', () => {
    expect(createEmptyProject().schemaVersion).toBe(SCHEMA_VERSION);
  });

  it('starts with one empty ground-floor sketch', () => {
    const project = createEmptyProject();
    expect(project.sketches).toHaveLength(1);
    expect(project.sketches[0].walls).toHaveLength(0);
    expect(project.sketches[0].floorIndex).toBe(0);
  });

  it('gives every project and sketch a unique id', () => {
    const a = createEmptyProject();
    const b = createEmptyProject();
    expect(a.id).not.toBe(b.id);
    expect(a.sketches[0].id).not.toBe(b.sketches[0].id);
  });

  it('uses the provided name', () => {
    expect(createEmptyProject('My House').name).toBe('My House');
  });
});

describe('createEmptySketch', () => {
  it('defaults to floor 0', () => {
    expect(createEmptySketch('Attic').floorIndex).toBe(0);
  });

  it('accepts an explicit floor index', () => {
    expect(createEmptySketch('Basement', -1).floorIndex).toBe(-1);
  });
});

describe('withWallAdded', () => {
  it('appends the wall to the matching sketch only', () => {
    const project = createEmptyProject();
    const otherSketch = createEmptySketch('Second Floor', 1);
    project.sketches.push(otherSketch);

    const next = withWallAdded(project, project.sketches[0].id, WALL);

    expect(next.sketches[0].walls).toEqual([WALL]);
    expect(next.sketches[1].walls).toEqual([]);
    expect(project.sketches[0].walls).toEqual([]); // original is untouched
  });
});

describe('withLastWallRemoved', () => {
  it('drops only the most recently added wall', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const second: WallSegment = { ...WALL, id: 'w2' };
    const withTwoWalls = withWallAdded(withWallAdded(project, sketchId, WALL), sketchId, second);

    const next = withLastWallRemoved(withTwoWalls, sketchId);

    expect(next.sketches[0].walls).toEqual([WALL]);
  });

  it('is a no-op on a sketch with no walls', () => {
    const project = createEmptyProject();
    const next = withLastWallRemoved(project, project.sketches[0].id);
    expect(next.sketches[0].walls).toEqual([]);
  });
});
