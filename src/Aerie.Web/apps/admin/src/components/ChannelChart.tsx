import { useMemo, useRef, useState } from 'react';
import type { ChannelHistoryPoint, ChannelStatePoint, DeviceChannelMetric } from '../types';
import { invertLinear, scaleLinear } from '../lib/scale';

const SERIES_COLORS = [
  'var(--series-1)',
  'var(--series-2)',
  'var(--series-3)',
  'var(--series-4)',
  'var(--series-5)',
  'var(--series-6)',
  'var(--series-7)',
  'var(--series-8)',
];

const WIDTH = 640;
const HEIGHT = 140;
const MARGIN_X = 12;
const MARGIN_TOP = 10;
const MARGIN_BOTTOM = 10;

interface ChannelChartProps {
  metric: DeviceChannelMetric;
  points: ChannelHistoryPoint[];
  states: ChannelStatePoint[];
  fromMs: number;
  toMs: number;
  onRangeSelect?: (fromMs: number, toMs: number) => void;
}

export function ChannelChart({ metric, points, states, fromMs, toMs, onRangeSelect }: ChannelChartProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [hoverX, setHoverX] = useState<number | null>(null);
  const [dragStartX, setDragStartX] = useState<number | null>(null);
  const [dragCurrentX, setDragCurrentX] = useState<number | null>(null);

  const xScale = useMemo(() => scaleLinear([fromMs, toMs], [MARGIN_X, WIDTH - MARGIN_X]), [fromMs, toMs]);
  const xInvert = useMemo(() => invertLinear([fromMs, toMs], [MARGIN_X, WIDTH - MARGIN_X]), [fromMs, toMs]);

  const values = points.map((p) => p.value);
  const yPad = values.length > 0 ? Math.max(0.5, (Math.max(...values) - Math.min(...values)) * 0.1) : 1;
  const yScale = useMemo(
    () =>
      scaleLinear(
        values.length > 0 ? [Math.min(...values) - yPad, Math.max(...values) + yPad] : [0, 1],
        [HEIGHT - MARGIN_BOTTOM, MARGIN_TOP],
      ),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [points],
  );

  const stateColors = useMemo(() => {
    const map = new Map<string, string>();
    for (const s of states) {
      if (!map.has(s.state)) map.set(s.state, SERIES_COLORS[map.size % SERIES_COLORS.length]);
    }
    return map;
  }, [states]);

  function svgXFromClientX(clientX: number): number | null {
    const rect = svgRef.current?.getBoundingClientRect();
    if (!rect || rect.width === 0) return null;
    return ((clientX - rect.left) / rect.width) * WIDTH;
  }

  function handlePointerMove(e: React.PointerEvent<SVGSVGElement>) {
    const x = svgXFromClientX(e.clientX);
    if (x === null) return;
    setHoverX(x);
    if (dragStartX !== null) setDragCurrentX(x);
  }

  function handlePointerDown(e: React.PointerEvent<SVGSVGElement>) {
    const x = svgXFromClientX(e.clientX);
    if (x === null) return;
    (e.target as Element).setPointerCapture(e.pointerId);
    setDragStartX(x);
    setDragCurrentX(x);
  }

  function handlePointerUp() {
    if (dragStartX !== null && dragCurrentX !== null && onRangeSelect) {
      const pixelSpan = Math.abs(dragCurrentX - dragStartX);
      if (pixelSpan > 6) {
        const lo = Math.min(dragStartX, dragCurrentX);
        const hi = Math.max(dragStartX, dragCurrentX);
        onRangeSelect(xInvert(lo), xInvert(hi));
      }
    }
    setDragStartX(null);
    setDragCurrentX(null);
  }

  function handlePointerLeave() {
    setHoverX(null);
    setDragStartX(null);
    setDragCurrentX(null);
  }

  const hoverTimeMs = hoverX !== null ? xInvert(hoverX) : null;

  const hoverInfo = useMemo(() => {
    if (hoverTimeMs === null) return null;
    if (points.length > 0) {
      let nearest = points[0];
      let best = Infinity;
      for (const p of points) {
        const d = Math.abs(new Date(p.time).getTime() - hoverTimeMs);
        if (d < best) {
          best = d;
          nearest = p;
        }
      }
      return { time: nearest.time, label: String(nearest.value), x: xScale(new Date(nearest.time).getTime()) };
    }
    if (states.length > 0) {
      let current = states[0];
      for (const s of states) {
        if (new Date(s.time).getTime() <= hoverTimeMs) current = s;
      }
      return { time: current.time, label: current.state, x: hoverX! };
    }
    return null;
  }, [hoverTimeMs, points, states, xScale, hoverX]);

  if (points.length === 0 && states.length === 0) {
    return (
      <div>
        <p className="text-muted mb-1">{metric}</p>
        <p className="text-muted" style={{ fontSize: 12 }}>
          No data in this range.
        </p>
      </div>
    );
  }

  const linePoints = points.map((p) => ({ x: xScale(new Date(p.time).getTime()), y: yScale(p.value) }));

  return (
    <div>
      <p className="text-muted mb-1">{metric}</p>
      <svg
        ref={svgRef}
        className="chart-svg"
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        preserveAspectRatio="none"
        onPointerMove={handlePointerMove}
        onPointerDown={handlePointerDown}
        onPointerUp={handlePointerUp}
        onPointerLeave={handlePointerLeave}
      >
        {states.length > 0 &&
          states.map((s, i) => {
            const startMs = new Date(s.time).getTime();
            const endMs = i + 1 < states.length ? new Date(states[i + 1].time).getTime() : toMs;
            const x = xScale(startMs);
            const width = Math.max(0, xScale(endMs) - x);
            return (
              <rect
                key={s.time}
                x={x}
                y={MARGIN_TOP}
                width={width}
                height={HEIGHT - MARGIN_TOP - MARGIN_BOTTOM}
                fill={stateColors.get(s.state)}
                opacity={0.85}
              />
            );
          })}

        {points.length > 0 && (
          <polyline
            className="chart-line"
            points={linePoints.map((p) => `${p.x},${p.y}`).join(' ')}
          />
        )}

        {dragStartX !== null && dragCurrentX !== null && (
          <rect
            className="chart-selection"
            x={Math.min(dragStartX, dragCurrentX)}
            y={0}
            width={Math.abs(dragCurrentX - dragStartX)}
            height={HEIGHT}
          />
        )}

        {hoverInfo && dragStartX === null && (
          <>
            <line className="chart-crosshair" x1={hoverInfo.x} y1={0} x2={hoverInfo.x} y2={HEIGHT} />
            <TooltipLabel x={hoverInfo.x} time={hoverInfo.time} label={hoverInfo.label} />
          </>
        )}
      </svg>
      {states.length > 0 && (
        <div className="chart-legend">
          {[...stateColors.entries()].map(([state, color]) => (
            <span className="chart-legend-item" key={state}>
              <span className="chart-legend-dot" style={{ background: color }} />
              {state}
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function TooltipLabel({ x, time, label }: { x: number; time: string; label: string }) {
  const text = `${new Date(time).toLocaleString()} · ${label}`;
  const boxWidth = Math.min(220, 8 + text.length * 5.2);
  const flip = x + boxWidth + 6 > WIDTH;
  const boxX = flip ? x - boxWidth - 6 : x + 6;

  return (
    <g>
      <rect className="chart-tooltip-bg" x={boxX} y={4} width={boxWidth} height={18} rx={3} />
      <text className="chart-tooltip-text" x={boxX + 6} y={17}>
        {text}
      </text>
    </g>
  );
}
