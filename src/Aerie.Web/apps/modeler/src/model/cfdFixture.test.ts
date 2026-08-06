import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { computeExportAirVolume } from './exportGeometry';
import { validateExportMesh } from './exportValidator';
import { meshToBinarySTL, meshToMultiSolidASCIISTL } from './stlExport';
import { createEmptyProject, createId, withRoomCeilingProfileSet, withRoomNamed, withWallAdded } from './schema';
import type { Point2, ProjectDocument, WallSegment } from './schema';

/**
 * The Step 7 CFD-validation fixture: the simplest possible watertight
 * building - a single 4m x 3m x 2.5m room, no doors or second floor - used to
 * prove the exporter's output actually meshes and solves in OpenFOAM (see
 * docs/cfd-validation.md). Only regenerated on demand (`WRITE_CFD_FIXTURE=1`),
 * not on every `npm test` run, since writing to disk is a deliberate one-off
 * side effect rather than a normal assertion.
 */
function singleRoomProject(): ProjectDocument {
  let project = createEmptyProject();
  const sketch = project.sketches[0];

  function wall(start: Point2, end: Point2): WallSegment {
    return { id: createId(), start, end, thickness: 0.15 };
  }

  const walls = [
    wall({ x: 0, y: 0 }, { x: 4, y: 0 }),
    wall({ x: 4, y: 0 }, { x: 4, y: 3 }),
    wall({ x: 4, y: 3 }, { x: 0, y: 3 }),
    wall({ x: 0, y: 3 }, { x: 0, y: 0 }),
  ];
  for (const w of walls) project = withWallAdded(project, sketch.id, w);

  project = withRoomNamed(project, sketch.id, null, { x: 2, y: 1.5 }, 'Room');
  const label = project.sketches[0].roomLabels[0];
  project = withRoomCeilingProfileSet(project, sketch.id, label.id, label.seed, { kind: 'flat', wallHeight: 2.5 });

  return project;
}

const fixtureDir = resolve(dirname(fileURLToPath(import.meta.url)), '../../../../../../docs/cfd/fixtures');

describe('CFD validation fixture', () => {
  it.runIf(process.env.WRITE_CFD_FIXTURE)('exports the single-room fixture to docs/cfd/fixtures', async () => {
    const project = singleRoomProject();
    const result = await computeExportAirVolume(project);
    expect(result.mesh).not.toBeNull();

    const validation = validateExportMesh(result.mesh!);
    expect(validation.issues).toEqual([]);
    expect(validation.ok).toBe(true);

    await mkdir(fixtureDir, { recursive: true });
    await writeFile(resolve(fixtureDir, 'single-room.stl'), meshToMultiSolidASCIISTL(result.mesh!));
    await writeFile(resolve(fixtureDir, 'single-room-binary.stl'), Buffer.from(meshToBinarySTL(result.mesh!)));
  }, 20000);
});
