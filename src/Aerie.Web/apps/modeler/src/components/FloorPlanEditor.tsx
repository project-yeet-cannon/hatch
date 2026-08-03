import { useEffect, useMemo, useRef, useState } from 'react';
import type { PointerEvent as ReactPointerEvent, WheelEvent as ReactWheelEvent } from 'react';
import { defaultCamera, fitCamera, panBy, screenToWorld, worldToScreen, zoomAt } from '../editor/camera';
import type { Camera } from '../editor/camera';
import { resolveCeilingProfile } from '../model/elevation';
import { distance, nearestPoint, nearestPointOnSegment, snapDrawPoint } from '../model/geometry';
import { detectRooms, matchRoomLabel, pointInPolygon } from '../model/roomDetection';
import {
  createId,
  withDefaultWallThicknessSet,
  withOpeningAdded,
  withOpeningRemoved,
  withOpeningsForWallsRemoved,
  withOpeningUpdated,
  withRoomCeilingProfileSet,
  withRoomNamed,
  withRoomStairwellVoidSet,
  withVertexMoved,
  withWallAdded,
  withWallLengthSet,
  withWallPositionsUpdated,
  withWallSplit,
  withWallsRemoved,
} from '../model/schema';
import type { Opening, OpeningKind, Point2, ProjectDocument, RoomLabel, Sketch, WallSegment } from '../model/schema';
import { solveSketch } from '../model/solver';
import type { EdgeStatus } from '../model/solver';
import { RoomPanel } from './RoomPanel';

interface FloorPlanEditorProps {
  project: ProjectDocument;
  sketch: Sketch;
  update: (mutate: (project: ProjectDocument) => ProjectDocument) => void;
}

type Tool = 'wall' | 'select' | 'door' | 'archway';

const OPENING_HIT_PX = 10;
const DEFAULT_OPENING_WIDTH: Record<OpeningKind, number> = { door: 0.9, archway: 1.0 };
const DEFAULT_OPENING_HEAD_HEIGHT: Record<OpeningKind, number> = { door: 2.03, archway: 2.1 };

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

/** The point `offset` meters from `wall.start` along its centerline, clamped to the wall's own length. */
function pointAtOffset(wall: WallSegment, offset: number): Point2 {
  const length = distance(wall.start, wall.end);
  const t = length < 1e-9 ? 0 : Math.max(0, Math.min(1, offset / length));
  return { x: wall.start.x + (wall.end.x - wall.start.x) * t, y: wall.start.y + (wall.end.y - wall.start.y) * t };
}

