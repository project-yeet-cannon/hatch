import { useMemo, useRef, useState } from 'react';
import type { MouseEvent as ReactMouseEvent } from 'react';
import { fitCamera, worldToScreen } from '../editor/camera';
import type { Camera } from '../editor/camera';
import { nearestPoint } from '../model/geometry';
import type { Point2, ProjectDocument, WallSegment } from '../model/schema';

interface SketchMergeViewProps {
  project: ProjectDocument;
  targetId: string;
  sourceId: string;
  onCancel: () => void;
  onConfirm: (correspondences: { source: Point2; target: Point2 }[]) => void;
}

const CANVAS_SIZE = { width: 320, height: 320 };
const VERTEX_HIT_PX = 12;

function collectVertices(walls: readonly WallSegment[]): Point2[] {
  const seen = new Map<string, Point2>();
  for (const wall of walls) {
    for (const p of [wall.start, wall.end]) {
      const key = `${Math.round(p.x / 1e-4)}:${Math.round(p.y / 1e-4)}`;
      if (!seen.has(key)) seen.set(key, p);
    }
  }
  return [...seen.values()];
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

/**
 * Pins point correspondences between two overlapping partial sketches of the
 * same floor so they can be merged into one (see model/schema.ts
 * withSketchesMerged and its rigid-transform fit in model/geometry.ts). Each
 * sketch renders in its own small static view (its own coordinate frame -
 * that's the whole point, they don't share an origin yet); clicking a vertex
 * in one and then the other pins a "this is the same corner" pair. At least
 * one pair is required, two or more also pin down rotation.
 */
export function SketchMergeView({ project, targetId, sourceId, onCancel, onConfirm }: SketchMergeViewProps) {
  const target = project.sketches.find((s) => s.id === targetId);
  const source = project.sketches.find((s) => s.id === sourceId);

  const [pairs, setPairs] = useState<{ source: Point2; target: Point2 }[]>([]);
  const [pendingTarget, setPendingTarget] = useState<Point2 | null>(null);
  const [pendingSource, setPendingSource] = useState<Point2 | null>(null);

  const targetVertices = useMemo(() => (target ? collectVertices(target.walls) : []), [target]);
  const sourceVertices = useMemo(() => (source ? collectVertices(source.walls) : []), [source]);

  const targetCamera = useMemo(() => {
    const { center, size } = boundsOf(targetVertices);
    return fitCamera(center, size, CANVAS_SIZE);
  }, [targetVertices]);
  const sourceCamera = useMemo(() => {
    const { center, size } = boundsOf(sourceVertices);
    return fitCamera(center, size, CANVAS_SIZE);
  }, [sourceVertices]);

  // Picking a point on one side completes a pair immediately against
  // whatever's still pending on the other side (using the just-picked point
  // directly, not the state we're about to set, since setState here doesn't
  // resolve before the very next click) - so a target/source click order
  // never leaves a stale pending point stranded.
  function pickTarget(p: Point2) {
    if (pendingSource) {
      setPairs((prev) => [...prev, { target: p, source: pendingSource }]);
      setPendingSource(null);
    } else {
      setPendingTarget(p);
    }
  }
  function pickSource(p: Point2) {
    if (pendingTarget) {
      setPairs((prev) => [...prev, { target: pendingTarget, source: p }]);
      setPendingTarget(null);
    } else {
      setPendingSource(p);
    }
  }

  if (!target || !source) return null;

  return (
    <div className="editor merge-view">
      <p className="text-muted editor-hint">
        Click the same corner in both sketches to pin a correspondence — at least one pair is required, two or more also fix rotation. "{target.name}" keeps its
        coordinates; "{source.name}" is transformed to match.
      </p>
      <div className="merge-canvases">
        <MergeCanvas title={target.name} walls={target.walls} vertices={targetVertices} camera={targetCamera} pending={pendingTarget} pinned={pairs.map((p) => p.target)} onPick={pickTarget} />
        <MergeCanvas title={source.name} walls={source.walls} vertices={sourceVertices} camera={sourceCamera} pending={pendingSource} pinned={pairs.map((p) => p.source)} onPick={pickSource} />
      </div>
      <div className="merge-pairs">
        {pairs.length === 0 && <p className="text-muted">No correspondences pinned yet.</p>}
        {pairs.map((pair, i) => (
          <div key={i} className="merge-pair-row">
            <span>
              Pair {i + 1}: ({pair.target.x.toFixed(2)}, {pair.target.y.toFixed(2)}) ↔ ({pair.source.x.toFixed(2)}, {pair.source.y.toFixed(2)})
            </span>
            <button className="btn-secondary btn-small" onClick={() => setPairs((prev) => prev.filter((_, idx) => idx !== i))}>
              Remove
            </button>
          </div>
        ))}
      </div>
      <div className="merge-actions">
        <button className="btn-secondary" onClick={onCancel}>
          Cancel
        </button>
        <button className="btn-primary" onClick={() => onConfirm(pairs)} disabled={pairs.length === 0}>
          Merge "{source.name}" into "{target.name}"
        </button>
      </div>
    </div>
  );
}

interface MergeCanvasProps {
  title: string;
  walls: readonly WallSegment[];
  vertices: readonly Point2[];
  camera: Camera;
  pending: Point2 | null;
  pinned: readonly Point2[];
  onPick: (p: Point2) => void;
}

function MergeCanvas({ title, walls, vertices, camera, pending, pinned, onPick }: MergeCanvasProps) {
  const svgRef = useRef<SVGSVGElement>(null);

  function handleClick(e: ReactMouseEvent<SVGSVGElement>) {
    const rect = svgRef.current!.getBoundingClientRect();
    const screen = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    const world = { x: (screen.x - camera.pan.x) / camera.zoom, y: (screen.y - camera.pan.y) / camera.zoom };
    const hit = nearestPoint(world, vertices, VERTEX_HIT_PX / camera.zoom);
    if (hit) onPick(hit);
  }

  return (
    <div className="merge-canvas-wrap">
      <h3>{title}</h3>
      <svg ref={svgRef} width={CANVAS_SIZE.width} height={CANVAS_SIZE.height} className="merge-canvas" onClick={handleClick}>
        {walls.map((wall) => {
          const s = worldToScreen(camera, wall.start);
          const e = worldToScreen(camera, wall.end);
          return <line key={wall.id} x1={s.x} y1={s.y} x2={e.x} y2={e.y} stroke="var(--ink)" strokeWidth={Math.max(2, wall.thickness * camera.zoom)} strokeLinecap="round" />;
        })}
        {vertices.map((v, i) => {
          const s = worldToScreen(camera, v);
          return <circle key={i} cx={s.x} cy={s.y} r={3.5} fill="var(--muted)" />;
        })}
        {pinned.map((p, i) => {
          const s = worldToScreen(camera, p);
          return <circle key={`pinned-${i}`} cx={s.x} cy={s.y} r={5} fill="none" stroke="var(--success)" strokeWidth={2} />;
        })}
        {pending && (
          <circle cx={worldToScreen(camera, pending).x} cy={worldToScreen(camera, pending).y} r={6} fill="none" stroke="var(--primary)" strokeWidth={2} />
        )}
      </svg>
    </div>
  );
}
