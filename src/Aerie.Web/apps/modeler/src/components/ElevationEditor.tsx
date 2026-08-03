import { useEffect, useMemo, useRef, useState } from 'react';
import type { PointerEvent as ReactPointerEvent, WheelEvent as ReactWheelEvent } from 'react';
import { defaultCamera, fitCamera, panBy, screenToWorld, worldToScreen, zoomAt } from '../editor/camera';
import type { Camera } from '../editor/camera';
import { findRoomLabel } from '../model/elevation';
import { distance, nearestPoint, snapDrawPoint } from '../model/geometry';
import {
  createId,
  withElevationBindingRemoved,
  withElevationBindingSet,
  withElevationBindingsForWallsRemoved,
  withVertexMoved,
  withWallAdded,
  withWallLengthSet,
  withWallPositionsUpdated,
  withWallsRemoved,
} from '../model/schema';
import type { ElevationBinding, Point2, ProjectDocument, RoomLabel, Sketch, WallSegment } from '../model/schema';
import { solveSketch } from '../model/solver';
import type { EdgeStatus } from '../model/solver';

interface ElevationEditorProps {
  project: ProjectDocument;
  sketch: Sketch;
  update: (mutate: (project: ProjectDocument) => ProjectDocument) => void;
}

type Tool = 'line' | 'select';

const VERTEX_HIT_PX = 9;
const LINE_HIT_PX = 8;
const MIN_LINE_LENGTH = 0.05;
const VERTEX_EPSILON = 1e-4;
const LINE_THICKNESS = 0.02;

function pointsClose(a: Point2, b: Point2, epsilon = VERTEX_EPSILON): boolean {
  return Math.abs(a.x - b.x) < epsilon && Math.abs(a.y - b.y) < epsilon;
}

function collectVertices(walls: readonly WallSegment[]): Point2[] {
  const seen = new Map<string, Point2>();
  for (const wall of walls) {
    for (const p of [wall.start, wall.end]) {
      const key = `${Math.round(p.x / VERTEX_EPSILON)}:${Math.round(p.y / VERTEX_EPSILON)}`;
      if (!seen.has(key)) seen.set(key, p);
    }
  }
  return [...seen.values()];
}

function findLineHit(walls: readonly WallSegment[], point: Point2, zoom: number): WallSegment | null {
  let best: WallSegment | null = null;
  let bestDist = LINE_HIT_PX / zoom;
  for (const wall of walls) {
    const seg = { x: wall.end.x - wall.start.x, y: wall.end.y - wall.start.y };
    const segLenSq = seg.x * seg.x + seg.y * seg.y;
    const t = segLenSq < 1e-12 ? 0 : Math.max(0, Math.min(1, ((point.x - wall.start.x) * seg.x + (point.y - wall.start.y) * seg.y) / segLenSq));
    const projected = { x: wall.start.x + seg.x * t, y: wall.start.y + seg.y * t };
    const d = distance(point, projected);
    if (d <= bestDist) {
      bestDist = d;
      best = wall;
    }
  }
  return best;
}

function boundsOf(points: readonly Point2[]): { center: Point2; size: { width: number; height: number } } {
  if (points.length === 0) return { center: { x: 0, y: 0 }, size: { width: 0, height: 0 } };
  const xs = points.map((p) => p.x);
  const ys = points.map((p) => p.y);
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs);
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);
  return {
    center: { x: (minX + maxX) / 2, y: (minY + maxY) / 2 },
    size: { width: Math.max(maxX - minX, 1), height: Math.max(maxY - minY, 1) },
  };
}

function isTypingTarget(target: EventTarget | null): boolean {
  const tag = (target as HTMLElement | null)?.tagName;
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
}

