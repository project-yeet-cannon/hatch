import { useId } from 'react';
import { batteryLabel, percentLabel, ringFraction, sessionLimit, toneClass } from '../lib/utilization';
import type { Utilization } from '../types';

/** The ring's geometry, in the SVG's own units. A 24-unit box with a stroke
    outside a disc, so the two readings never overlap. */
const SIZE = 24;
const CENTRE = SIZE / 2;
const RING_RADIUS = 9.5;
const DISC_RADIUS = 6.5;
const RING_CIRCUMFERENCE = 2 * Math.PI * RING_RADIUS;

/**
 * Two numbers in one glyph: how much of the session window has been spent, and
 * how much of the five hours is left before it comes back.
 *
 * The fill is the percentage - a disc clipped from the bottom, so it reads the
 * way a battery does. The ring around it is the window still to run, drawn as
 * an arc from the top. They are deliberately different shapes: they are
 * different quantities, and a second arc would read as more of the first.
 *
 * A window with no reset instant draws the ring as a dashed track rather than
 * as a full or an empty one. "We do not know when this comes back" and "it
 * comes back now" are not the same thing and must not look the same.
 *
 * Returns null when there is no battery - no token configured on this
 * installation. No element, no placeholder, no reserved space.
 */
export function UtilizationBattery({
  reading,
  now,
  onOpen,
}: {
  reading: Utilization | null;
  now: Date;
  onOpen: () => void;
}) {
  // A document-unique id, because the clip path is referenced by url() and two
  // batteries on one page sharing one id would clip through each other.
  const clipId = useId();

  const limit = sessionLimit(reading);
  const fraction = ringFraction(limit?.resetsAt, now);
  const label = batteryLabel(reading, now);

  // The share of the disc that is ink. No session row at all - the `unknown`
  // answer - draws an empty disc under an em dash rather than a made-up level.
  const filled = limit === null ? 0 : Math.min(100, Math.max(0, limit.percent)) / 100;

  if (reading === null) return null;

  return (
    <button
      type="button"
      className={`hatch-battery ${toneClass(limit?.tone)}`}
      onClick={onOpen}
      title={label}
      aria-label={label}
      aria-haspopup="dialog"
    >
      <svg className="hatch-battery-glyph" viewBox={`0 0 ${SIZE} ${SIZE}`} aria-hidden="true" focusable="false">
        <defs>
          <clipPath id={clipId}>
            <circle cx={CENTRE} cy={CENTRE} r={DISC_RADIUS} />
          </clipPath>
        </defs>

        {/* The track the arc is drawn on, so a nearly-spent window is still a
            circle rather than a lone stub floating in the strip. */}
        <circle className="hatch-battery-track" cx={CENTRE} cy={CENTRE} r={RING_RADIUS} />

        {fraction === null ? (
          <circle
            className="hatch-battery-ring hatch-battery-ring-unknown"
            cx={CENTRE}
            cy={CENTRE}
            r={RING_RADIUS}
          />
        ) : (
          <circle
            className="hatch-battery-ring"
            cx={CENTRE}
            cy={CENTRE}
            r={RING_RADIUS}
            /* Started at twelve o'clock and run clockwise, which is how every
               timer anybody has used runs. */
            transform={`rotate(-90 ${CENTRE} ${CENTRE})`}
            strokeDasharray={RING_CIRCUMFERENCE}
            strokeDashoffset={RING_CIRCUMFERENCE * (1 - fraction)}
          />
        )}

        <circle className="hatch-battery-disc" cx={CENTRE} cy={CENTRE} r={DISC_RADIUS} />
        {/* Clipped from the bottom by a rect rather than by an arc: a chord
            through a circle is a fiddly path, and a rect over the top of the
            disc is the same picture with none of the trigonometry. */}
        <rect
          className="hatch-battery-fill"
          x={CENTRE - DISC_RADIUS}
          y={CENTRE + DISC_RADIUS - 2 * DISC_RADIUS * filled}
          width={2 * DISC_RADIUS}
          height={2 * DISC_RADIUS * filled}
          clipPath={`url(#${clipId})`}
        />
      </svg>

      <span className="hatch-battery-percent">{percentLabel(limit)}</span>
    </button>
  );
}
