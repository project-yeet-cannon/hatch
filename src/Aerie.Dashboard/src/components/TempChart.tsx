import type { ComfortRange, ComfortStatus, TempPoint } from '../types';
import { scaleLinear, toPolylinePoints } from '../lib/scale';
import { formatAxisHour } from '../lib/format';

interface TempChartProps {
  history: TempPoint[];
  forecast: TempPoint[];
  comfortRange?: ComfortRange;
  status: ComfortStatus;
  /** Compact renders a sparkline (no axis, no comfort band); default renders the full chart. */
  compact?: boolean;
}

const STATUS_CLASS: Record<ComfortStatus, string> = {
  warm: 'g-warm',
  cool: 'g-cool',
  comfortable: 'g-ok',
};

export function TempChart({ history, forecast, comfortRange, status, compact }: TempChartProps) {
  const width = compact ? 300 : 440;
  const height = compact ? 60 : 150;
  const marginTop = compact ? 4 : 8;
  const marginBottom = compact ? 4 : 8;

  const allPoints = [...history, ...forecast];

  // No readings yet (e.g. a freshly configured zone, or outside before a
  // weather integration is wired up): render an empty chart rather than
  // producing NaN geometry from empty min/max.
  if (allPoints.length === 0) {
    return <svg className={compact ? 'hf-spark' : 'hf-chart'} viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" />;
  }

  const times = allPoints.map((p) => new Date(p.time).getTime());
  const temps = allPoints.map((p) => p.tempF).concat(
    comfortRange ? [comfortRange.lowF, comfortRange.highF] : [],
  );

  const xScale = scaleLinear([Math.min(...times), Math.max(...times)], [10, width - 10]);
  const yScale = scaleLinear(
    [Math.min(...temps) - 1, Math.max(...temps) + 1],
    [height - marginBottom, marginTop],
  );

  const actualPoints = history.map((p) => ({ x: xScale(new Date(p.time).getTime()), y: yScale(p.tempF) }));
  const forecastPoints = forecast.map((p) => ({ x: xScale(new Date(p.time).getTime()), y: yScale(p.tempF) }));
  const nowX = actualPoints[actualPoints.length - 1]?.x ?? forecastPoints[0]?.x;

  const statusClass = STATUS_CLASS[status];

  return (
    <svg
      className={compact ? 'hf-spark' : 'hf-chart'}
      viewBox={`0 0 ${width} ${height}`}
      preserveAspectRatio="none"
    >
      {!compact && comfortRange && (
        <>
          <rect className="g-band" x={0} y={yScale(comfortRange.highF)} width={width} height={yScale(comfortRange.lowF) - yScale(comfortRange.highF)} />
          <line className="g-grid" x1={0} y1={yScale(comfortRange.highF)} x2={width} y2={yScale(comfortRange.highF)} />
          <line className="g-grid" x1={0} y1={yScale(comfortRange.lowF)} x2={width} y2={yScale(comfortRange.lowF)} />
        </>
      )}
      {compact && comfortRange && (
        <rect className="g-band" x={0} y={yScale(comfortRange.highF)} width={width} height={yScale(comfortRange.lowF) - yScale(comfortRange.highF)} />
      )}
      <polyline className="g-actual" points={toPolylinePoints(actualPoints)} />
      <polyline className={`g-fore ${statusClass}`} points={toPolylinePoints(forecastPoints)} />
      {!compact && nowX !== undefined && <line className="g-now" x1={nowX} y1={0} x2={nowX} y2={height} />}
    </svg>
  );
}

interface AxisProps {
  history: TempPoint[];
  forecast: TempPoint[];
  timeZone: string;
}

export function TempChartAxis({ history, forecast, timeZone }: AxisProps) {
  const all = [...history, ...forecast];
  if (all.length === 0) return <div className="hf-xax" />;

  const at = (fraction: number) => all[Math.min(all.length - 1, Math.max(0, Math.round((all.length - 1) * fraction)))];
  const now = history[history.length - 1] ?? forecast[0];

  return (
    <div className="hf-xax">
      <span>{formatAxisHour(at(0).time, timeZone)}</span>
      <span>{formatAxisHour(at(0.25).time, timeZone)}</span>
      <span>now {formatAxisHour(now.time, timeZone)}</span>
      <span>{formatAxisHour(at(0.75).time, timeZone)}</span>
      <span>{formatAxisHour(at(1).time, timeZone)}</span>
    </div>
  );
}