/**
 * A side-view sketch: dimension lines drawn and measured exactly like a
 * floor plan's walls (same snapping + LM solver, see model/solver.ts), but
 * displayed with y increasing upward (height above the floor) rather than
 * plan north. `Point2.y` in an elevation sketch's own walls IS the height in
 * meters - only screen conversion flips its sign; nothing else in the model
 * needs to know a sketch is an elevation. A selected line can be bound to a
 * room's wallHeight or ridgeHeight (model/schema.ts ElevationBinding), which
 * is how a real ceiling measurement ends up driving the 3D model instead of
 * a typed-in guess (see model/elevation.ts resolveCeilingProfile).
 */
export function ElevationEditor({ project, sketch, update }: ElevationEditorProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);

  const [viewport, setViewport] = useState({ width: 0, height: 0 });
  const [camera, setCamera] = useState<Camera>(() => defaultCamera());
  const didInitialFit = useRef(false);

  const [tool, setTool] = useState<Tool>('line');
  const [drawStart, setDrawStart] = useState<Point2 | null>(null);
  const [cursorWorld, setCursorWorld] = useState<Point2 | null>(null);
  const [selectedWallIds, setSelectedWallIds] = useState<Set<string>>(new Set());
  const [draggingVertex, setDraggingVertex] = useState<Point2 | null>(null);
  const [dragPreview, setDragPreview] = useState<Point2 | null>(null);
  const [spacePressed, setSpacePressed] = useState(false);
  const [isPanning, setIsPanning] = useState(false);
  const panState = useRef<{ startScreen: Point2; startCamera: Camera } | null>(null);

  const vertices = useMemo(() => collectVertices(sketch.walls), [sketch.walls]);
  const solution = useMemo(() => solveSketch(sketch.walls), [sketch.walls]);
  const singleSelectedWall = selectedWallIds.size === 1 ? sketch.walls.find((w) => selectedWallIds.has(w.id)) ?? null : null;
  const bindingForSelected = singleSelectedWall ? sketch.elevationBindings.find((b) => b.wallId === singleSelectedWall.id) : undefined;

  const candidateRooms = useMemo(() => {
    const rooms: { label: RoomLabel; sketchName: string }[] = [];
    for (const s of project.sketches) {
      if (s.kind !== 'floorPlan' || s.floorIndex !== sketch.floorIndex) continue;
      for (const label of s.roomLabels) rooms.push({ label, sketchName: s.name });
    }
    return rooms;
  }, [project.sketches, sketch.floorIndex]);

  useEffect(() => {
    setDrawStart(null);
    setSelectedWallIds(new Set());
    didInitialFit.current = false;
  }, [sketch.id]);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      setViewport({ width: entry.contentRect.width, height: entry.contentRect.height });
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    if (didInitialFit.current || viewport.width === 0 || viewport.height === 0) return;
    didInitialFit.current = true;
    const { center, size } = boundsOf(vertices);
    setCamera(fitCamera({ x: center.x, y: -center.y }, vertices.length > 0 ? size : { width: 0, height: 0 }, viewport));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [viewport]);

  const zoomToFit = () => {
    const { center, size } = boundsOf(vertices);
    setCamera(fitCamera({ x: center.x, y: -center.y }, vertices.length > 0 ? size : { width: 0, height: 0 }, viewport));
  };

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (isTypingTarget(e.target)) return;
      if (e.code === 'Space') {
        setSpacePressed(true);
        e.preventDefault();
        return;
      }
      if (e.key === 'Escape') {
        if (drawStart) setDrawStart(null);
        else setSelectedWallIds(new Set());
        return;
      }
      if ((e.key === 'Delete' || e.key === 'Backspace') && selectedWallIds.size > 0) {
        update((p) => withElevationBindingsForWallsRemoved(withWallsRemoved(p, sketch.id, [...selectedWallIds]), sketch.id, [...selectedWallIds]));
        setSelectedWallIds(new Set());
        return;
      }
      if (e.key === '1') setTool('line');
      if (e.key === '2') setTool('select');
    }
    function onKeyUp(e: KeyboardEvent) {
      if (e.code === 'Space') setSpacePressed(false);
    }
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
    };
  }, [drawStart, selectedWallIds, sketch.id, update]);

  // Height-space (y-up) world point <-> screen, flipping the sign of y at
  // the boundary so stored coordinates keep "y is meters above the floor"
  // semantics everywhere except this presentation layer.
  function heightToScreen(p: Point2): Point2 {
    return worldToScreen(camera, { x: p.x, y: -p.y });
  }
  function screenToHeight(p: Point2): Point2 {
    const w = screenToWorld(camera, p);
    return { x: w.x, y: -w.y };
  }

  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas || viewport.width === 0 || viewport.height === 0) return;
    const dpr = window.devicePixelRatio || 1;
    canvas.width = viewport.width * dpr;
    canvas.height = viewport.height * dpr;
    canvas.style.width = `${viewport.width}px`;
    canvas.style.height = `${viewport.height}px`;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

    const styles = getComputedStyle(document.documentElement);
    const colors = {
      ink: styles.getPropertyValue('--ink').trim() || '#1a1a1a',
      muted: styles.getPropertyValue('--muted').trim() || '#666',
      line: styles.getPropertyValue('--line').trim() || '#e0e0e0',
      primary: styles.getPropertyValue('--primary').trim() || '#06c',
      success: styles.getPropertyValue('--success').trim() || '#388e3c',
      warning: styles.getPropertyValue('--warning').trim() || '#b58900',
    };

    ctx.clearRect(0, 0, viewport.width, viewport.height);

    // Ground line at height 0.
    const groundY = heightToScreen({ x: 0, y: 0 }).y;
    ctx.strokeStyle = colors.line;
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, groundY);
    ctx.lineTo(viewport.width, groundY);
    ctx.stroke();

    for (const wall of sketch.walls) {
      const bound = sketch.elevationBindings.some((b) => b.wallId === wall.id);
      const status = solution.statuses.get(wall.id);
      const color = selectedWallIds.has(wall.id) ? colors.primary : status === 'measured' ? colors.success : status === 'derived' ? colors.primary : colors.warning;
      const s = heightToScreen(wall.start);
      const e = heightToScreen(wall.end);
      ctx.strokeStyle = color;
      ctx.lineWidth = selectedWallIds.has(wall.id) ? 4 : 3;
      ctx.lineCap = 'round';
      ctx.beginPath();
      ctx.moveTo(s.x, s.y);
      ctx.lineTo(e.x, e.y);
      ctx.stroke();

      if (bound) {
        ctx.fillStyle = colors.primary;
        ctx.beginPath();
        ctx.arc((s.x + e.x) / 2, (s.y + e.y) / 2, 4, 0, Math.PI * 2);
        ctx.fill();
      }

      const solvedLength = solution.lengths.get(wall.id);
      if (solvedLength !== undefined) {
        ctx.font = '11px Manrope, sans-serif';
        ctx.fillStyle = color;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'bottom';
        ctx.fillText(`${solvedLength.toFixed(2)} m`, (s.x + e.x) / 2, Math.min(s.y, e.y) - 4);
      }
    }

    for (const vertex of vertices) {
      if (draggingVertex && pointsClose(vertex, draggingVertex)) continue;
      const s = heightToScreen(vertex);
      ctx.beginPath();
      ctx.arc(s.x, s.y, 3.5, 0, Math.PI * 2);
      ctx.fillStyle = colors.muted;
      ctx.fill();
    }

    if (draggingVertex) {
      const s = heightToScreen(dragPreview ?? draggingVertex);
      ctx.beginPath();
      ctx.arc(s.x, s.y, 6, 0, Math.PI * 2);
      ctx.fillStyle = colors.primary;
      ctx.fill();
    }

    if (tool === 'line' && drawStart && cursorWorld) {
      const vertexTolerance = VERTEX_HIT_PX / camera.zoom;
      const snap = snapDrawPoint(cursorWorld, drawStart, vertices, vertexTolerance);
      const s = heightToScreen(drawStart);
      const e = heightToScreen(snap.point);
      ctx.save();
      ctx.globalAlpha = 0.6;
      ctx.strokeStyle = colors.primary;
      ctx.lineWidth = 3;
      ctx.lineCap = 'round';
      ctx.beginPath();
      ctx.moveTo(s.x, s.y);
      ctx.lineTo(e.x, e.y);
      ctx.stroke();
      ctx.restore();
      ctx.font = '12px Manrope, sans-serif';
      ctx.fillStyle = colors.success;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'bottom';
      ctx.fillText(`${distance(drawStart, snap.point).toFixed(2)} m`, (s.x + e.x) / 2, Math.min(s.y, e.y) - 6);
    }
    // heightToScreen/screenToHeight close over `camera` and are recreated each render, so they're intentionally excluded from deps.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [camera, viewport, sketch.walls, sketch.elevationBindings, vertices, selectedWallIds, draggingVertex, dragPreview, tool, drawStart, cursorWorld, solution]);

  function getCanvasPoint(e: ReactPointerEvent | ReactWheelEvent): Point2 {
    const rect = canvasRef.current!.getBoundingClientRect();
    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
  }

  function handleWheel(e: ReactWheelEvent) {
    e.preventDefault();
    const factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
    setCamera((c) => zoomAt(c, getCanvasPoint(e), factor));
  }

  function handlePointerDown(e: ReactPointerEvent) {
    const screen = getCanvasPoint(e);
    if (e.button === 1 || (e.button === 0 && spacePressed)) {
      setIsPanning(true);
      panState.current = { startScreen: screen, startCamera: camera };
      (e.target as Element).setPointerCapture(e.pointerId);
      return;
    }
    if (e.button !== 0) return;
    const rawWorld = screenToHeight(screen);

    if (tool === 'line') {
      const vertexTolerance = VERTEX_HIT_PX / camera.zoom;
      const snap = snapDrawPoint(rawWorld, drawStart, vertices, vertexTolerance);
      const point = snap.point;
      if (drawStart === null) {
        setDrawStart(point);
        return;
      }
      if (distance(drawStart, point) < MIN_LINE_LENGTH) {
        setDrawStart(null);
        return;
      }
      const wall: WallSegment = { id: createId(), start: drawStart, end: point, thickness: LINE_THICKNESS };
      update((p) => withWallAdded(p, sketch.id, wall));
      setDrawStart(point);
      return;
    }

    // select tool
    const vertexTolerance = VERTEX_HIT_PX / camera.zoom;
    const hitVertex = nearestPoint(rawWorld, vertices, vertexTolerance);
    if (hitVertex) {
      setDraggingVertex(hitVertex);
      setDragPreview(hitVertex);
      (e.target as Element).setPointerCapture(e.pointerId);
      return;
    }

    const hitWall = findLineHit(sketch.walls, rawWorld, camera.zoom);
    if (hitWall) {
      setSelectedWallIds((prev) => {
        const next = e.shiftKey ? new Set(prev) : new Set<string>();
        if (next.has(hitWall.id)) next.delete(hitWall.id);
        else next.add(hitWall.id);
        return next;
      });
      return;
    }

    setSelectedWallIds(new Set());
  }

  function handlePointerMove(e: ReactPointerEvent) {
    const screen = getCanvasPoint(e);
    if (isPanning && panState.current) {
      const dx = screen.x - panState.current.startScreen.x;
      const dy = screen.y - panState.current.startScreen.y;
      setCamera(panBy(panState.current.startCamera, { x: dx, y: dy }));
      return;
    }
    const rawWorld = screenToHeight(screen);
    setCursorWorld(rawWorld);

    if (draggingVertex) {
      const others = vertices.filter((v) => !pointsClose(v, draggingVertex));
      const tolerance = VERTEX_HIT_PX / camera.zoom;
      const snap = snapDrawPoint(rawWorld, null, others, tolerance);
      setDragPreview(snap.point);
    }
  }

  function handlePointerUp() {
    if (isPanning) {
      setIsPanning(false);
      panState.current = null;
      return;
    }
    if (draggingVertex) {
      const final = dragPreview ?? draggingVertex;
      if (!pointsClose(final, draggingVertex)) {
        update((p) => withVertexMoved(p, sketch.id, draggingVertex, final));
      }
      setDraggingVertex(null);
      setDragPreview(null);
    }
  }

  const cursorStyle = isPanning ? 'grabbing' : spacePressed ? 'grab' : tool === 'line' ? 'crosshair' : 'default';

  return (
    <div className="editor">
      <div className="editor-toolbar">
        <div className="editor-tool-group">
          <button className={tool === 'line' ? 'btn-primary' : 'btn-secondary'} onClick={() => setTool('line')}>
            Draw dimension
          </button>
          <button className={tool === 'select' ? 'btn-primary' : 'btn-secondary'} onClick={() => setTool('select')}>
            Select / move
          </button>
        </div>
        <button className="btn-secondary" onClick={zoomToFit}>
          Zoom to fit
        </button>
        <button
          className="btn-secondary"
          onClick={() => {
            update((p) => withElevationBindingsForWallsRemoved(withWallsRemoved(p, sketch.id, [...selectedWallIds]), sketch.id, [...selectedWallIds]));
            setSelectedWallIds(new Set());
          }}
          disabled={selectedWallIds.size === 0}
        >
          Delete selected
        </button>
        {singleSelectedWall && (
          <DimensionInput
            wall={singleSelectedWall}
            solvedLength={solution.lengths.get(singleSelectedWall.id) ?? 0}
            status={solution.statuses.get(singleSelectedWall.id)}
            onSet={(length) => update((p) => withWallLengthSet(p, sketch.id, singleSelectedWall.id, length))}
          />
        )}
        <button
          className="btn-secondary"
          onClick={() => update((p) => withWallPositionsUpdated(p, sketch.id, solution.walls))}
          disabled={sketch.walls.length === 0}
          title="Reshape the drawing to match the solved dimensions"
        >
          Apply solved geometry
        </button>
      </div>
      <p className="text-muted editor-hint">
        {tool === 'line'
          ? 'Click to start a dimension line, click again to place each point (chains continue automatically). Esc cancels. Space-drag or middle-drag to pan, scroll to zoom.'
          : 'Drag a point to move it, click a line to select and set its real length (Del to remove), then bind it to a room below.'}
      </p>
      <div className="elevation-body">
        <div ref={containerRef} className="editor-canvas-container elevation-canvas-container">
          <canvas
            ref={canvasRef}
            className="editor-canvas"
            style={{ cursor: cursorStyle }}
            onWheel={handleWheel}
            onPointerDown={handlePointerDown}
            onPointerMove={handlePointerMove}
            onPointerUp={handlePointerUp}
          />
        </div>
        <BindingPanel
          project={project}
          sketch={sketch}
          selectedWall={singleSelectedWall}
          binding={bindingForSelected}
          candidateRooms={candidateRooms}
          onBind={(roomLabelId, target) => {
            if (!singleSelectedWall) return;
            update((p) => withElevationBindingSet(p, sketch.id, singleSelectedWall.id, roomLabelId, target));
          }}
          onUnbind={(wallId) => update((p) => withElevationBindingRemoved(p, sketch.id, wallId))}
        />
      </div>
    </div>
  );
}

