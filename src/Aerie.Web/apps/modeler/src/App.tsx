import { useRef } from 'react';
import type { ChangeEvent } from 'react';
import './App.css';
import { createId, withLastWallRemoved, withWallAdded } from './model/schema';
import { useProjectStore } from './state/useProjectStore';

function randomWall() {
  const angle = Math.random() * Math.PI * 2;
  const length = Math.round((2 + Math.random() * 4) * 10) / 10;
  const originX = Math.round(Math.random() * 8 * 10) / 10;
  const originY = Math.round(Math.random() * 8 * 10) / 10;
  return {
    id: createId(),
    start: { x: originX, y: originY },
    end: {
      x: Math.round((originX + Math.cos(angle) * length) * 10) / 10,
      y: Math.round((originY + Math.sin(angle) * length) * 10) / 10,
    },
    thickness: 0.15,
  };
}

function formatSaveStatus(isSaving: boolean, savedAt: Date | null): string {
  if (isSaving) return 'Saving…';
  if (!savedAt) return 'Not saved yet';
  return `Saved ${savedAt.toLocaleTimeString()}`;
}

export function App() {
  const store = useProjectStore();
  const fileInputRef = useRef<HTMLInputElement>(null);

  if (store.status === 'loading' || !store.project) {
    return (
      <div className="modeler-app modeler-loading">
        <p className="text-muted">Loading project…</p>
      </div>
    );
  }

  const project = store.project;
  const sketch = project.sketches[0];

  const handleImportChange = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    try {
      await store.importProject(file);
    } catch (err) {
      window.alert(err instanceof Error ? err.message : 'Failed to import project file.');
    }
  };

  return (
    <div className="modeler-app">
      <header className="modeler-header">
        <div>
          <h1>Aerie Modeler</h1>
          <p className="text-muted">{project.name}</p>
        </div>
        <span className="modeler-save-status">{formatSaveStatus(store.isSaving, store.lastSavedAt)}</span>
      </header>

      <div className="modeler-toolbar">
        <button className="btn-secondary" onClick={store.undo} disabled={!store.canUndo}>
          Undo
        </button>
        <button className="btn-secondary" onClick={store.redo} disabled={!store.canRedo}>
          Redo
        </button>
        <button className="btn-secondary" onClick={store.exportProject}>
          Export project
        </button>
        <button className="btn-secondary" onClick={() => fileInputRef.current?.click()}>
          Import project
        </button>
        <input ref={fileInputRef} type="file" accept="application/json,.json" hidden onChange={handleImportChange} />
      </div>

      <main className="card modeler-placeholder">
        <h2>{sketch.name}</h2>
        <p className="text-muted">
          {sketch.walls.length} wall{sketch.walls.length === 1 ? '' : 's'}. The floor-plan editor (draw walls, snapping,
          room detection) arrives in step 2 of the plan — these buttons exercise the persistence spine until then.
        </p>
        <div className="flex gap-2">
          <button className="btn-primary" onClick={() => store.update((p) => withWallAdded(p, sketch.id, randomWall()))}>
            Add wall
          </button>
          <button
            className="btn-secondary"
            onClick={() => store.update((p) => withLastWallRemoved(p, sketch.id))}
            disabled={sketch.walls.length === 0}
          >
            Remove last wall
          </button>
        </div>
      </main>
    </div>
  );
}
