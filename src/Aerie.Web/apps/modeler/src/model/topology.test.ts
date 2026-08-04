import { describe, expect, it } from 'vitest';
import { buildTopology } from './topology';
import { createEmptyProject, createId, withOpeningAdded, withRoomCeilingProfileSet, withRoomNamed, withWallAdded } from './schema';
import type { Point2, ProjectDocument, WallSegment } from './schema';

function wall(start: Point2, end: Point2, thickness = 0.15): WallSegment {
  return { id: createId(), start, end, thickness };
}

/** Two 4m x 3m rooms sharing a partition wall at x=4, with a door through it. */
function twoRoomProject(): { project: ProjectDocument; wallAB: WallSegment } {
  let project = createEmptyProject('Two Room House');
  const sketch = project.sketches[0];

  const wallAB = wall({ x: 4, y: 0 }, { x: 4, y: 3 });
  const walls = [
    wall({ x: 0, y: 0 }, { x: 4, y: 0 }),
    wallAB,
    wall({ x: 4, y: 3 }, { x: 0, y: 3 }),
    wall({ x: 0, y: 3 }, { x: 0, y: 0 }),
    wall({ x: 4, y: 0 }, { x: 8, y: 0 }),
    wall({ x: 8, y: 0 }, { x: 8, y: 3 }),
    wall({ x: 8, y: 3 }, { x: 4, y: 3 }),
  ];
  for (const w of walls) project = withWallAdded(project, sketch.id, w);

  project = withRoomNamed(project, sketch.id, null, { x: 2, y: 1.5 }, 'Living Room');
  project = withRoomNamed(project, sketch.id, null, { x: 6, y: 1.5 }, 'Kitchen');
  for (const label of project.sketches[0].roomLabels) {
    project = withRoomCeilingProfileSet(project, sketch.id, label.id, label.seed, { kind: 'flat', wallHeight: 2.5 });
  }

  project = withOpeningAdded(project, sketch.id, { id: createId(), wallId: wallAB.id, offset: 1, width: 0.9, headHeight: 2.0, kind: 'door' });

  return { project, wallAB };
}

describe('buildTopology', () => {
  it('lists both rooms as nodes with floor area, volume, and ceiling profile', () => {
    const { project } = twoRoomProject();
    const topology = buildTopology(project);

    expect(topology.rooms).toHaveLength(2);
    const names = topology.rooms.map((r) => r.name).sort();
    expect(names).toEqual(['Kitchen', 'Living Room']);
    for (const room of topology.rooms) {
      expect(room.floorArea).toBeCloseTo(12, 6);
      expect(room.volume).toBeCloseTo(30, 6);
      expect(room.ceilingProfile).toEqual({ kind: 'flat', eaveHeight: 2.5, ridgeHeight: undefined });
      expect(room.floorIndex).toBe(0);
      expect(room.stairwellVoid).toBe(false);
    }
  });

  it('connects the door opening to both rooms it borders', () => {
    const { project } = twoRoomProject();
    const topology = buildTopology(project);

    expect(topology.openings).toHaveLength(1);
    const opening = topology.openings[0];
    expect(opening.kind).toBe('door');
    expect(opening.sillHeight).toBe(0);
    expect(opening.headHeight).toBe(2.0);
    expect(opening.freeArea).toBeCloseTo(0.9 * 2.0, 6);
    expect([opening.roomA, opening.roomB].sort()).toEqual(
      topology.rooms
        .map((r) => r.id)
        .slice()
        .sort(),
    );
  });

  it('reserves an empty sensors array', () => {
    const { project } = twoRoomProject();
    const topology = buildTopology(project);
    expect(topology.sensors).toEqual([]);
  });
});