interface DimensionInputProps {
  wall: WallSegment;
  solvedLength: number;
  status: EdgeStatus | undefined;
  onSet: (length: number | null) => void;
}

function DimensionInput({ wall, solvedLength, status, onSet }: DimensionInputProps) {
  const [draft, setDraft] = useState(() => (wall.measuredLength ?? solvedLength).toFixed(2));

  useEffect(() => {
    setDraft((wall.measuredLength ?? solvedLength).toFixed(2));
  }, [wall.id, wall.measuredLength, solvedLength]);

  function commit() {
    const value = Number(draft);
    if (Number.isFinite(value) && value > 0) onSet(value);
  }

  return (
    <label className="editor-dimension" title={`Status: ${status ?? 'estimated'}`}>
      Length (m)
      <input type="number" min={0.05} step={0.01} value={draft} onChange={(e) => setDraft(e.target.value)} onBlur={commit} onKeyDown={(e) => e.key === 'Enter' && commit()} />
      {wall.measuredLength !== undefined && (
        <button className="btn-secondary" onClick={() => onSet(null)}>
          Clear
        </button>
      )}
    </label>
  );
}

interface BindingPanelProps {
  project: ProjectDocument;
  sketch: Sketch;
  selectedWall: WallSegment | null;
  binding: ElevationBinding | undefined;
  candidateRooms: { label: RoomLabel; sketchName: string }[];
  onBind: (roomLabelId: string, target: ElevationBinding['target']) => void;
  onUnbind: (wallId: string) => void;
}

