import { createEmptySketch, floorLabel, withSketchRemoved, withSketchRenamed } from '../model/schema';
import type { ProjectDocument, Sketch } from '../model/schema';

interface ProjectSidebarProps {
  project: ProjectDocument;
  activeSketchId: string;
  onSelectSketch: (id: string) => void;
  onSketchCreated: (sketch: Sketch) => void;
  onStartMerge: (targetId: string, sourceId: string) => void;
  update: (mutate: (project: ProjectDocument) => ProjectDocument) => void;
}

export function ProjectSidebar({ project, activeSketchId, onSelectSketch, onSketchCreated, onStartMerge, update }: ProjectSidebarProps) {
  const floorPlans = project.sketches.filter((s) => s.kind === 'floorPlan');
  const elevations = project.sketches.filter((s) => s.kind === 'elevation');

  const floorIndexes = [...new Set(floorPlans.map((s) => s.floorIndex))].sort((a, b) => a - b);
  const activeFloorIndex = project.sketches.find((s) => s.id === activeSketchId)?.floorIndex ?? floorIndexes[0] ?? 0;

  function addFloor() {
    const nextFloorIndex = floorIndexes.length > 0 ? Math.max(...floorIndexes) + 1 : 0;
    const name = window.prompt('Floor name', floorLabel(nextFloorIndex));
    if (name == null || name.trim().length === 0) return;
    const sketch = createEmptySketch(name.trim(), nextFloorIndex, 'floorPlan');
    update((p) => ({ ...p, sketches: [...p.sketches, sketch] }));
    onSketchCreated(sketch);
  }

  function addPartialSketch(floorIndex: number) {
    const existingCount = project.sketches.filter((s) => s.kind === 'floorPlan' && s.floorIndex === floorIndex).length;
    const name = window.prompt('Sketch name', `${floorLabel(floorIndex)} (part ${existingCount + 1})`);
    if (name == null || name.trim().length === 0) return;
    const sketch = createEmptySketch(name.trim(), floorIndex, 'floorPlan');
    update((p) => ({ ...p, sketches: [...p.sketches, sketch] }));
    onSketchCreated(sketch);
  }

  function addElevation() {
    const name = window.prompt('Elevation name', `${floorLabel(activeFloorIndex)} elevation`);
    if (name == null || name.trim().length === 0) return;
    const sketch = createEmptySketch(name.trim(), activeFloorIndex, 'elevation');
    update((p) => ({ ...p, sketches: [...p.sketches, sketch] }));
    onSketchCreated(sketch);
  }

  function renameSketch(sketch: Sketch) {
    const name = window.prompt('Sketch name', sketch.name);
    if (name == null || name.trim().length === 0) return;
    update((p) => withSketchRenamed(p, sketch.id, name.trim()));
  }

  function deleteSketch(sketch: Sketch) {
    if (project.sketches.length <= 1) return;
    if (!window.confirm(`Delete "${sketch.name}"? This can't be undone once you close the project.`)) return;
    update((p) => withSketchRemoved(p, sketch.id));
    if (activeSketchId === sketch.id) {
      const fallback = project.sketches.find((s) => s.id !== sketch.id);
      if (fallback) onSelectSketch(fallback.id);
    }
  }

  return (
    <aside className="card sidebar">
      <div className="sidebar-section">
        <div className="sidebar-section-header">
          <h3>Floors</h3>
          <button className="btn-secondary btn-small" onClick={addFloor}>
            + Floor
          </button>
        </div>
        {floorIndexes.map((floorIndex) => {
          const sketches = floorPlans.filter((s) => s.floorIndex === floorIndex);
          return (
            <div key={floorIndex} className="sidebar-floor-group">
              <div className="sidebar-floor-label">
                <span>{floorLabel(floorIndex)}</span>
                <button className="btn-secondary btn-small" onClick={() => addPartialSketch(floorIndex)} title="Add another partial sketch of this floor to merge later">
                  + Sketch
                </button>
              </div>
              {sketches.map((sketch) => (
                <SketchRow
                  key={sketch.id}
                  sketch={sketch}
                  active={sketch.id === activeSketchId}
                  mergeCandidates={sketches.filter((s) => s.id !== sketch.id)}
                  onSelect={() => onSelectSketch(sketch.id)}
                  onRename={() => renameSketch(sketch)}
                  onDelete={() => deleteSketch(sketch)}
                  onMergeInto={(sourceId) => onStartMerge(sketch.id, sourceId)}
                />
              ))}
            </div>
          );
        })}
      </div>

      <div className="sidebar-section">
        <div className="sidebar-section-header">
          <h3>Elevations</h3>
          <button className="btn-secondary btn-small" onClick={addElevation}>
            + Elevation
          </button>
        </div>
        {elevations.map((sketch) => (
          <SketchRow
            key={sketch.id}
            sketch={sketch}
            active={sketch.id === activeSketchId}
            mergeCandidates={[]}
            onSelect={() => onSelectSketch(sketch.id)}
            onRename={() => renameSketch(sketch)}
            onDelete={() => deleteSketch(sketch)}
            onMergeInto={() => {}}
          />
        ))}
        {elevations.length === 0 && <p className="text-muted sidebar-empty-hint">No elevations yet.</p>}
      </div>
    </aside>
  );
}

interface SketchRowProps {
  sketch: Sketch;
  active: boolean;
  mergeCandidates: Sketch[];
  onSelect: () => void;
  onRename: () => void;
  onDelete: () => void;
  onMergeInto: (sourceId: string) => void;
}

function SketchRow({ sketch, active, mergeCandidates, onSelect, onRename, onDelete, onMergeInto }: SketchRowProps) {
  const count = sketch.kind === 'floorPlan' ? `${sketch.walls.length} wall${sketch.walls.length === 1 ? '' : 's'}` : `${sketch.walls.length} dimension${sketch.walls.length === 1 ? '' : 's'}`;
  return (
    <div className={`sidebar-sketch-row${active ? ' sidebar-sketch-row-active' : ''}`}>
      <button className="sidebar-sketch-name" onClick={onSelect}>
        <span>{sketch.name}</span>
        <span className="text-muted sidebar-sketch-count">{count}</span>
      </button>
      <div className="sidebar-sketch-actions">
        {mergeCandidates.length > 0 && (
          <select
            className="sidebar-merge-select"
            defaultValue=""
            title="Merge another partial sketch of this floor into this one"
            onChange={(e) => {
              if (e.target.value) onMergeInto(e.target.value);
              e.target.value = '';
            }}
          >
            <option value="" disabled>
              Merge…
            </option>
            {mergeCandidates.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        )}
        <button className="btn-secondary btn-small" onClick={onRename} title="Rename">
          ✎
        </button>
        <button className="btn-secondary btn-small" onClick={onDelete} title="Delete">
          ✕
        </button>
      </div>
    </div>
  );
}

