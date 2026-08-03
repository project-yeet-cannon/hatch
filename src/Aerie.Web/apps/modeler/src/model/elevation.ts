import { DEFAULT_CEILING_PROFILE } from './schema';
import type { CeilingProfile, ElevationBinding, ProjectDocument, RoomLabel, Sketch } from './schema';
import { solveSketch } from './solver';

export type CeilingHeightSource = 'default' | 'manual' | 'elevation';

export interface ResolvedCeilingProfile extends CeilingProfile {
  wallHeightSource: CeilingHeightSource;
  ridgeHeightSource: CeilingHeightSource;
  /** The elevation sketch driving wallHeight and/or ridgeHeight, if either came from one. */
  boundSketchNames: string[];
}

/** Finds a room label by id across every sketch in the project (elevation bindings target labels by id alone, since an elevation can bind rooms on any floor). */
export function findRoomLabel(project: ProjectDocument, roomLabelId: string): RoomLabel | undefined {
  for (const sketch of project.sketches) {
    const label = sketch.roomLabels.find((l) => l.id === roomLabelId);
    if (label) return label;
  }
  return undefined;
}

function bindingsForRoom(project: ProjectDocument, roomLabelId: string): { sketch: Sketch; binding: ElevationBinding }[] {
  const matches: { sketch: Sketch; binding: ElevationBinding }[] = [];
  for (const sketch of project.sketches) {
    if (sketch.kind !== 'elevation') continue;
    for (const binding of sketch.elevationBindings) {
      if (binding.roomLabelId === roomLabelId) matches.push({ sketch, binding });
    }
  }
  return matches;
}

/**
 * Resolves the effective ceiling profile for a room: the manually-entered
 * profile (or DEFAULT_CEILING_PROFILE if never set), with wallHeight and/or
 * ridgeHeight overridden by whichever elevation sketch has bound that field
 * to this room, using that sketch's currently-solved dimension (see
 * model/solver.ts). If more than one elevation binds the same field, the
 * first one found wins - the UI prevents creating a second binding for a
 * field that's already bound (see components/ElevationEditor.tsx).
 */
export function resolveCeilingProfile(project: ProjectDocument, roomLabelId: string): ResolvedCeilingProfile {
  const label = findRoomLabel(project, roomLabelId);
  const base = label?.ceilingProfile ?? DEFAULT_CEILING_PROFILE;
  const resolved: ResolvedCeilingProfile = {
    ...base,
    wallHeightSource: label?.ceilingProfile ? 'manual' : 'default',
    ridgeHeightSource: label?.ceilingProfile?.ridgeHeight !== undefined ? 'manual' : 'default',
    boundSketchNames: [],
  };

  for (const { sketch, binding } of bindingsForRoom(project, roomLabelId)) {
    const solved = solveSketch(sketch.walls).lengths.get(binding.wallId);
    if (solved === undefined) continue;
    if (binding.target === 'wallHeight') {
      resolved.wallHeight = solved;
      resolved.wallHeightSource = 'elevation';
    } else {
      resolved.ridgeHeight = solved;
      resolved.ridgeHeightSource = 'elevation';
    }
    resolved.boundSketchNames.push(sketch.name);
  }

  return resolved;
}
