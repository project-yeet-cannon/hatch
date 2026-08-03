// The persisted shape of a project: what's saved to IndexedDB on every
// mutation and what an exported .aeriemodel.json file contains. Bump
// SCHEMA_VERSION whenever this shape changes in a way old files can't be
// read as-is, and teach projectFile.ts how to migrate forward from it.
export const SCHEMA_VERSION = 1;

export interface Point2 {
  x: number;
  y: number;
}

export interface WallSegment {
  id: string;
  start: Point2;
  end: Point2;
  /** Meters, centered on the start-end centerline. */
  thickness: number;
}

export type SketchKind = 'floorPlan' | 'elevation';

export interface Sketch {
  id: string;
  name: string;
  kind: SketchKind;
  /** floorPlan sketches sharing a floorIndex are candidates for merging (step 4). */
  floorIndex: number;
  walls: WallSegment[];
  createdAt: string;
  updatedAt: string;
}

export interface ProjectSettings {
  unit: 'm' | 'ft';
  defaultWallThickness: number;
}

export interface ProjectDocument {
  schemaVersion: typeof SCHEMA_VERSION;
  id: string;
  name: string;
  createdAt: string;
  updatedAt: string;
  settings: ProjectSettings;
  sketches: Sketch[];
}

export function createId(): string {
  return crypto.randomUUID();
}

export function createEmptySketch(name: string, floorIndex = 0): Sketch {
  const now = new Date().toISOString();
  return {
    id: createId(),
    name,
    kind: 'floorPlan',
    floorIndex,
    walls: [],
    createdAt: now,
    updatedAt: now,
  };
}

export function createEmptyProject(name = 'Untitled Home'): ProjectDocument {
  const now = new Date().toISOString();
  return {
    schemaVersion: SCHEMA_VERSION,
    id: createId(),
    name,
    createdAt: now,
    updatedAt: now,
    settings: {
      unit: 'm',
      defaultWallThickness: 0.15,
    },
    sketches: [createEmptySketch('Ground Floor', 0)],
  };
}

function updateSketch(project: ProjectDocument, sketchId: string, update: (sketch: Sketch) => Sketch): ProjectDocument {
  return {
    ...project,
    sketches: project.sketches.map((sketch) => (sketch.id === sketchId ? update(sketch) : sketch)),
  };
}

// The real floor-plan editor (drawing, snapping, room detection) is step 2 of
// the plan. These two mutators exist so step 1's persistence spine has
// something real to autosave/undo/export - App.tsx uses them from a
// placeholder "Add wall" button until the editor lands.
export function withWallAdded(project: ProjectDocument, sketchId: string, wall: WallSegment): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({ ...sketch, walls: [...sketch.walls, wall], updatedAt: now }));
}

export function withLastWallRemoved(project: ProjectDocument, sketchId: string): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({ ...sketch, walls: sketch.walls.slice(0, -1), updatedAt: now }));
}
