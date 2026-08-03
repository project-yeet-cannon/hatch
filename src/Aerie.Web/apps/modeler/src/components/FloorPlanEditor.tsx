import { useEffect, useMemo, useRef, useState } from 'react';
import type { PointerEvent as ReactPointerEvent, WheelEvent as ReactWheelEvent } from 'react';
import { defaultCamera, fitCamera, panBy, screenToWorld, worldToScreen, zoomAt } from '../editor/camera';
import type { Camera } from '../editor/camera';
import { distance, nearestPoint, nearestPointOnSegment, snapDrawPoint } from '../model/geometry';
import { detectRooms, matchRoomLabel, pointInPolygon } from '../model/roomDetection';
import {
  createId,
  withDefaultWallThicknessSet,
  withRoomNamed,
  withVertexMoved,
  withWallAdded,
  withWallLengthSet,
  withWallPositionsUpdated,
  withWallSplit,
  withWallsRemoved,
} from '../model/schema';
import type { Point2, ProjectDocument, Sketch, WallSegment } from '../model/schema';
import { solveSketch } from '../model/solver';
import type { EdgeStatus } from '../model/solver';

interface FloorPlanEditorProps {
  project: ProjectDocument;
  sketch: Sketch;
  update: (mutate: (project: ProjectDocument) => ProjectDocument) => void;
}

type Tool = 'wall' | 'select';

const VERTEX_HIT_PX = 9;
const WALL_HIT_PX = 6;
const EDGE_SNAP_PX = 10;
const MIN_WALL_LENGTH = 0.05;
const VERTEX_EPSILON = 1e-4;

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

function findWallHit(walls: readonly WallSegment[], point: Point2, zoom: number): WallSegment | null {
  let best: WallSegment | null = null;
  let bestDist = Infinity;
  for (const wall of walls) {
    const { distance: d } = nearestPointOnSegment(point, wall.start, wall.end);
    const tolerance = wall.thickness / 2 + WALL_HIT_PX / zoom;
    if (d <= tolerance && d < bestDist) {
      bestDist = d;
      best = wall;
    }
  }
  return best;
}

/** A point landing mid-span of an existing wall (a T-junction), not near either of its own endpoints. */
function findEdgeSnap(walls: readonly WallSegment[], point: Point2, tolerance: number): { wallId: string; point: Point2 } | null {
  let best: { wallId: string; point: Point2; dist: number } | null = null;
  for (const wall of walls) {
    const hit = nearestPointOnSegment(point, wall.start, wall.end);
    if (hit.t < 0.02 || hit.t > 0.98) continue;
    if (hit.distance <= tolerance && (!best || hit.distance < best.dist)) {
      best = { wallId: wall.id, point: hit.point, dist: hit.distance };
    }
  }
  return best ? { wallId: best.wallId, point: best.point } : null;
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
  return tag === 'INPUT' || tag === 'TEXTAREA';
}

