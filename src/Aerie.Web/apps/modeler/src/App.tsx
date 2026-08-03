import { useEffect, useRef, useState } from 'react';
import type { ChangeEvent } from 'react';
import './App.css';
import { ElevationEditor } from './components/ElevationEditor';
import { FloorPlanEditor } from './components/FloorPlanEditor';
import { ProjectSidebar } from './components/ProjectSidebar';
import { SketchMergeView } from './components/SketchMergeView';
import { withSketchesMerged } from './model/schema';
import type { Sketch } from './model/schema';
import { useProjectStore } from './state/useProjectStore';

function formatSaveStatus(isSaving: boolean, savedAt: Date | null): string {
  if (isSaving) return 'Saving…';
  if (!savedAt) return 'Not saved yet';
  return `Saved ${savedAt.toLocaleTimeString()}`;
}

export function App() {
  const store = useProjectStore();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [activeSketchId, setActiveSketchId] = useState<string | null>(null);
  const [mergeTarget, setMergeTarget] = useState<{ targetId: string; sourceId: string } | null>(null);

  const project = store.project;

  // Keep the active sketch valid as the project loads or sketches are added/removed/merged.
  useEffect(() => {
    if (!project) return;
    if (activeSketchId && project.sketches.some((s) => s.id === activeSketchId)) return;
    setActiveSketchId(project.sketches[0]?.id ?? null);
  }, [project, activeSketchId]);

  if (store.status === 'loading' || !project || !activeSketchId) {
    return (
      <div className="modeler-app modeler-loading">
        <p className="text-muted">Loading project…</p>
      </div>
    );
  }

  const sketch = project.sketches.find((s) => s.id === activeSketchId) ?? project.sketches[0];

  const handleImportChange = async (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    try {
      await store.importProject(file);
      setActiveSketchId(null);
      setMergeTarget(null);
    } catch (err) {
      window.alert(err instanceof Error ? err.message : 'Failed to import project file.');
    }
  };

  function handleSketchCreated(created: Sketch) {
    setActiveSketchId(created.id);
  }

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

      <div className="modeler-body">
        <ProjectSidebar
          project={project}
          activeSketchId={activeSketchId}
          onSelectSketch={(id) => {
            setActiveSketchId(id);
            setMergeTarget(null);
          }}
          onSketchCreated={handleSketchCreated}
          onStartMerge={(targetId, sourceId) => setMergeTarget({ targetId, sourceId })}
          update={store.update}
        />

        <main className="card modeler-editor-card">
          {mergeTarget ? (
            <SketchMergeView
              project={project}
              targetId={mergeTarget.targetId}
              sourceId={mergeTarget.sourceId}
              onCancel={() => setMergeTarget(null)}
              onConfirm={(correspondences) => {
                store.update((p) => withSketchesMerged(p, mergeTarget.targetId, mergeTarget.sourceId, correspondences));
                setActiveSketchId(mergeTarget.targetId);
                setMergeTarget(null);
              }}
            />
          ) : sketch.kind === 'elevation' ? (
            <ElevationEditor project={project} sketch={sketch} update={store.update} />
          ) : (
            <FloorPlanEditor project={project} sketch={sketch} update={store.update} />
          )}
        </main>
      </div>
    </div>
  );
}