function findOpeningHit(openings: readonly Opening[], walls: readonly WallSegment[], point: Point2, zoom: number): Opening | null {
  const wallsById = new Map(walls.map((w) => [w.id, w]));
  let best: Opening | null = null;
  let bestDist = OPENING_HIT_PX / zoom;
  for (const opening of openings) {
    const wall = wallsById.get(opening.wallId);
    if (!wall) continue;
    const d = distance(point, pointAtOffset(wall, opening.offset));
    if (d <= bestDist) {
      bestDist = d;
      best = opening;
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
  const [selectedOpeningId, setSelectedOpeningId] = useState<string | null>(null);
  const [roomPanelTarget, setRoomPanelTarget] = useState<Point2 | null>(null);
  const [showOnionSkin, setShowOnionSkin] = useState(true);
  const [draggingVertex, setDraggingVertex] = useState<Point2 | null>(null);
  const [dragPreview, setDragPreview] = useState<Point2 | null>(null);
  const [spacePressed, setSpacePressed] = useState(false);
  const [isPanning, setIsPanning] = useState(false);
  const panState = useRef<{ startScreen: Point2; startCamera: Camera } | null>(null);

  const rooms = useMemo(() => detectRooms(sketch.walls), [sketch.walls]);
  const vertices = useMemo(() => collectVertices(sketch.walls), [sketch.walls]);

  const floorBelow = useMemo(
    () => project.sketches.find((s) => s.kind === 'floorPlan' && s.floorIndex === sketch.floorIndex - 1),
    [project.sketches, sketch.floorIndex],
  );

  // The dimension solver: sketch.walls is the initial guess, measuredLength values are the known
  // inputs, and the result gives every wall a measured/derived/estimated status plus a solved
  // length for display. Solving is read-only here - it doesn't move the walls the user is
  // interacting with, only "Apply solved geometry" below writes the solved positions back.
  const solution = useMemo(() => solveSketch(sketch.walls), [sketch.walls]);
  const solvedRooms = useMemo(() => detectRooms(solution.walls), [solution.walls]);
  const totalArea = useMemo(() => solvedRooms.reduce((sum, room) => sum + room.area, 0), [solvedRooms]);

  const singleSelectedWall = selectedWallIds.size === 1 ? sketch.walls.find((w) => selectedWallIds.has(w.id)) ?? null : null;
  const selectedOpening = selectedOpeningId ? sketch.openings.find((o) => o.id === selectedOpeningId) ?? null : null;

  const roomPanelRoom = roomPanelTarget ? rooms.find((r) => pointInPolygon(roomPanelTarget, r.points)) : undefined;
  const roomPanelLabel: RoomLabel | undefined = roomPanelRoom ? matchRoomLabel(sketch.roomLabels, roomPanelRoom) : undefined;
  const roomPanelProfile = useMemo(
    () => (roomPanelLabel ? resolveCeilingProfile(project, roomPanelLabel.id) : resolveCeilingProfile(project, '__none__')),
    [project, roomPanelLabel],
  );

  // Reset drawing/selection state when switching sketches so stale ids from
  // a different wall set can't linger.
  useEffect(() => {
    setDrawStart(null);
    setSelectedWallIds(new Set());
    setSelectedOpeningId(null);
    setRoomPanelTarget(null);
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
        else {
          setSelectedWallIds(new Set());
          setSelectedOpeningId(null);
          setRoomPanelTarget(null);
        }
        return;
      }
      if ((e.key === 'Delete' || e.key === 'Backspace') && selectedWallIds.size > 0) {
        update((p) => withOpeningsForWallsRemoved(withWallsRemoved(p, sketch.id, [...selectedWallIds]), sketch.id, [...selectedWallIds]));
        setSelectedWallIds(new Set());
        return;
      }
      if ((e.key === 'Delete' || e.key === 'Backspace') && selectedOpeningId) {
        update((p) => withOpeningRemoved(p, sketch.id, selectedOpeningId));
        setSelectedOpeningId(null);
        return;
      }
      if (e.key === '1') setTool('wall');
      if (e.key === '2') setTool('select');
      if (e.key === '3') setTool('door');
      if (e.key === '4') setTool('archway');
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
  }, [drawStart, selectedWallIds, selectedOpeningId, sketch.id, update]);

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
      card: styles.getPropertyValue('--card').trim() || '#ffffff',
      danger: styles.getPropertyValue('--danger').trim() || '#d32f2f',
    };

    ctx.clearRect(0, 0, viewport.width, viewport.height);
    drawGrid(ctx, camera, viewport, colors.line);

    if (showOnionSkin && floorBelow) {
      for (const wall of floorBelow.walls) drawOnionWall(ctx, camera, wall, colors);
    }

    for (const room of rooms) {
      const label = matchRoomLabel(sketch.roomLabels, room);
      drawRoom(ctx, camera, room, label, colors);
    }

    for (const wall of sketch.walls) {
      drawWall(ctx, camera, wall, selectedWallIds.has(wall.id), solution.statuses.get(wall.id), solution.lengths.get(wall.id), colors);
    }

    const wallsById = new Map(sketch.walls.map((w) => [w.id, w]));
    for (const opening of sketch.openings) {
      const wall = wallsById.get(opening.wallId);
      if (!wall) continue;
      drawOpening(ctx, camera, wall, opening, opening.id === selectedOpeningId, colors);
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

    if ((tool === 'door' || tool === 'archway') && cursorWorld) {
      const hoverWall = findWallHit(sketch.walls, cursorWorld, camera.zoom);
      if (hoverWall) {
        const hit = nearestPointOnSegment(cursorWorld, hoverWall.start, hoverWall.end);
        const previewOpening: Opening = {
          id: '__preview__',
          wallId: hoverWall.id,
          offset: hit.t * distance(hoverWall.start, hoverWall.end),
          width: DEFAULT_OPENING_WIDTH[tool],
          headHeight: DEFAULT_OPENING_HEAD_HEIGHT[tool],
          kind: tool,
        };
        drawOpening(ctx, camera, hoverWall, previewOpening, false, colors, 0.55);
      }
    }
  }, [
    camera,
    viewport,
    sketch.walls,
    sketch.roomLabels,
    sketch.openings,
    rooms,
    vertices,
    selectedWallIds,
    selectedOpeningId,
    draggingVertex,
    dragPreview,
    tool,
    drawStart,
    cursorWorld,
    project.settings.defaultWallThickness,
    solution,
    showOnionSkin,
    floorBelow,
  ]);

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

    if (tool === 'door' || tool === 'archway') {
      const hitWall = findWallHit(sketch.walls, rawWorld, camera.zoom);
      if (!hitWall) return;
      const hit = nearestPointOnSegment(rawWorld, hitWall.start, hitWall.end);
      const opening: Opening = {
        id: createId(),
        wallId: hitWall.id,
        offset: hit.t * distance(hitWall.start, hitWall.end),
        width: DEFAULT_OPENING_WIDTH[tool],
        headHeight: DEFAULT_OPENING_HEAD_HEIGHT[tool],
        kind: tool,
      };
      update((p) => withOpeningAdded(p, sketch.id, opening));
      setSelectedOpeningId(opening.id);
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

    const hitOpening = findOpeningHit(sketch.openings, sketch.walls, rawWorld, camera.zoom);
    if (hitOpening) {
      setSelectedOpeningId(hitOpening.id);
      setSelectedWallIds(new Set());
      return;
    }

    const hitWall = findWallHit(sketch.walls, rawWorld, camera.zoom);
    if (hitWall) {
      setSelectedOpeningId(null);
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
      setRoomPanelTarget(room.centroid);
      return;
    }

    setSelectedWallIds(new Set());
    setSelectedOpeningId(null);
    setRoomPanelTarget(null);
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
          <button className={tool === 'door' ? 'btn-primary' : 'btn-secondary'} onClick={() => setTool('door')}>
            Door
          </button>
          <button className={tool === 'archway' ? 'btn-primary' : 'btn-secondary'} onClick={() => setTool('archway')}>
            Archway
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
            update((p) => withOpeningsForWallsRemoved(withWallsRemoved(p, sketch.id, [...selectedWallIds]), sketch.id, [...selectedWallIds]));
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
        {selectedOpening && (
          <OpeningInspector
            opening={selectedOpening}
            onChange={(patch) => update((p) => withOpeningUpdated(p, sketch.id, selectedOpening.id, patch))}
            onDelete={() => {
              update((p) => withOpeningRemoved(p, sketch.id, selectedOpening.id));
              setSelectedOpeningId(null);
            }}
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
        {floorBelow && (
          <label className="editor-onion-toggle">
            <input type="checkbox" checked={showOnionSkin} onChange={(e) => setShowOnionSkin(e.target.checked)} />
            Show {floorBelow.name} below
          </label>
        )}
        <span className="text-muted editor-status">
          {sketch.walls.length} wall{sketch.walls.length === 1 ? '' : 's'} · {sketch.openings.length} opening{sketch.openings.length === 1 ? '' : 's'} ·{' '}
          {rooms.length} room{rooms.length === 1 ? '' : 's'} detected · {totalArea.toFixed(1)} m² total (solved)
        </span>
      </div>
      <p className="text-muted editor-hint">
        {tool === 'wall' &&
          'Click to start a wall, click again to place each corner (chains continue automatically). Esc cancels. Space-drag or middle-drag to pan, scroll to zoom.'}
        {tool === 'select' &&
          'Drag a corner to move it, click a wall to select and set its real length (Del to remove), click a door/archway to edit it, click inside a room to open its panel. Green = measured, blue = derived, amber = estimated.'}
        {(tool === 'door' || tool === 'archway') && `Click a wall to place a ${tool}. Select it afterward (tool 2) to adjust width, position, and head height.`}
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
        {roomPanelTarget && roomPanelRoom && (
          <RoomPanel
            screenPosition={worldToScreen(camera, roomPanelRoom.centroid)}
            name={roomPanelLabel?.name ?? ''}
            profile={roomPanelProfile}
            stairwellVoid={roomPanelLabel?.stairwellVoid ?? false}
            onSetName={(name) => update((p) => withRoomNamed(p, sketch.id, roomPanelLabel?.id ?? null, roomPanelRoom.centroid, name))}
            onSetCeilingProfile={(kind, wallHeight, ridgeHeight) =>
              update((p) => withRoomCeilingProfileSet(p, sketch.id, roomPanelLabel?.id ?? null, roomPanelRoom.centroid, { kind, wallHeight, ridgeHeight }))
            }
            onSetStairwellVoid={(value) => update((p) => withRoomStairwellVoidSet(p, sketch.id, roomPanelLabel?.id ?? null, roomPanelRoom.centroid, value))}
            onClose={() => setRoomPanelTarget(null)}
          />
        )}
      </div>
    </div>
  );
}

interface OpeningInspectorProps {
  opening: Opening;
  onChange: (patch: Partial<Pick<Opening, 'width' | 'headHeight'>>) => void;
  onDelete: () => void;
}

/** Shown in the toolbar when a single door/archway is selected: width and head height, plus delete. */
function OpeningInspector({ opening, onChange, onDelete }: OpeningInspectorProps) {
  const [width, setWidth] = useState(opening.width.toFixed(2));
  const [headHeight, setHeadHeight] = useState(opening.headHeight.toFixed(2));

  useEffect(() => setWidth(opening.width.toFixed(2)), [opening.id, opening.width]);
  useEffect(() => setHeadHeight(opening.headHeight.toFixed(2)), [opening.id, opening.headHeight]);

  function commitWidth() {
    const value = Number(width);
    if (Number.isFinite(value) && value > 0) onChange({ width: value });
  }
  function commitHeadHeight() {
    const value = Number(headHeight);
    if (Number.isFinite(value) && value > 0) onChange({ headHeight: value });
  }

  return (
    <label className="editor-dimension" title={opening.kind === 'door' ? 'Door' : 'Archway'}>
      {opening.kind === 'door' ? 'Door' : 'Archway'} width / head (m)
      <input type="number" min={0.3} step={0.01} value={width} onChange={(e) => setWidth(e.target.value)} onBlur={commitWidth} onKeyDown={(e) => e.key === 'Enter' && commitWidth()} />
      <input
        type="number"
        min={1.5}
        step={0.01}
        value={headHeight}
        onChange={(e) => setHeadHeight(e.target.value)}
        onBlur={commitHeadHeight}
        onKeyDown={(e) => e.key === 'Enter' && commitHeadHeight()}
      />
      <button className="btn-secondary" onClick={onDelete}>
        Delete
      </button>
    </label>
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
  card: string;
  danger: string;
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
  label: RoomLabel | undefined,
  colors: CanvasColors,
) {
  ctx.beginPath();
  room.points.forEach((p, i) => {
    const s = worldToScreen(camera, p);
    if (i === 0) ctx.moveTo(s.x, s.y);
    else ctx.lineTo(s.x, s.y);
  });
  ctx.closePath();
  ctx.fillStyle = label?.stairwellVoid ? colors.danger + '1a' : colors.primaryBg;
  ctx.fill();

  if (label?.stairwellVoid) {
    ctx.save();
    ctx.clip();
    ctx.strokeStyle = colors.danger;
    ctx.globalAlpha = 0.35;
    ctx.lineWidth = 1;
    const bounds = boundsOf(room.points);
    const topLeft = worldToScreen(camera, { x: bounds.center.x - bounds.size.width, y: bounds.center.y - bounds.size.height });
    const bottomRight = worldToScreen(camera, { x: bounds.center.x + bounds.size.width, y: bounds.center.y + bounds.size.height });
    const step = 14;
    ctx.beginPath();
    for (let x = topLeft.x - (bottomRight.y - topLeft.y); x < bottomRight.x; x += step) {
      ctx.moveTo(x, topLeft.y);
      ctx.lineTo(x + (bottomRight.y - topLeft.y), bottomRight.y);
    }
    ctx.stroke();
    ctx.restore();
  }

  const name = label?.name;
  const displayLabel = name && name.length > 0 ? name : 'Click to name';
  const center = worldToScreen(camera, room.centroid);
  ctx.font = '13px Manrope, sans-serif';
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillStyle = name ? colors.ink : colors.muted;
  ctx.fillText(displayLabel, center.x, center.y - 7);
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

/** The floor-below's walls, drawn faint and dashed for alignment reference (see FloorPlanEditor's onion-skin toggle). */
function drawOnionWall(ctx: CanvasRenderingContext2D, camera: Camera, wall: WallSegment, colors: CanvasColors) {
  const start = worldToScreen(camera, wall.start);
  const end = worldToScreen(camera, wall.end);
  ctx.save();
  ctx.globalAlpha = 0.35;
  ctx.strokeStyle = colors.muted;
  ctx.setLineDash([6, 4]);
  ctx.lineWidth = Math.max(1.5, wall.thickness * camera.zoom * 0.6);
  ctx.lineCap = 'round';
  ctx.beginPath();
  ctx.moveTo(start.x, start.y);
  ctx.lineTo(end.x, end.y);
  ctx.stroke();
  ctx.restore();
}

/** A door/archway: erases the wall stroke across its span, draws end jambs, and (for doors) a swing arc. `alpha` lets the placement tool draw a faint preview before committing. */
function drawOpening(ctx: CanvasRenderingContext2D, camera: Camera, wall: WallSegment, opening: Opening, selected: boolean, colors: CanvasColors, alpha = 1) {
  const wallLen = distance(wall.start, wall.end);
  if (wallLen < 1e-6) return;
  const dir = { x: (wall.end.x - wall.start.x) / wallLen, y: (wall.end.y - wall.start.y) / wallLen };
  const perp = { x: -dir.y, y: dir.x };
  const half = Math.min(opening.width / 2, wallLen / 2);
  const center = pointAtOffset(wall, opening.offset);
  const p1 = { x: center.x - dir.x * half, y: center.y - dir.y * half };
  const p2 = { x: center.x + dir.x * half, y: center.y + dir.y * half };
  const s1 = worldToScreen(camera, p1);
  const s2 = worldToScreen(camera, p2);

  ctx.save();
  ctx.globalAlpha = alpha;

  ctx.strokeStyle = colors.card;
  ctx.lineCap = 'butt';
  ctx.lineWidth = wall.thickness * camera.zoom + 4;
  ctx.beginPath();
  ctx.moveTo(s1.x, s1.y);
  ctx.lineTo(s2.x, s2.y);
  ctx.stroke();

  const strokeColor = selected ? colors.primary : colors.ink;
  ctx.strokeStyle = strokeColor;
  ctx.lineWidth = 1.5;

  const jamb = (wall.thickness / 2) * camera.zoom + 2;
  for (const s of [s1, s2]) {
    ctx.beginPath();
    ctx.moveTo(s.x - perp.x * jamb, s.y - perp.y * jamb);
    ctx.lineTo(s.x + perp.x * jamb, s.y + perp.y * jamb);
    ctx.stroke();
  }

  if (opening.kind === 'door') {
    const radiusScreen = distance(p1, p2) * camera.zoom;
    const startAngle = Math.atan2(dir.y, dir.x);
    const endAngle = Math.atan2(perp.y, perp.x);
    ctx.beginPath();
    ctx.arc(s1.x, s1.y, radiusScreen, startAngle, endAngle);
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(s1.x, s1.y);
    ctx.lineTo(s1.x + perp.x * radiusScreen, s1.y + perp.y * radiusScreen);
    ctx.stroke();
  } else {
    ctx.setLineDash([4, 3]);
    ctx.beginPath();
    ctx.moveTo(s1.x, s1.y);
    ctx.lineTo(s2.x, s2.y);
    ctx.stroke();
    ctx.setLineDash([]);
  }

  ctx.restore();
}
