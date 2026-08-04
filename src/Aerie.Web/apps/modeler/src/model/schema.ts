import { applyRigidTransform2D, fitRigidTransform2D } from './geometry';

// The persisted shape of a project: what's saved to IndexedDB on every
// mutation and what an exported .aeriemodel.json file contains. Bump
// SCHEMA_VERSION whenever this shape changes in a way old files can't be
// read as-is, and add a branch to migrateProjectDocument below to upgrade
// them rather than rejecting them.
export const SCHEMA_VERSION = 4;

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

export type OpeningKind = 'door' | 'archway';

/** A door or archway cut into a floorPlan wall. Windows are deferred (see TODO_MODELING.md). */
export interface Opening {
  id: string;
  wallId: string;
  /** Meters from wall.start along the centerline to the opening's midpoint. */
  offset: number;
  /** Meters. */
  width: number;
  /** Meters above the floor to the top of the opening. */
  headHeight: number;
  kind: OpeningKind;
}

export type CeilingProfileKind = 'flat' | 'shed' | 'gable';

/** A room's vertical shape: a flat ceiling at wallHeight, or a shed/gable roof rising from wallHeight (the eave line) to ridgeHeight. */
export interface CeilingProfile {
  kind: CeilingProfileKind;
  /** Meters above the room's floor, to the eave line (flat ceilings only have this). */
  wallHeight: number;
  /** Meters above the room's floor, to the ridge. Only meaningful for 'shed'/'gable'. */
  ridgeHeight?: number;
}

export const DEFAULT_CEILING_PROFILE: CeilingProfile = { kind: 'flat', wallHeight: 2.4 };

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
  /** Manually-entered ceiling shape; may be overridden per-field by an elevation binding (see model/elevation.ts). Absent means DEFAULT_CEILING_PROFILE. */
  ceilingProfile?: CeilingProfile;
  /** Marks this room as an open shaft (stairwell, double-height void) rather than a floor that carries a ceiling into the room above. */
  stairwellVoid?: boolean;
}

/**
 * Binds a dimension line drawn in an elevation sketch to a specific room's
 * ceiling height, so a measurement taken from a side view (e.g. "eave to
 * ridge") drives the 3D model instead of a manually-typed number. See
 * model/elevation.ts for how bindings are resolved against a solved sketch.
 */
export interface ElevationBinding {
  id: string;
  wallId: string;
  roomLabelId: string;
  target: 'wallHeight' | 'ridgeHeight';
}

export type SketchKind = 'floorPlan' | 'elevation';

export interface Sketch {
  id: string;
  name: string;
  kind: SketchKind;
  /** floorPlan sketches sharing a floorIndex are candidates for merging (step 4); an elevation sketch's floorIndex is the floor it primarily elevates. */
  floorIndex: number;
  walls: WallSegment[];
  roomLabels: RoomLabel[];
  /** floorPlan sketches only. */
  openings: Opening[];
  /** elevation sketches only; targets rooms by RoomLabel id, which may live in any sketch. */
  elevationBindings: ElevationBinding[];
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

/** Human-readable label for a floorIndex (0 = ground floor, negative = basement), shared by the sidebar and 3D generation's per-floor naming. */
export function floorLabel(floorIndex: number): string {
  if (floorIndex === 0) return 'Ground floor';
  return floorIndex > 0 ? `Floor ${floorIndex}` : `Basement ${Math.abs(floorIndex)}`;
}

export function createEmptySketch(name: string, floorIndex = 0, kind: SketchKind = 'floorPlan'): Sketch {
  const now = new Date().toISOString();
  return {
    id: createId(),
    name,
    kind,
    floorIndex,
    walls: [],
    roomLabels: [],
    openings: [],
    elevationBindings: [],
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

  if (version === 3) {
    // Sketches gained openings + elevationBindings arrays; rooms gained optional ceilingProfile/stairwellVoid.
    sketches = sketches.map((sketch) => ({ ...sketch, openings: sketch.openings ?? [], elevationBindings: sketch.elevationBindings ?? [] }));
    version = 4;
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

/** Patches an existing room label (`labelId` set) or creates a new one seeded at `seed`, carrying `defaults` merged under `patch`. Shared by every room-label setter below so a not-yet-named room can still receive a ceiling profile or void flag. */
function updateRoomLabel(
  project: ProjectDocument,
  sketchId: string,
  labelId: string | null,
  seed: Point2,
  defaults: Pick<RoomLabel, 'name'>,
  patch: Partial<Omit<RoomLabel, 'id' | 'seed'>>,
): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => {
    if (labelId) {
      return {
        ...sketch,
        roomLabels: sketch.roomLabels.map((label) => (label.id === labelId ? { ...label, ...patch } : label)),
        updatedAt: now,
      };
    }
    const label: RoomLabel = { id: createId(), seed, ...defaults, ...patch };
    return { ...sketch, roomLabels: [...sketch.roomLabels, label], updatedAt: now };
  });
}

/** Renames an existing room label (`labelId` set) or creates a new one for a just-named, previously-unlabeled room. */
export function withRoomNamed(project: ProjectDocument, sketchId: string, labelId: string | null, seed: Point2, name: string): ProjectDocument {
  return updateRoomLabel(project, sketchId, labelId, seed, { name }, { name });
}

/** Sets a room's ceiling shape. `labelId` null creates an unnamed label (e.g. the user opened the room panel before naming it). */
export function withRoomCeilingProfileSet(
  project: ProjectDocument,
  sketchId: string,
  labelId: string | null,
  seed: Point2,
  ceilingProfile: CeilingProfile,
): ProjectDocument {
  return updateRoomLabel(project, sketchId, labelId, seed, { name: '' }, { ceilingProfile });
}

/** Marks/unmarks a room as an open shaft (stairwell, double-height void) that doesn't carry a ceiling into the floor above. */
export function withRoomStairwellVoidSet(project: ProjectDocument, sketchId: string, labelId: string | null, seed: Point2, stairwellVoid: boolean): ProjectDocument {
  return updateRoomLabel(project, sketchId, labelId, seed, { name: '' }, { stairwellVoid });
}

export function withDefaultWallThicknessSet(project: ProjectDocument, thickness: number): ProjectDocument {
  return { ...project, settings: { ...project.settings, defaultWallThickness: thickness } };
}

/** Adds a door/archway opening to a floorPlan sketch's wall. */
export function withOpeningAdded(project: ProjectDocument, sketchId: string, opening: Opening): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({ ...sketch, openings: [...sketch.openings, opening], updatedAt: now }));
}

