import { describe, expect, it } from 'vitest';
import {
  createEmptyProject,
  createEmptySketch,
  migrateProjectDocument,
  SCHEMA_VERSION,
  withDefaultWallThicknessSet,
  withElevationBindingRemoved,
  withElevationBindingSet,
  withOpeningAdded,
  withOpeningRemoved,
  withOpeningsForWallsRemoved,
  withOpeningUpdated,
  withRoomCeilingProfileSet,
  withRoomNamed,
  withRoomStairwellVoidSet,
  withSketchAdded,
  withSketchesMerged,
  withSketchRemoved,
  withSketchRenamed,
  withVertexMoved,
  withWallAdded,
  withWallLengthSet,
  withWallPositionsUpdated,
  withWallSplit,
  withWallsRemoved,
} from './schema';
import type { Opening, WallSegment } from './schema';

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
    expect(migrated.schemaVersion).toBe(SCHEMA_VERSION);
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

describe('openings', () => {
  const OPENING: Opening = { id: 'o1', wallId: 'w1', offset: 1, width: 0.9, headHeight: 2, kind: 'door' };

  it('withOpeningAdded appends to the matching sketch only', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const next = withOpeningAdded(project, sketchId, OPENING);
    expect(next.sketches[0].openings).toEqual([OPENING]);
    expect(project.sketches[0].openings).toEqual([]);
  });

  it('withOpeningUpdated patches only the named fields', () => {
    const base = createEmptyProject();
    const project = withOpeningAdded(base, base.sketches[0].id, OPENING);
    const sketchId = project.sketches[0].id;
    const next = withOpeningUpdated(project, sketchId, OPENING.id, { width: 1.2 });
    expect(next.sketches[0].openings[0]).toMatchObject({ ...OPENING, width: 1.2 });
  });

  it('withOpeningRemoved removes only the given opening', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const withTwo = withOpeningAdded(withOpeningAdded(project, sketchId, OPENING), sketchId, { ...OPENING, id: 'o2' });
    const next = withOpeningRemoved(withTwo, sketchId, 'o2');
    expect(next.sketches[0].openings).toEqual([OPENING]);
  });

  it('withOpeningsForWallsRemoved drops openings anchored to the given walls', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const withTwo = withOpeningAdded(withOpeningAdded(project, sketchId, OPENING), sketchId, { ...OPENING, id: 'o2', wallId: 'w2' });
    const next = withOpeningsForWallsRemoved(withTwo, sketchId, ['w1']);
    expect(next.sketches[0].openings).toEqual([{ ...OPENING, id: 'o2', wallId: 'w2' }]);
  });
});

describe('room ceiling profile / stairwell void', () => {
  it('withRoomCeilingProfileSet creates a label with no name when none exists yet', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const next = withRoomCeilingProfileSet(project, sketchId, null, { x: 1, y: 1 }, { kind: 'gable', wallHeight: 2.4, ridgeHeight: 4 });
    expect(next.sketches[0].roomLabels).toHaveLength(1);
    expect(next.sketches[0].roomLabels[0]).toMatchObject({ ceilingProfile: { kind: 'gable', wallHeight: 2.4, ridgeHeight: 4 } });
  });

  it('withRoomCeilingProfileSet patches an existing label without touching its name', () => {
    const base = createEmptyProject();
    const project = withRoomNamed(base, base.sketches[0].id, null, { x: 1, y: 1 }, 'Kitchen');
    const sketchId = project.sketches[0].id;
    const labelId = project.sketches[0].roomLabels[0].id;
    const next = withRoomCeilingProfileSet(project, sketchId, labelId, { x: 1, y: 1 }, { kind: 'flat', wallHeight: 2.7 });
    expect(next.sketches[0].roomLabels[0]).toMatchObject({ name: 'Kitchen', ceilingProfile: { kind: 'flat', wallHeight: 2.7 } });
  });

  it('withRoomStairwellVoidSet toggles the flag on an existing label', () => {
    const base = createEmptyProject();
    const project = withRoomNamed(base, base.sketches[0].id, null, { x: 1, y: 1 }, 'Stairs');
    const sketchId = project.sketches[0].id;
    const labelId = project.sketches[0].roomLabels[0].id;
    const next = withRoomStairwellVoidSet(project, sketchId, labelId, { x: 1, y: 1 }, true);
    expect(next.sketches[0].roomLabels[0].stairwellVoid).toBe(true);
  });
});

describe('sketch management', () => {
  it('withSketchAdded appends a new sketch and returns its id', () => {
    const project = createEmptyProject();
    const { project: next, sketchId } = withSketchAdded(project, 'Second Floor', 1);
    expect(next.sketches).toHaveLength(2);
    expect(next.sketches[1]).toMatchObject({ id: sketchId, name: 'Second Floor', floorIndex: 1, kind: 'floorPlan' });
  });

  it('withSketchAdded can create an elevation sketch', () => {
    const { project } = withSketchAdded(createEmptyProject(), 'East Elevation', 0, 'elevation');
    expect(project.sketches[1].kind).toBe('elevation');
  });

  it('withSketchRenamed renames only the target sketch', () => {
    const { project } = withSketchAdded(createEmptyProject(), 'Second Floor', 1);
    const next = withSketchRenamed(project, project.sketches[1].id, 'Attic');
    expect(next.sketches[1].name).toBe('Attic');
    expect(next.sketches[0].name).toBe(project.sketches[0].name);
  });

  it('withSketchRemoved drops only the target sketch', () => {
    const { project } = withSketchAdded(createEmptyProject(), 'Second Floor', 1);
    const next = withSketchRemoved(project, project.sketches[1].id);
    expect(next.sketches).toHaveLength(1);
  });
});

