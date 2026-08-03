// The persisted shape of a project: what's saved to IndexedDB on every
// mutation and what an exported .aeriemodel.json file contains. Bump
// SCHEMA_VERSION whenever this shape changes in a way old files can't be
// read as-is, and add a branch to migrateProjectDocument below to upgrade
// them rather than rejecting them.
export const SCHEMA_VERSION = 3;

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
  /** User-entered length in meters, if this edge has been measured (see model/solver.ts). Absent means unmeasured. */
  measuredLength?: number;
}

// Rooms themselves are derived at runtime from closed loops in a sketch's
// wall graph (see model/roomDetection.ts) rather than stored directly - but
// a user-given name needs to survive re-derivation as walls are edited. Each
// label carries a `seed` point that was inside the room at naming time; a
// freshly detected room is matched back to its label by testing which
// room's polygon still contains that seed (point-in-polygon).
export interface RoomLabel {
  id: string;
  name: string;
  seed: Point2;
}

export type SketchKind = 'floorPlan' | 'elevation';

export interface Sketch {
  id: string;
  name: string;
  kind: SketchKind;
  /** floorPlan sketches sharing a floorIndex are candidates for merging (step 4). */
  floorIndex: number;
  walls: WallSegment[];
  roomLabels: RoomLabel[];
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
    roomLabels: [],
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

/**
 * Upgrades a raw decoded document (from IndexedDB or an imported file) from
 * any earlier schema version to the current one. Throws if the document's
 * version is newer than this build understands, or isn't a project at all.
 */
export function migrateProjectDocument(raw: unknown): ProjectDocument {
  if (typeof raw !== 'object' || raw === null) {
    throw new Error('Project document is not an object.');
  }

  const doc = raw as Record<string, unknown>;
  let version = typeof doc.schemaVersion === 'number' ? doc.schemaVersion : undefined;
  let sketches = Array.isArray(doc.sketches) ? (doc.sketches as Record<string, unknown>[]) : [];

  if (version === 1) {
    sketches = sketches.map((sketch) => ({ ...sketch, roomLabels: sketch.roomLabels ?? [] }));
    version = 2;
  }

  if (version === 2) {
    // Walls gained an optional measuredLength; existing walls are valid as-is with it absent.
    version = 3;
  }

  if (version !== SCHEMA_VERSION) {
    throw new Error(`Unsupported schema version ${String(doc.schemaVersion)} (this build reads version ${SCHEMA_VERSION}).`);
  }

  return { ...doc, schemaVersion: version, sketches } as unknown as ProjectDocument;
}

function updateSketch(project: ProjectDocument, sketchId: string, update: (sketch: Sketch) => Sketch): ProjectDocument {
  return {
    ...project,
    sketches: project.sketches.map((sketch) => (sketch.id === sketchId ? update(sketch) : sketch)),
  };
}

function pointsEqual(a: Point2, b: Point2, epsilon = 1e-4): boolean {
  return Math.abs(a.x - b.x) < epsilon && Math.abs(a.y - b.y) < epsilon;
}

export function withWallAdded(project: ProjectDocument, sketchId: string, wall: WallSegment): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({ ...sketch, walls: [...sketch.walls, wall], updatedAt: now }));
}

export function withWallsRemoved(project: ProjectDocument, sketchId: string, wallIds: readonly string[]): ProjectDocument {
  const now = new Date().toISOString();
  const idSet = new Set(wallIds);
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    walls: sketch.walls.filter((wall) => !idSet.has(wall.id)),
    updatedAt: now,
  }));
}

/** Splits an existing wall into two at `at` (e.g. where a new wall's endpoint lands mid-span), so the point becomes a shared graph vertex. */
export function withWallSplit(project: ProjectDocument, sketchId: string, wallId: string, at: Point2): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => {
    const index = sketch.walls.findIndex((wall) => wall.id === wallId);
    if (index === -1) return sketch;
    const wall = sketch.walls[index];
    const before: WallSegment = { id: createId(), start: wall.start, end: at, thickness: wall.thickness };
    const after: WallSegment = { id: createId(), start: at, end: wall.end, thickness: wall.thickness };
    const walls = [...sketch.walls];
    walls.splice(index, 1, before, after);
    return { ...sketch, walls, updatedAt: now };
  });
}

/** Moves every wall endpoint currently at `from` to `to` together, so dragging a shared vertex keeps the walls meeting there connected. */
export function withVertexMoved(project: ProjectDocument, sketchId: string, from: Point2, to: Point2): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    walls: sketch.walls.map((wall) => ({
      ...wall,
      start: pointsEqual(wall.start, from) ? to : wall.start,
      end: pointsEqual(wall.end, from) ? to : wall.end,
    })),
    updatedAt: now,
  }));
}

/** Sets (or, with `null`, clears) a wall's user-entered length, the input to the dimension solver (model/solver.ts). */
export function withWallLengthSet(project: ProjectDocument, sketchId: string, wallId: string, measuredLength: number | null): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    walls: sketch.walls.map((wall) => {
      if (wall.id !== wallId) return wall;
      if (measuredLength === null) {
        const { measuredLength: _drop, ...rest } = wall;
        return rest;
      }
      return { ...wall, measuredLength };
    }),
    updatedAt: now,
  }));
}

/** Overwrites wall start/end points with solved positions (see model/solver.ts), keyed by wall id; walls not present in `solvedWalls` are untouched. */
export function withWallPositionsUpdated(project: ProjectDocument, sketchId: string, solvedWalls: readonly WallSegment[]): ProjectDocument {
  const now = new Date().toISOString();
  const byId = new Map(solvedWalls.map((wall) => [wall.id, wall]));
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    walls: sketch.walls.map((wall) => {
      const solved = byId.get(wall.id);
      return solved ? { ...wall, start: solved.start, end: solved.end } : wall;
    }),
    updatedAt: now,
  }));
}

/** Renames an existing room label (`labelId` set) or creates a new one for a just-named, previously-unlabeled room. */
export function withRoomNamed(project: ProjectDocument, sketchId: string, labelId: string | null, seed: Point2, name: string): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => {
    if (labelId) {
      return {
        ...sketch,
        roomLabels: sketch.roomLabels.map((label) => (label.id === labelId ? { ...label, name } : label)),
        updatedAt: now,
      };
    }
    const label: RoomLabel = { id: createId(), name, seed };
    return { ...sketch, roomLabels: [...sketch.roomLabels, label], updatedAt: now };
  });
}

export function withDefaultWallThicknessSet(project: ProjectDocument, thickness: number): ProjectDocument {
  return { ...project, settings: { ...project.settings, defaultWallThickness: thickness } };
}
