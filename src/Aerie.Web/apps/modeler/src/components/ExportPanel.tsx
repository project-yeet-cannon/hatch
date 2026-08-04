import { useEffect, useState } from 'react';
import { computeExportAirVolume } from '../model/exportGeometry';
import type { PatchedMesh } from '../model/exportGeometry';
import { validateExportMesh } from '../model/exportValidator';
import type { ExportValidationResult } from '../model/exportValidator';
import { buildTopology } from '../model/topology';
import type { ProjectDocument } from '../model/schema';
import { downloadBinarySTL, downloadGLB, downloadMultiSolidSTL, downloadOBJ, downloadTopologyJSON } from '../persistence/modelExport';

interface ExportPanelProps {
  project: ProjectDocument;
}

/**
 * Spec #8: export the home in formats CFD/CAD tooling can consume, plus a
 * validator so a bad model fails here with a clear message rather than
 * inside a mesher (see TODO_MODELING.md's CFD research and exportGeometry.ts
 * for what each format is for and why doors don't get their own patch yet).
 */
export function ExportPanel({ project }: ExportPanelProps) {
  const [mesh, setMesh] = useState<PatchedMesh | null>(null);
  const [meshWarnings, setMeshWarnings] = useState<string[]>([]);
  const [validation, setValidation] = useState<ExportValidationResult | null>(null);
  const [status, setStatus] = useState<'idle' | 'loading' | 'error'>('idle');
  const [error, setError] = useState<string | null>(null);
  const [busyAction, setBusyAction] = useState<string | null>(null);

  async function regenerate() {
    setStatus('loading');
    setError(null);
    try {
      const result = await computeExportAirVolume(project);
      setMesh(result.mesh);
      setMeshWarnings(result.warnings);
      setValidation(result.mesh ? validateExportMesh(result.mesh) : null);
      setStatus('idle');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to generate the export geometry.');
      setStatus('error');
    }
  }

  useEffect(() => {
    regenerate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const meshReady = mesh !== null && validation !== null && validation.ok;

  async function runAction(name: string, action: () => void | Promise<void>) {
    setBusyAction(name);
    try {
      await action();
    } catch (err) {
      setError(err instanceof Error ? err.message : `Failed to export ${name}.`);
    } finally {
      setBusyAction(null);
    }
  }

  return (
    <div className="export-panel">
      <div className="export-panel-toolbar">
        <button className="btn-primary" onClick={regenerate} disabled={status === 'loading'}>
          {status === 'loading' ? 'Generating…' : 'Regenerate export geometry'}
        </button>
        {status === 'error' && <span className="viewer3d-error">{error}</span>}
      </div>

      {meshWarnings.length > 0 && <p className="text-muted export-panel-warnings">{meshWarnings.join(' ')}</p>}

      {validation && (
        <div className={validation.ok ? 'export-panel-validation-ok' : 'export-panel-validation-error'}>
          <p>
            {validation.triangleCount.toLocaleString()} triangles, {validation.vertexCount.toLocaleString()} vertices, {validation.surfaceArea.toFixed(2)} m² surface area.
          </p>
          {validation.issues.map((issue, i) => (
            <p key={i} className={issue.severity === 'error' ? 'export-panel-issue-error' : 'export-panel-issue-warning'}>
              {issue.severity === 'error' ? 'Error: ' : 'Warning: '}
              {issue.message}
            </p>
          ))}
        </div>
      )}

      {error && status !== 'error' && <p className="export-panel-issue-error">{error}</p>}

      <div className="export-panel-formats">
        <div className="export-panel-format">
          <div>
            <strong>Binary STL</strong>
            <p className="text-muted">Single watertight air volume - SimScale and most generic viewers.</p>
          </div>
          <button
            className="btn-secondary"
            disabled={!meshReady || busyAction !== null}
            onClick={() => runAction('stl', () => downloadBinarySTL(project, mesh!))}
          >
            {busyAction === 'stl' ? 'Exporting…' : 'Download .stl'}
          </button>
        </div>

        <div className="export-panel-format">
          <div>
            <strong>Multi-solid ASCII STL</strong>
            <p className="text-muted">Named boundary patches (walls/floor/ceiling) for OpenFOAM's snappyHexMesh.</p>
          </div>
          <button
            className="btn-secondary"
            disabled={!meshReady || busyAction !== null}
            onClick={() => runAction('patches.stl', () => downloadMultiSolidSTL(project, mesh!))}
          >
            {busyAction === 'patches.stl' ? 'Exporting…' : 'Download patches.stl'}
          </button>
        </div>

        <div className="export-panel-format">
          <div>
            <strong>OBJ</strong>
            <p className="text-muted">Grouped by patch - general CAD/viz tools, Blender.</p>
          </div>
          <button className="btn-secondary" disabled={!meshReady || busyAction !== null} onClick={() => runAction('obj', () => downloadOBJ(project, mesh!))}>
            {busyAction === 'obj' ? 'Exporting…' : 'Download .obj'}
          </button>
        </div>

        <div className="export-panel-format">
          <div>
            <strong>GLB</strong>
            <p className="text-muted">Binary glTF - quick viewing/sharing in any glTF-compatible viewer.</p>
          </div>
          <button className="btn-secondary" disabled={!meshReady || busyAction !== null} onClick={() => runAction('glb', () => downloadGLB(project, mesh!))}>
            {busyAction === 'glb' ? 'Exporting…' : 'Download .glb'}
          </button>
        </div>

        <div className="export-panel-format">
          <div>
            <strong>Topology JSON</strong>
            <p className="text-muted">Rooms/openings graph for the Aerie climate controller (sensors reserved for later).</p>
          </div>
          <button className="btn-secondary" disabled={busyAction !== null} onClick={() => runAction('topology.json', () => downloadTopologyJSON(project, buildTopology(project)))}>
            {busyAction === 'topology.json' ? 'Exporting…' : 'Download topology.json'}
          </button>
        </div>
      </div>
    </div>
  );
}