export function withOpeningRemoved(project: ProjectDocument, sketchId: string, openingId: string): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    openings: sketch.openings.filter((opening) => opening.id !== openingId),
    updatedAt: now,
  }));
}

export function withOpeningUpdated(project: ProjectDocument, sketchId: string, openingId: string, patch: Partial<Pick<Opening, 'offset' | 'width' | 'headHeight' | 'kind'>>): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    openings: sketch.openings.map((opening) => (opening.id === openingId ? { ...opening, ...patch } : opening)),
    updatedAt: now,
  }));
}

/** Removes any opening left dangling by a wall deletion (e.g. via withWallsRemoved). Call after removing walls. */
export function withOpeningsForWallsRemoved(project: ProjectDocument, sketchId: string, wallIds: readonly string[]): ProjectDocument {
  const idSet = new Set(wallIds);
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    openings: sketch.openings.filter((opening) => !idSet.has(opening.wallId)),
    updatedAt: now,
  }));
}

/** Creates and appends a new sketch (a new floor's plan, another partial sketch of an existing floor, or an elevation), returning the updated project and the new sketch's id. */
export function withSketchAdded(project: ProjectDocument, name: string, floorIndex: number, kind: SketchKind = 'floorPlan'): { project: ProjectDocument; sketchId: string } {
  const sketch = createEmptySketch(name, floorIndex, kind);
  return { project: { ...project, sketches: [...project.sketches, sketch] }, sketchId: sketch.id };
}

export function withSketchRenamed(project: ProjectDocument, sketchId: string, name: string): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({ ...sketch, name, updatedAt: now }));
}

export function withSketchRemoved(project: ProjectDocument, sketchId: string): ProjectDocument {
  return { ...project, sketches: project.sketches.filter((sketch) => sketch.id !== sketchId) };
}

/** Sets (or, with `null`, clears) an elevation sketch's binding of `wallId`'s solved length to a room's ceiling height (see model/elevation.ts). */
export function withElevationBindingSet(project: ProjectDocument, sketchId: string, wallId: string, roomLabelId: string, target: ElevationBinding['target']): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => {
    const withoutExisting = sketch.elevationBindings.filter((binding) => binding.wallId !== wallId);
    const binding: ElevationBinding = { id: createId(), wallId, roomLabelId, target };
    return { ...sketch, elevationBindings: [...withoutExisting, binding], updatedAt: now };
  });
}

export function withElevationBindingRemoved(project: ProjectDocument, sketchId: string, wallId: string): ProjectDocument {
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    elevationBindings: sketch.elevationBindings.filter((binding) => binding.wallId !== wallId),
    updatedAt: now,
  }));
}

/** Removes any binding left dangling by a dimension-line deletion in an elevation sketch. Call after removing walls. */
export function withElevationBindingsForWallsRemoved(project: ProjectDocument, sketchId: string, wallIds: readonly string[]): ProjectDocument {
  const idSet = new Set(wallIds);
  const now = new Date().toISOString();
  return updateSketch(project, sketchId, (sketch) => ({
    ...sketch,
    elevationBindings: sketch.elevationBindings.filter((binding) => !idSet.has(binding.wallId)),
    updatedAt: now,
  }));
}

/**
 * Merges `sourceSketchId` into `targetSketchId` (both must be floorPlan
 * sketches on the same floor): applies the least-squares rigid transform
 * (see model/geometry.ts fitRigidTransform2D) that best maps each
 * `correspondences[i].source` point onto its `.target` point, transforms the
 * source sketch's walls/openings/room-label seeds into the target's
 * coordinate frame, and appends them to the target sketch. The source sketch
 * is then removed. Needs at least one correspondence pair; two or more
 * pin down rotation as well as translation.
 */
export function withSketchesMerged(
  project: ProjectDocument,
  targetSketchId: string,
  sourceSketchId: string,
  correspondences: readonly { source: Point2; target: Point2 }[],
): ProjectDocument {
  const source = project.sketches.find((sketch) => sketch.id === sourceSketchId);
  if (!source || correspondences.length === 0) return project;

  const transform = fitRigidTransform2D(
    correspondences.map((c) => c.source),
    correspondences.map((c) => c.target),
  );

  const transformedWalls = source.walls.map((wall) => ({
    ...wall,
    start: applyRigidTransform2D(wall.start, transform),
    end: applyRigidTransform2D(wall.end, transform),
  }));
  const transformedLabels = source.roomLabels.map((label) => ({ ...label, seed: applyRigidTransform2D(label.seed, transform) }));

  const now = new Date().toISOString();
  const merged = updateSketch(project, targetSketchId, (sketch) => ({
    ...sketch,
    walls: [...sketch.walls, ...transformedWalls],
    roomLabels: [...sketch.roomLabels, ...transformedLabels],
    openings: [...sketch.openings, ...source.openings],
    updatedAt: now,
  }));

  return withSketchRemoved(merged, sourceSketchId);
}
