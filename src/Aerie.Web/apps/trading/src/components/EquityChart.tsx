import { useId } from 'react';
import { Text } from '@aerie/ui';
import { day, money } from '../lib/format';
import type { Curve } from '../types';

/**
 * A run's equity curve, as inline SVG.
 *
 * **No charting library, and that is the plan's instruction rather than a
 * preference:** *"Grafana carries deep-dive time series. Do not build a
 * charting stack to compete with a tool already deployed and already good at
 * this."* What a run detail needs is the shape of one series and where it
 * started - which is a path, two axis labels and a baseline. A library would
 * add two hundred kilobytes to every page load to draw the same polyline.
 *
 * **The starting-cash line is the only reference drawn**, because it is the
 * only one that is a fact rather than a comparison: above it the run made
 * money, below it lost. A baseline series would be a second curve this
 * endpoint does not carry, and inventing one from the baseline *metric* would
 * be drawing a straight line through two points and calling it a strategy.
 *
 * Colours are tokens (`--series-1`, `--line`, `--muted`), so the chart is
 * correct in both themes without knowing which one it is in. Nothing here
 * reads a theme.
 */
export function EquityChart({ curve, startingCash }: { curve: Curve; startingCash: string }) {
  const gradientId = useId();

  if (!curve.recorded) {
    /* A run that finished before curves were stored has none. Saying so beats
       an empty chart, which reads as "this run did nothing". */
    return <Text tone="muted">No equity curve was recorded for this run.</Text>;
  }
  if (curve.points.length < 2) {
    return <Text tone="muted">This run produced too few points to plot.</Text>;
  }

  const width = 900;
  const height = 260;
  const padding = { top: 12, right: 12, bottom: 24, left: 64 };

  const values = curve.points.map(([, equity]) => Number(equity));
  const times = curve.points.map(([at]) => new Date(at).getTime());
  const cash = Number(startingCash);

  /* The starting cash is inside the range whether or not the run ever traded
     above or below it, so the reference line is always on the chart. */
  const low = Math.min(...values, cash);
  const high = Math.max(...values, cash);
  const span = high - low || 1;
  const first = times[0];
  const last = times[times.length - 1];
  const elapsed = last - first || 1;

  const x = (at: number) => padding.left + ((at - first) / elapsed) * (width - padding.left - padding.right);
  const y = (value: number) =>
    padding.top + (1 - (value - low) / span) * (height - padding.top - padding.bottom);

  const line = curve.points
    .map(([at, equity], index) => `${index === 0 ? 'M' : 'L'}${x(new Date(at).getTime()).toFixed(1)} ${y(Number(equity)).toFixed(1)}`)
    .join(' ');
  const area = `${line} L${x(last).toFixed(1)} ${y(low).toFixed(1)} L${x(first).toFixed(1)} ${y(low).toFixed(1)} Z`;

  return (
    <figure className="tr-chart">
      <svg
        viewBox={`0 0 ${width} ${height}`}
        role="img"
        aria-label={`Equity from ${money(values[0])} to ${money(values[values.length - 1])}`}
        preserveAspectRatio="none"
      >
        <defs>
          <linearGradient id={gradientId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--series-1)" stopOpacity="0.22" />
            <stop offset="100%" stopColor="var(--series-1)" stopOpacity="0" />
          </linearGradient>
        </defs>

        {/* The reference: what the run started with. Dashed, so it reads as a
            reference rather than as a second series. */}
        <line
          x1={padding.left}
          x2={width - padding.right}
          y1={y(cash)}
          y2={y(cash)}
          stroke="var(--line-strong)"
          strokeDasharray="4 4"
          strokeWidth="1"
          vectorEffect="non-scaling-stroke"
        />

        <path d={area} fill={`url(#${gradientId})`} />
        <path
          d={line}
          fill="none"
          stroke="var(--series-1)"
          strokeWidth="2"
          strokeLinejoin="round"
          vectorEffect="non-scaling-stroke"
        />

        <text x="4" y={y(high) + 4} className="tr-chart-label">{money(high)}</text>
        <text x="4" y={y(low) + 4} className="tr-chart-label">{money(low)}</text>
        <text x={padding.left} y={height - 6} className="tr-chart-label">{day(curve.points[0][0])}</text>
        <text x={width - padding.right} y={height - 6} textAnchor="end" className="tr-chart-label">
          {day(curve.points[curve.points.length - 1][0])}
        </text>
      </svg>
      <figcaption>
        <Text tone="muted">
          {curve.sampled
            ? `${curve.points.length.toLocaleString()} of ${curve.points_total.toLocaleString()} points, evenly sampled — a spike between two kept points is not drawn.`
            : `${curve.points_total.toLocaleString()} points, every bar the run saw. The dashed line is the starting cash.`}
        </Text>
      </figcaption>
    </figure>
  );
}
