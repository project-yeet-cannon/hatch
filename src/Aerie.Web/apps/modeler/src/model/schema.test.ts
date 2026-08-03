import { describe, expect, it } from 'vitest';
import {
  createEmptyProject,
  createEmptySketch,
  migrateProjectDocument,
  SCHEMA_VERSION,
  withDefaultWallThicknessSet,
  withRoomNamed,
  withVertexMoved,
  withWallAdded,
  withWallSplit,
  withWallsRemoved,
} from './schema';
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
    expect(project.sketches[0].roomLabels).toHaveLength(0);
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

describe('migrateProjectDocument', () => {
  it('passes a current-version document through unchanged', () => {
    const project = createEmptyProject('My House');
    expect(migrateProjectDocument(JSON.parse(JSON.stringify(project)))).toEqual(project);
  });

  it('backfills roomLabels on a version-1 document', () => {
    const legacy = {
      schemaVersion: 1,
      id: 'p1',
      name: 'Old House',
      createdAt: 'now',
      updatedAt: 'now',
      settings: { unit: 'm', defaultWallThickness: 0.15 },
      sketches: [{ id: 's1', name: 'Ground Floor', kind: 'floorPlan', floorIndex: 0, walls: [WALL], createdAt: 'now', updatedAt: 'now' }],
    };
    const migrated = migrateProjectDocument(legacy);
    expect(migrated.schemaVersion).toBe(2);
    expect(migrated.sketches[0].roomLabels).toEqual([]);
    expect(migrated.sketches[0].walls).toEqual([WALL]);
  });

  it('rejects a document from an unsupported future version', () => {
    expect(() => migrateProjectDocument({ ...createEmptyProject(), schemaVersion: 999 })).toThrow(/schema version/i);
  });

  it('rejects non-object input', () => {
    expect(() => migrateProjectDocument(null)).toThrow();
    expect(() => migrateProjectDocument(42)).toThrow();
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

describe('withWallsRemoved', () => {
  it('removes only the given wall ids', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const second: WallSegment = { ...WALL, id: 'w2' };
    const withTwoWalls = withWallAdded(withWallAdded(project, sketchId, WALL), sketchId, second);

    const next = withWallsRemoved(withTwoWalls, sketchId, [WALL.id]);

    expect(next.sketches[0].walls).toEqual([second]);
  });

  it('is a no-op for ids that are not present', () => {
    const project = withWallAdded(createEmptyProject(), createEmptyProject().sketches[0].id, WALL);
    const next = withWallsRemoved(project, project.sketches[0].id, ['nope']);
    expect(next.sketches[0].walls).toEqual(project.sketches[0].walls);
  });
});

describe('withWallSplit', () => {
  it('replaces the wall with two walls meeting at the split point', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const withWall = withWallAdded(project, sketchId, WALL);

    const next = withWallSplit(withWall, sketchId, WALL.id, { x: 1, y: 0 });

    expect(next.sketches[0].walls).toHaveLength(2);
    const [a, b] = next.sketches[0].walls;
    expect(a.start).toEqual(WALL.start);
    expect(a.end).toEqual({ x: 1, y: 0 });
    expect(b.start).toEqual({ x: 1, y: 0 });
    expect(b.end).toEqual(WALL.end);
    expect(a.thickness).toBe(WALL.thickness);
    expect(b.thickness).toBe(WALL.thickness);
    expect(a.id).not.toBe(b.id);
  });

  it('is a no-op for an unknown wall id', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const withWall = withWallAdded(project, sketchId, WALL);
    const next = withWallSplit(withWall, sketchId, 'missing', { x: 1, y: 0 });
    expect(next.sketches[0].walls).toEqual(withWall.sketches[0].walls);
  });
});

describe('withVertexMoved', () => {
  it('moves every wall endpoint at the given point, leaving others untouched', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const second: WallSegment = { id: 'w2', start: { x: 3, y: 0 }, end: { x: 3, y: 4 }, thickness: 0.15 };
    const withWalls = withWallAdded(withWallAdded(project, sketchId, WALL), sketchId, second);

    const next = withVertexMoved(withWalls, sketchId, { x: 3, y: 0 }, { x: 3, y: 1 });

    const [a, b] = next.sketches[0].walls;
    expect(a.end).toEqual({ x: 3, y: 1 });
    expect(a.start).toEqual({ x: 0, y: 0 });
    expect(b.start).toEqual({ x: 3, y: 1 });
    expect(b.end).toEqual({ x: 3, y: 4 });
  });
});

describe('withRoomNamed', () => {
  it('creates a new label when labelId is null', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const next = withRoomNamed(project, sketchId, null, { x: 1, y: 1 }, 'Kitchen');
    expect(next.sketches[0].roomLabels).toHaveLength(1);
    expect(next.sketches[0].roomLabels[0]).toMatchObject({ name: 'Kitchen', seed: { x: 1, y: 1 } });
  });

  it('renames an existing label by id without touching its seed', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const named = withRoomNamed(project, sketchId, null, { x: 1, y: 1 }, 'Kitchen');
    const labelId = named.sketches[0].roomLabels[0].id;

    const renamed = withRoomNamed(named, sketchId, labelId, { x: 1, y: 1 }, 'Pantry');

    expect(renamed.sketches[0].roomLabels).toHaveLength(1);
    expect(renamed.sketches[0].roomLabels[0]).toMatchObject({ id: labelId, name: 'Pantry', seed: { x: 1, y: 1 } });
  });
});

describe('withDefaultWallThicknessSet', () => {
  it('updates the project settings', () => {
    const next = withDefaultWallThicknessSet(createEmptyProject(), 0.2);
    expect(next.settings.defaultWallThickness).toBe(0.2);
  });
});