/** Binds the selected dimension line's solved length to a room's ceiling height, and lists every binding already made in this sketch (see model/elevation.ts). */
function BindingPanel({ project, sketch, selectedWall, binding, candidateRooms, onBind, onUnbind }: BindingPanelProps) {
  const [roomLabelId, setRoomLabelId] = useState('');
  const [target, setTarget] = useState<ElevationBinding['target']>('wallHeight');

  useEffect(() => {
    if (binding) {
      setRoomLabelId(binding.roomLabelId);
      setTarget(binding.target);
    }
  }, [binding]);

  return (
    <aside className="card elevation-binding-panel">
      <h3>Bind to room</h3>
      {!selectedWall && <p className="text-muted">Select a dimension line to bind it to a room's ceiling height.</p>}
      {selectedWall && candidateRooms.length === 0 && <p className="text-muted">No named rooms on this floor yet - name one in its floor plan first.</p>}
      {selectedWall && candidateRooms.length > 0 && (
        <div className="elevation-binding-form">
          <label>
            Room
            <select value={roomLabelId} onChange={(e) => setRoomLabelId(e.target.value)}>
              <option value="" disabled>
                Choose a room…
              </option>
              {candidateRooms.map(({ label, sketchName }) => (
                <option key={label.id} value={label.id}>
                  {label.name || '(unnamed room)'} — {sketchName}
                </option>
              ))}
            </select>
          </label>
          <label>
            Drives
            <select value={target} onChange={(e) => setTarget(e.target.value as ElevationBinding['target'])}>
              <option value="wallHeight">Wall / eave height</option>
              <option value="ridgeHeight">Ridge height</option>
            </select>
          </label>
          <div className="elevation-binding-actions">
            <button className="btn-primary" onClick={() => roomLabelId && onBind(roomLabelId, target)} disabled={!roomLabelId}>
              Bind
            </button>
            {binding && (
              <button className="btn-secondary" onClick={() => onUnbind(selectedWall.id)}>
                Unbind
              </button>
            )}
          </div>
        </div>
      )}

      <h3>All bindings</h3>
      {sketch.elevationBindings.length === 0 && <p className="text-muted">None yet.</p>}
      <ul className="elevation-binding-list">
        {sketch.elevationBindings.map((b) => {
          const room = findRoomLabel(project, b.roomLabelId);
          const wallLength = sketch.walls.find((w) => w.id === b.wallId)?.measuredLength;
          return (
            <li key={b.id}>
              {room?.name || '(unnamed room)'} · {b.target === 'wallHeight' ? 'wall height' : 'ridge height'}
              {wallLength !== undefined ? ` · ${wallLength.toFixed(2)} m` : ''}
              <button className="btn-secondary btn-small" onClick={() => onUnbind(b.wallId)}>
                Unbind
              </button>
            </li>
          );
        })}
      </ul>
    </aside>
  );
}

