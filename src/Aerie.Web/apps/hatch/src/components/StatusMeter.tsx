import { statusVars } from '../lib/color';
import { donePercent, meterLabel, meterSegments } from '../lib/meter';
import type { Rollup, Status } from '../types';

export type StatusMeterSize = 'sm' | 'lg';

/**
 * A count is only worth printing on a segment that has room for it. The
 * component cannot measure itself, so it decides from the share: at a tenth of
 * a full-width bar there is room for two digits, and below that the number
 * would be a clipped glyph pretending to be information.
 */
const WIDE_ENOUGH = 10;

/**
 * Where the bulk of a subtree is sitting: one stacked bar, a segment per
 * status, in the colour the operator painted that column.
 *
 * `sm` on a list row, where the bar is one cell of many; `lg` on a page, where
 * it is the thing the page is about and its segments carry their counts.
 *
 * Nothing here invents paint. A colour reaches CSS through `statusVars`, the
 * same way StatusPill's does, so the ink written on a segment is the one
 * lib/color.ts computed for that fill rather than a guess a stylesheet made.
 */
export function StatusMeter({
  rollup,
  statuses,
  size = 'sm',
  counts = false,
}: {
  rollup: Rollup;
  statuses: Status[];
  size?: StatusMeterSize;
  /** Print `12 / 43`, the percentage, and anything waiting, beside the bar. */
  counts?: boolean;
}) {
  const segments = meterSegments(rollup, statuses);

  /* Nothing filed under it yet. Drawn as nothing rather than as an empty grey
     trough, which reads as "0% done" when the truth is that there is nothing
     to be done yet - and an epic with no stories is at the start of its life,
     not stalled at the bottom of a bar. */
  if (segments.length === 0) return null;

  return (
    <div className={`hatch-meter hatch-meter-${size}`}>
      {/* One picture of one number, so it is announced as one thing rather than
          as a row of unlabelled boxes. */}
      <div className="hatch-meter-bar" role="img" aria-label={meterLabel(rollup, statuses)}>
        {segments.map((segment) => (
          <span
            key={segment.statusId}
            className="hatch-meter-segment"
            /* flex-grow by count rather than a width in percent: the floor in
               App.css then guarantees one task out of two hundred is a visible
               sliver instead of a sub-pixel nothing, and the rest of the bar
               divides what is left in proportion. */
            style={{ ...statusVars(segment.color), flexGrow: segment.count }}
            title={`${segment.name}: ${segment.count}`}
          >
            {size === 'lg' && segment.percent >= WIDE_ENOUGH && (
              <span className="hatch-meter-segment-count">{segment.count}</span>
            )}
          </span>
        ))}
      </div>
      {counts && (
        <div className="hatch-meter-counts">
          <span className="hatch-meter-done">
            {rollup.done} / {rollup.leaves}
          </span>
          <span className="hatch-meter-percent">{donePercent(rollup)}%</span>
          {/* The one thing that stops a subtree moving. Nothing below this
              issue can advance while somebody owes it a decision, so it is
              said in the same warn colours a question wears on the board. */}
          {rollup.waiting > 0 && (
            <span
              className="hatch-meter-waiting"
              title={`${rollup.waiting} unanswered question${rollup.waiting === 1 ? '' : 's'} below this`}
            >
              {rollup.waiting} waiting
            </span>
          )}
        </div>
      )}
    </div>
  );
}
