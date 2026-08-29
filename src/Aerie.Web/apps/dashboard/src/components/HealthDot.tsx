import type { CSSProperties } from 'react';
import type { HealthLevel } from '../lib/healthSignal';

interface HealthDotProps {
  level: HealthLevel;
  /** How many faults are in the buffer, for the label a screen reader reads. */
  count: number;
  onOpen: () => void;
}

/**
 * The wall's one admission that something is wrong. Top-left rather than the
 * conventional top-right: the right half of the header is a 108px clock, which
 * is the loudest element on the page and the thing the wall is most often read
 * for - a warning indicator should not have to compete with it.
 *
 * Renders nothing at level 0, which is almost always.
 */
export function HealthDot({ level, count, onOpen }: HealthDotProps) {
  if (level === 0) return null;

  return (
    <button
      type="button"
      className="hf-health-dot"
      style={{ '--health-level': `${level}%` } as CSSProperties}
      onClick={onOpen}
      aria-label={`${count} ${count === 1 ? 'problem' : 'problems'} recorded. Open details.`}
    >
      <span className="hf-health-pip" aria-hidden="true" />
    </button>
  );
}
