import { useEffect, useRef, useState } from 'react';
import type { ChangeEvent } from 'react';
import { TopBar } from '@aerie/ui';
import './App.css';
import { ElevationEditor } from './components/ElevationEditor';
import { ExportPanel } from './components/ExportPanel';
import { FloorPlanEditor } from './components/FloorPlanEditor';
import { ProjectSidebar } from './components/ProjectSidebar';
import { SketchMergeView } from './components/SketchMergeView';
import { Viewer3D } from './components/Viewer3D';
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
  const [show3D, setShow3D] = useState(false);
  const [showExport, setShowExport] = useState(false);

  const project = store.project;

  // Keep the active sketch valid as the project loads or sketches are added/removed/merged.
  useEffect(() => {
    if (!project) return;
    if (activeSketchId && project.sketches.some((s) => s.id === activeSketchId)) return;
    setActiveSketchId(project.sketches[0]?.id ?? null);
  }, [project, activeSketchId]);

  if (store.status === 'loading' || !project || !activeSketchId) {
    return (
      <div className="modeler-app">
        <TopBar appName="Aerie Modeler" />
        <div className="modeler-content modeler-loading">
          <p className="text-muted">Loading project…</p>
        </div>
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
      {/* The shared bar, in place of this app's own header - which was also the
          only page in the house with no way back to the app picker.

          The two things that header carried keep their sides of it. The open
          project's name is identity, so it goes in `leading`, right of the app
          name; the save status is state, so it goes in `trailing`, beside the
          theme switch. Neither is an <h1>: the bar says where you are, and the
          editor below is the page. */}
      <TopBar
        appName="Aerie Modeler"
        leading={<span className="modeler-project-name">{project.name}</span>}
        trailing={
          <span className="modeler-save-status">
            {formatSaveStatus(store.isSaving, store.lastSavedAt)}
          </span>
        }
      />

      <div className="modeler-content">
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
          <button
            className={show3D ? 'btn-primary' : 'btn-secondary'}
            onClick={() => {
              setShow3D((v) => !v);
              setShowExport(false);
            }}
          >
            {show3D ? 'Back to 2D' : '3D view'}
          </button>
          <button
            className={showExport ? 'btn-primary' : 'btn-secondary'}
            onClick={() => {
              setShowExport((v) => !v);
              setShow3D(false);
            }}
          >
            {showExport ? 'Back to 2D' : 'Export'}
          </button>
        </div>

        <div className="modeler-body">
          <ProjectSidebar
            project={project}
            activeSketchId={activeSketchId}
            onSelectSketch={(id) => {
              setActiveSketchId(id);
              setMergeTarget(null);
              setShow3D(false);
              setShowExport(false);
            }}
            onSketchCreated={handleSketchCreated}
            onStartMerge={(targetId, sourceId) => setMergeTarget({ targetId, sourceId })}
            update={store.update}
          />

          <main className="card modeler-editor-card">
            {showExport ? (
              <ExportPanel project={project} />
            ) : show3D ? (
              <Viewer3D project={project} />
            ) : mergeTarget ? (
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
    </div>
  );
}
