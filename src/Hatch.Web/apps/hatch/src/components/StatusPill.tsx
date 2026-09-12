import { statusVars } from '../lib/color';
import type { Status } from '../types';

export type StatusPillSize = 'sm' | 'lg';

/**
 * A status, wearing its colour.
 *
 * Filled rather than outlined, and the ink is computed from the fill (see
 * lib/color.ts) rather than left to a stylesheet - a column the operator paints
 * pale yellow needs black text on it, and CSS cannot ask that question yet.
 *
 * Small on a card or in a list; large on the issue page, where the status is
 * the first thing the page is meant to say.
 */
export function StatusPill({ status, size = 'sm' }: { status: Status; size?: StatusPillSize }) {
  return (
    <span className={`hatch-status-pill hatch-status-pill-${size}`} style={statusVars(status.color)}>
      {status.name}
    </span>
  );
}

/** The colour on its own, for a column heading that already says the name beside it. */
export function StatusDot({ status }: { status: Status }) {
  return <span className="hatch-status-dot" style={statusVars(status.color)} aria-hidden="true" />;
}