export function FloorPlanEditor({ project, sketch, update }: FloorPlanEditorProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);

  const [viewport, setViewport] = useState({ width: 0, height: 0 });
  const [camera, setCamera] = useState<Camera>(() => defaultCamera());
  const didInitialFit = useRef(false);

  const [tool, setTool] = useState<Tool>('wall');
  const [drawStart, setDrawStart] = useState<Point2 | null>(null);
  const [cursorWorld, setCursorWorld] = useState<Point2 | null>(null);
  const [selectedWallIds, setSelectedWallIds] = useState<Set<string>>(new Set());
  const [draggingVertex, setDraggingVertex] = useState<Point2 | null>(null);
  const [dragPreview, setDragPreview] = useState<Point2 | null>(null);
  const [spacePressed, setSpacePressed] = useState(false);
  const [isPanning, setIsPanning] = useState(false);
  const panState = useRef<{ startScreen: Point2; startCamera: Camera } | null>(null);

  const rooms = useMemo(() => detectRooms(sketch.walls), [sketch.walls]);
  const vertices = useMemo(() => collectVertices(sketch.walls), [sketch.walls]);

  // The dimension solver: sketch.walls is the initial guess, measuredLength values are the known
  // inputs, and the result gives every wall a measured/derived/estimated status plus a solved
  // length for display. Solving is read-only here - it doesn't move the walls the user is
  // interacting with, only "Apply solved geometry" below writes the solved positions back.
  const solution = useMemo(() => solveSketch(sketch.walls), [sketch.walls]);
  const solvedRooms = useMemo(() => detectRooms(solution.walls), [solution.walls]);
  const totalArea = useMemo(() => solvedRooms.reduce((sum, room) => sum + room.area, 0), [solvedRooms]);

  const singleSelectedWall = selectedWallIds.size === 1 ? sketch.walls.find((w) => selectedWallIds.has(w.id)) ?? null : null;

  // Reset drawing/selection state when switching sketches so stale ids from
  // a different wall set can't linger.
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
    setCamera(fitCamera(center, vertices.length > 0 ? size : { width: 0, height: 0 }, viewport));
    // Runs once per sketch (guarded by didInitialFit) as soon as the viewport has a real size.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [viewport]);

  const zoomToFit = () => {
    const { center, size } = boundsOf(vertices);
    setCamera(fitCamera(center, vertices.length > 0 ? size : { width: 0, height: 0 }, viewport));
  };

  // Keyboard shortcuts: tool switching, delete selection, escape to cancel a
  // wall chain or clear selection, space-hold to pan.
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
        update((p) => withWallsRemoved(p, sketch.id, [...selectedWallIds]));
        setSelectedWallIds(new Set());
        return;
      }
      if (e.key === '1') setTool('wall');
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

  // Render loop: redraw whenever anything visible changes.
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
      primaryBg: styles.getPropertyValue('--primary-bg').trim() || 'rgba(0,102,204,0.08)',
      success: styles.getPropertyValue('--success').trim() || '#388e3c',
      warning: styles.getPropertyValue('--warning').trim() || '#b58900',
    };

    ctx.clearRect(0, 0, viewport.width, viewport.height);
    drawGrid(ctx, camera, viewport, colors.line);

    for (const room of rooms) {
      const label = matchRoomLabel(sketch.roomLabels, room);
      drawRoom(ctx, camera, room, label?.name, colors);
    }

    for (const wall of sketch.walls) {
      drawWall(ctx, camera, wall, selectedWallIds.has(wall.id), solution.statuses.get(wall.id), solution.lengths.get(wall.id), colors);
    }

    for (const vertex of vertices) {
      const isDragging = draggingVertex !== null && pointsClose(vertex, draggingVertex);
      if (isDragging) continue;
      drawVertex(ctx, camera, vertex, false, colors);
    }

    if (draggingVertex) {
      drawVertex(ctx, camera, dragPreview ?? draggingVertex, true, colors);
    }

    if (tool === 'wall' && drawStart && cursorWorld) {
      const vertexTolerance = VERTEX_HIT_PX / camera.zoom;
      const snap = snapDrawPoint(cursorWorld, drawStart, vertices, vertexTolerance);
      drawGhostWall(ctx, camera, drawStart, snap.point, project.settings.defaultWallThickness, colors);
    }
  }, [camera, viewport, sketch.walls, sketch.roomLabels, rooms, vertices, selectedWallIds, draggingVertex, dragPreview, tool, drawStart, cursorWorld, project.settings.defaultWallThickness, solution]);

  function getCanvasPoint(e: ReactPointerEvent | ReactWheelEvent): Point2 {
    const rect = canvasRef.current!.getBoundingClientRect();
    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
  }

  function placeWallPoint(rawWorld: Point2): Point2 {
    const vertexTolerance = VERTEX_HIT_PX / camera.zoom;
    const snap = snapDrawPoint(rawWorld, drawStart, vertices, vertexTolerance);
    if (snap.kind !== 'vertex') {
      const edgeTolerance = EDGE_SNAP_PX / camera.zoom;
      const edgeHit = findEdgeSnap(sketch.walls, snap.point, edgeTolerance);
      if (edgeHit) {
        update((p) => withWallSplit(p, sketch.id, edgeHit.wallId, edgeHit.point));
        return edgeHit.point;
      }
    }
    return snap.point;
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
    const rawWorld = screenToWorld(camera, screen);

    if (tool === 'wall') {
      const point = placeWallPoint(rawWorld);
      if (drawStart === null) {
        setDrawStart(point);
        return;
      }
      if (distance(drawStart, point) < MIN_WALL_LENGTH) {
        setDrawStart(null);
        return;
      }
      const wall: WallSegment = { id: createId(), start: drawStart, end: point, thickness: project.settings.defaultWallThickness };
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

    const hitWall = findWallHit(sketch.walls, rawWorld, camera.zoom);
    if (hitWall) {
      setSelectedWallIds((prev) => {
        const next = e.shiftKey ? new Set(prev) : new Set<string>();
        if (next.has(hitWall.id)) next.delete(hitWall.id);
        else next.add(hitWall.id);
        return next;
      });
      return;
    }

    const room = rooms.find((r) => pointInPolygon(rawWorld, r.points));
    if (room) {
      const label = matchRoomLabel(sketch.roomLabels, room);
      const name = window.prompt('Room name', label?.name ?? '');
      if (name != null && name.trim().length > 0) {
        update((p) => withRoomNamed(p, sketch.id, label?.id ?? null, room.centroid, name.trim()));
      }
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
    const rawWorld = screenToWorld(camera, screen);
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

  const cursorStyle = isPanning ? 'grabbing' : spacePressed ? 'grab' : tool === 'wall' ? 'crosshair' : 'default';

  return (
    <div className="editor">
      <div className="editor-toolbar">
        <div className="editor-tool-group">
          <button className={tool === 'wall' ? 'btn-primary' : 'btn-secondary'} onClick={() => setTool('wall')}>
            Draw wall
          </button>
          <button className={tool === 'select' ? 'btn-primary' : 'btn-secondary'} onClick={() => setTool('select')}>
            Select / move
          </button>
        </div>
        <label className="editor-thickness">
          Wall thickness (m)
          <input
            type="number"
            min={0.05}
            max={1}
            step={0.01}
            value={project.settings.defaultWallThickness}
            onChange={(e) => {
              const value = Number(e.target.value);
              if (Number.isFinite(value) && value > 0) update((p) => withDefaultWallThicknessSet(p, value));
            }}
          />
        </label>
        <button className="btn-secondary" onClick={zoomToFit}>
          Zoom to fit
        </button>
        <button
          className="btn-secondary"
          onClick={() => {
            update((p) => withWallsRemoved(p, sketch.id, [...selectedWallIds]));
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
        <span className="text-muted editor-status">
          {sketch.walls.length} wall{sketch.walls.length === 1 ? '' : 's'} · {rooms.length} room{rooms.length === 1 ? '' : 's'} detected ·{' '}
          {totalArea.toFixed(1)} m² total (solved)
        </span>
      </div>
      <p className="text-muted editor-hint">
        {tool === 'wall'
          ? 'Click to start a wall, click again to place each corner (chains continue automatically). Esc cancels. Space-drag or middle-drag to pan, scroll to zoom.'
          : 'Drag a corner to move it, click a wall to select and set its real length (Del to remove), click inside a room to name it. Green = measured, blue = derived from other measurements, amber = estimated from the sketch.'}
      </p>
      <div ref={containerRef} className="editor-canvas-container">
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
    </div>
  );
}

interface DimensionInputProps {
  wall: WallSegment;
  solvedLength: number;
  status: EdgeStatus | undefined;
  onSet: (length: number | null) => void;
}

/** Shown in the toolbar when exactly one wall is selected: lets the user type its real-world length (a "measurement") or clear a previously entered one back to a solver estimate. */
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
      Wall length (m)
      <input
        type="number"
        min={0.05}
        step={0.01}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === 'Enter') commit();
        }}
      />
      {wall.measuredLength !== undefined && (
        <button className="btn-secondary" onClick={() => onSet(null)}>
          Clear
        </button>
      )}
    </label>
  );
}

