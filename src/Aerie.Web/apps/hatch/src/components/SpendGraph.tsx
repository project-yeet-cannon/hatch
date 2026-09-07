import { dayLabel, type SpendMeasure } from '../lib/leaderboard';
import { bars, bucketPhrase, graphLabel, measureLabel, peakLabel, spanPhrase, stillGathering } from '../lib/spend';
import type { WorkLogHistory } from '../types';

/** One bucket's slot, in the SVG's own units. The bar is inset inside it, so
    the gap between bars is arithmetic rather than a stroke. */
const SLOT = 4;
const INSET = 0.4;
const HEIGHT = 100;

/** The two the control offers, in the order they are drawn. */
const MEASURES: { key: SpendMeasure; label: string }[] = [
  { key: 'tokens', label: 'Tokens' },
  { key: 'cost', label: 'Notional USD' },
];

/**
 * What the nights cost, along a time axis: one bar per bucket.
 *
 * Hand-cut SVG in the house's own tokens - the app carries no charting
 * dependency and this does not add one; `UtilizationBattery` is the precedent.
 * Everything that could be wrong is in `lib/spend.ts` and tested there, and what
 * is here places what that returns.
 *
 * **An empty bucket is drawn as a zero, never as a gap.** An hour in which
 * nothing ran is an hour that cost nothing, which is a measurement; a break
 * would assert an absence where there is one. It keeps its slot, and its slot
 * stays hoverable, which is what that rule means when the mouse is over it.
 *
 * **The two measures never share an axis.** They differ by six orders of
 * magnitude, and a second y-axis would draw two lies crossing - so the control
 * rescales the whole graph rather than adding a series to it.
 */
export function SpendGraph({
  history,
  measure,
  onMeasure,
}: {
  history: WorkLogHistory;
  measure: SpendMeasure;
  onMeasure: (measure: SpendMeasure) => void;
}) {
  // Nothing has ever been logged under this filter. A sentence, not an axis
  // over an empty log - the page says it too, and this keeps the component
  // honest on its own.
  if (stillGathering(history)) {
    return (
      <p className="text-muted">Nothing has been logged yet, so there is no spend to draw.</p>
    );
  }

  const drawn = bars(history, measure);

  return (
    <div className="hatch-spend">
      <div className="hatch-spend-head">
        <div className="hatch-spend-measures" role="group" aria-label="Measure">
          {MEASURES.map((option) => (
            <button
              key={option.key}
              type="button"
              className={`hatch-spend-measure${measure === option.key ? ' active' : ''}`}
              aria-pressed={measure === option.key}
              onClick={() => onMeasure(option.key)}
            >
              {option.label}
            </button>
          ))}
        </div>

        {/* The top of the scale, so a shape has a size. */}
        <span className="hatch-spend-peak text-muted">{peakLabel(history, measure)}</span>
      </div>

      {/* preserveAspectRatio="none" is safe here because every mark is a fill:
          nothing is stroked, so nothing is distorted by the non-uniform
          scaling. */}
      <svg
        className="hatch-spend-plot"
        viewBox={`0 0 ${Math.max(1, drawn.length) * SLOT} ${HEIGHT}`}
        preserveAspectRatio="none"
        role="img"
        aria-label={graphLabel(history, measure)}
      >
        {drawn.map((bar, i) => (
          <g key={bar.start}>
            {/* A full-height transparent slot carrying the title, so an empty
                bucket is still hoverable. */}
            <rect className="hatch-spend-slot" x={i * SLOT} y={0} width={SLOT} height={HEIGHT}>
              <title>{bar.label}</title>
            </rect>
            <rect
              className="hatch-spend-bar"
              x={i * SLOT + INSET}
              y={HEIGHT - HEIGHT * bar.fraction}
              width={SLOT - 2 * INSET}
              height={HEIGHT * bar.fraction}
            />
          </g>
        ))}
      </svg>

      <div className="hatch-spend-axis text-muted">
        <span>{dayLabel(history.from)}</span>
        {/* Named from the answer: the size is the server's choice and the
            window is the server's snapping, and the page says both. */}
        <span>
          Spend {bucketPhrase(history)} in {measureLabel(measure)}, {spanPhrase(history)}
        </span>
        <span>{dayLabel(history.to)}</span>
      </div>
    </div>
  );
}
