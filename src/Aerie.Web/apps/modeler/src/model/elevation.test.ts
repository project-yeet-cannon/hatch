import { describe, expect, it } from 'vitest';
import { findRoomLabel, resolveCeilingProfile } from './elevation';
import {
  createEmptyProject,
  DEFAULT_CEILING_PROFILE,
  withElevationBindingSet,
  withRoomCeilingProfileSet,
  withRoomNamed,
  withSketchAdded,
  withWallAdded,
  withWallLengthSet,
} from './schema';

function projectWithNamedRoom() {
  const project = createEmptyProject();
  const sketchId = project.sketches[0].id;
  const named = withRoomNamed(project, sketchId, null, { x: 1, y: 1 }, 'Kitchen');
  const roomLabelId = named.sketches[0].roomLabels[0].id;
  return { project: named, sketchId, roomLabelId };
}

describe('findRoomLabel', () => {
  it('finds a label across any sketch by id', () => {
    const { project, roomLabelId } = projectWithNamedRoom();
    expect(findRoomLabel(project, roomLabelId)?.name).toBe('Kitchen');
  });

  it('returns undefined for an unknown id', () => {
    expect(findRoomLabel(createEmptyProject(), 'nope')).toBeUndefined();
  });
});

describe('resolveCeilingProfile', () => {
  it('falls back to the default profile for an unlabeled room', () => {
    const project = createEmptyProject();
    const resolved = resolveCeilingProfile(project, 'nope');
    expect(resolved).toMatchObject({ ...DEFAULT_CEILING_PROFILE, wallHeightSource: 'default', ridgeHeightSource: 'default', boundSketchNames: [] });
  });

  it('reports a manually-set profile as manual', () => {
    const { project, sketchId, roomLabelId } = projectWithNamedRoom();
    const withProfile = withRoomCeilingProfileSet(project, sketchId, roomLabelId, { x: 1, y: 1 }, { kind: 'gable', wallHeight: 2.5, ridgeHeight: 4 });
    const resolved = resolveCeilingProfile(withProfile, roomLabelId);
    expect(resolved).toMatchObject({ kind: 'gable', wallHeight: 2.5, ridgeHeight: 4, wallHeightSource: 'manual', ridgeHeightSource: 'manual' });
  });

  it('overrides wallHeight from a bound, solved elevation sketch', () => {
    const { project, roomLabelId } = projectWithNamedRoom();
    const { project: withElevation, sketchId: elevationSketchId } = withSketchAdded(project, 'North Elevation', 0, 'elevation');
    const withWall = withWallAdded(withElevation, elevationSketchId, { id: 'e1', start: { x: 0, y: 0 }, end: { x: 0, y: 2.6 }, thickness: 0.02 });
    const withMeasurement = withWallLengthSet(withWall, elevationSketchId, 'e1', 2.6);
    const withBinding = withElevationBindingSet(withMeasurement, elevationSketchId, 'e1', roomLabelId, 'wallHeight');

    const resolved = resolveCeilingProfile(withBinding, roomLabelId);
    expect(resolved.wallHeight).toBeCloseTo(2.6, 2);
    expect(resolved.wallHeightSource).toBe('elevation');
    expect(resolved.boundSketchNames).toEqual(['North Elevation']);
  });

  it('leaves ridgeHeight on the manual/default source when only wallHeight is bound', () => {
    const { project, roomLabelId } = projectWithNamedRoom();
    const { project: withElevation, sketchId: elevationSketchId } = withSketchAdded(project, 'North Elevation', 0, 'elevation');
    const withWall = withWallAdded(withElevation, elevationSketchId, { id: 'e1', start: { x: 0, y: 0 }, end: { x: 0, y: 2.6 }, thickness: 0.02 });
    const withBinding = withElevationBindingSet(withWall, elevationSketchId, 'e1', roomLabelId, 'wallHeight');

    const resolved = resolveCeilingProfile(withBinding, roomLabelId);
    expect(resolved.ridgeHeightSource).toBe('default');
  });
});