interface CanvasColors {
  ink: string;
  muted: string;
  line: string;
  primary: string;
  primaryBg: string;
  success: string;
  warning: string;
}

function statusColor(status: EdgeStatus | undefined, colors: CanvasColors): string {
  if (status === 'measured') return colors.success;
  if (status === 'derived') return colors.primary;
  if (status === 'estimated') return colors.warning;
  return colors.ink;
}

function drawGrid(ctx: CanvasRenderingContext2D, camera: Camera, viewport: { width: number; height: number }, lineColor: string) {
  const topLeft = screenToWorld(camera, { x: 0, y: 0 });
  const bottomRight = screenToWorld(camera, { x: viewport.width, y: viewport.height });
  let step = 1;
  if (camera.zoom < 15) step = 5;
  if (camera.zoom < 4) step = 20;

  ctx.strokeStyle = lineColor;
  ctx.lineWidth = 1;
  ctx.beginPath();
  const startX = Math.floor(topLeft.x / step) * step;
  for (let x = startX; x <= bottomRight.x; x += step) {
    const sx = worldToScreen(camera, { x, y: 0 }).x;
    ctx.moveTo(sx, 0);
    ctx.lineTo(sx, viewport.height);
  }
  const startY = Math.floor(topLeft.y / step) * step;
  for (let y = startY; y <= bottomRight.y; y += step) {
    const sy = worldToScreen(camera, { x: 0, y }).y;
    ctx.moveTo(0, sy);
    ctx.lineTo(viewport.width, sy);
  }
  ctx.stroke();
}

