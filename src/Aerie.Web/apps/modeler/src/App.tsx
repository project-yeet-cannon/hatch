import { useRef } from 'react';
import type { ChangeEvent } from 'react';
import './App.css';
import { FloorPlanEditor } from './components/FloorPlanEditor';
import { useProjectStore } from './state/useProjectStore';

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

      <main className="card modeler-editor-card">
        <FloorPlanEditor project={project} sketch={sketch} update={store.update} />
      </main>
    </div>
  );
}