describe('withSketchesMerged', () => {
  it('transforms the source sketch into the target frame and appends its walls', () => {
    const project = createEmptyProject();
    const targetId = project.sketches[0].id;
    const withTargetWall = withWallAdded(project, targetId, { id: 'ta', start: { x: 0, y: 0 }, end: { x: 4, y: 0 }, thickness: 0.15 });

    const { project: withSource, sketchId: sourceId } = withSketchAdded(withTargetWall, 'Partial', 0);
    // Source sketch drawn in its own local frame, offset and rotated 90deg from the target's.
    const withSourceWall = withWallAdded(withSource, sourceId, { id: 'sa', start: { x: 10, y: 10 }, end: { x: 10, y: 14 }, thickness: 0.15 });

    // Corresponds target's (4,0) <-> source's (10,10), and target's (0,0) <-> source's (10,14):
    // a 90deg rotation plus translation maps the source frame onto the target frame.
    const merged = withSketchesMerged(withSourceWall, targetId, sourceId, [
      { source: { x: 10, y: 10 }, target: { x: 4, y: 0 } },
      { source: { x: 10, y: 14 }, target: { x: 0, y: 0 } },
    ]);

    expect(merged.sketches).toHaveLength(1);
    expect(merged.sketches[0].walls).toHaveLength(2);
    const mergedWall = merged.sketches[0].walls.find((w) => w.id === 'sa')!;
    expect(mergedWall.start.x).toBeCloseTo(4, 5);
    expect(mergedWall.start.y).toBeCloseTo(0, 5);
    expect(mergedWall.end.x).toBeCloseTo(0, 5);
    expect(mergedWall.end.y).toBeCloseTo(0, 5);
  });

  it('is a no-op when given no correspondences', () => {
    const project = createEmptyProject();
    const { project: withSource, sketchId: sourceId } = withSketchAdded(project, 'Partial', 0);
    const next = withSketchesMerged(withSource, project.sketches[0].id, sourceId, []);
    expect(next).toBe(withSource);
  });
});

describe('elevation bindings', () => {
  it('withElevationBindingSet adds a binding and replaces any existing one for the same wall', () => {
    const { project } = withSketchAdded(createEmptyProject(), 'East Elevation', 0, 'elevation');
    const sketchId = project.sketches[1].id;
    const first = withElevationBindingSet(project, sketchId, 'w1', 'room1', 'wallHeight');
    expect(first.sketches[1].elevationBindings).toHaveLength(1);

    const replaced = withElevationBindingSet(first, sketchId, 'w1', 'room2', 'ridgeHeight');
    expect(replaced.sketches[1].elevationBindings).toHaveLength(1);
    expect(replaced.sketches[1].elevationBindings[0]).toMatchObject({ wallId: 'w1', roomLabelId: 'room2', target: 'ridgeHeight' });
  });

  it('withElevationBindingRemoved removes the binding for the given wall', () => {
    const { project } = withSketchAdded(createEmptyProject(), 'East Elevation', 0, 'elevation');
    const sketchId = project.sketches[1].id;
    const withBinding = withElevationBindingSet(project, sketchId, 'w1', 'room1', 'wallHeight');
    const next = withElevationBindingRemoved(withBinding, sketchId, 'w1');
    expect(next.sketches[1].elevationBindings).toEqual([]);
  });
});

describe('withWallLengthSet', () => {
  it('sets a measured length on the target wall only', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const second: WallSegment = { ...WALL, id: 'w2' };
    const withTwoWalls = withWallAdded(withWallAdded(project, sketchId, WALL), sketchId, second);

    const next = withWallLengthSet(withTwoWalls, sketchId, WALL.id, 3.2);

    expect(next.sketches[0].walls[0].measuredLength).toBe(3.2);
    expect(next.sketches[0].walls[1].measuredLength).toBeUndefined();
  });

  it('clears a measured length when passed null', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const measured = withWallLengthSet(withWallAdded(project, sketchId, WALL), sketchId, WALL.id, 3.2);

    const cleared = withWallLengthSet(measured, sketchId, WALL.id, null);

    expect(cleared.sketches[0].walls[0].measuredLength).toBeUndefined();
  });
});

describe('withWallPositionsUpdated', () => {
  it('overwrites start/end for walls present in the solved set, leaving others alone', () => {
    const project = createEmptyProject();
    const sketchId = project.sketches[0].id;
    const second: WallSegment = { id: 'w2', start: { x: 3, y: 0 }, end: { x: 3, y: 4 }, thickness: 0.15 };
    const withTwoWalls = withWallAdded(withWallAdded(project, sketchId, WALL), sketchId, second);

    const next = withWallPositionsUpdated(withTwoWalls, sketchId, [{ ...WALL, end: { x: 3.1, y: 0 } }]);

    expect(next.sketches[0].walls[0].end).toEqual({ x: 3.1, y: 0 });
    expect(next.sketches[0].walls[1]).toEqual(second);
  });
});