function drawRoom(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  room: { points: Point2[]; area: number; centroid: Point2 },
  name: string | undefined,
  colors: CanvasColors,
) {
  ctx.beginPath();
  room.points.forEach((p, i) => {
    const s = worldToScreen(camera, p);
    if (i === 0) ctx.moveTo(s.x, s.y);
    else ctx.lineTo(s.x, s.y);
  });
  ctx.closePath();
  ctx.fillStyle = colors.primaryBg;
  ctx.fill();

  const label = name ?? 'Click to name';
  const center = worldToScreen(camera, room.centroid);
  ctx.font = '13px Manrope, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillStyle = name ? colors.ink : colors.muted;
  ctx.fillText(label, center.x, center.y - 7);
  ctx.fillStyle = colors.muted;
  ctx.font = '11px Manrope, sans-serif';
  ctx.fillText(`${room.area.toFixed(1)} m²`, center.x, center.y + 9);
}

function drawWall(
  ctx: CanvasRenderingContext2D,
  camera: Camera,
  wall: WallSegment,
  selected: boolean,
  status: EdgeStatus | undefined,
  solvedLength: number | undefined,
  colors: CanvasColors,
) {
  const start = worldToScreen(camera, wall.start);
  const end = worldToScreen(camera, wall.end);
  ctx.strokeStyle = selected ? colors.primary : statusColor(status, colors);
  ctx.lineWidth = selected ? Math.max(3, wall.thickness * camera.zoom + 1) : Math.max(2, wall.thickness * camera.zoom);
  ctx.lineCap = 'round';
  ctx.beginPath();
  ctx.moveTo(start.x, start.y);
  ctx.lineTo(end.x, end.y);
  ctx.stroke();

  if (solvedLength === undefined) return;
  const mid = { x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 };
  ctx.font = '11px Manrope, sans-serif';
  ctx.fillStyle = statusColor(status, colors);
  ctx.textAlign = 'center';
  ctx.textBaseline = 'bottom';
  ctx.fillText(`${solvedLength.toFixed(2)} m`, mid.x, mid.y - 4);
}

function drawVertex(ctx: CanvasRenderingContext2D, camera: Camera, point: Point2, active: boolean, colors: CanvasColors) {
  const s = worldToScreen(camera, point);
  ctx.beginPath();
  ctx.arc(s.x, s.y, active ? 6 : 3.5, 0, Math.PI * 2);
  ctx.fillStyle = active ? colors.primary : colors.muted;
  ctx.fill();
}

function drawGhostWall(ctx: CanvasRenderingContext2D, camera: Camera, start: Point2, end: Point2, thickness: number, colors: CanvasColors) {
  const s = worldToScreen(camera, start);
  const e = worldToScreen(camera, end);
  ctx.save();
  ctx.globalAlpha = 0.6;
  ctx.strokeStyle = colors.primary;
  ctx.lineWidth = Math.max(2, thickness * camera.zoom);
  ctx.lineCap = 'round';
  ctx.beginPath();
  ctx.moveTo(s.x, s.y);
  ctx.lineTo(e.x, e.y);
  ctx.stroke();
  ctx.restore();

  const length = distance(start, end);
  const mid = { x: (s.x + e.x) / 2, y: (s.y + e.y) / 2 };
  ctx.font = '12px Manrope, sans-serif';
  ctx.fillStyle = colors.success;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'bottom';
  ctx.fillText(`${length.toFixed(2)} m`, mid.x, mid.y - 6);

  ctx.beginPath();
  ctx.arc(e.x, e.y, 4, 0, Math.PI * 2);
  ctx.fillStyle = colors.primary;
  ctx.fill();
}
