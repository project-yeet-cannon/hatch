import type { ReactNode } from 'react';
import './EmptyState.css';

export interface EmptyStateProps {
  /** What is not here. A sentence, in the words of the thing that is missing:
      "No zones yet", not "No data". */
  message: ReactNode;
  /** The one thing to do about it, when there is one. */
  action?: ReactNode;
  className?: string;
}

/**
 * The component the renders-nothing rule is stated against.
 *
 * The house rule is that a component with nothing to say renders nothing — no
 * empty tables with a "no rows" row, no placeholder tiles. The rule needs an
 * exception with a name, because sometimes the *absence* is the news: a Zones
 * page with no zones has to say so, or it reads as a page that failed to load.
 * This is that exception, and the fact that it is a component is the point —
 * an empty state you have to import is an empty state somebody decided on.
 *
 * It renders admin's existing muted sentence and nothing more. There is no
 * illustration, no headline, no bordered box: what an empty state should look
 * like is a design decision, and this phase's job was to make it one decision
 * instead of forty scattered `<p className="text-muted">`s.
 *
 * Not for errors and not for loading. "Nothing here" and "something broke" are
 * different facts, and a page that says the first when it means the second
 * sends the operator looking in the wrong place.
 */
export function EmptyState({ message, action, className }: EmptyStateProps) {
  const classes = ['hatch-empty'];
  if (className) classes.push(className);

  return (
    <div className={classes.join(' ')}>
      <p className="hatch-empty__message">{message}</p>
      {action}
    </div>
  );
}
