import type { HTMLAttributes, ReactNode } from 'react';
import './Badge.css';

/** `muted` is the resting state — a fact about a row, not a warning about it.
    The three accents are for a badge that has something to say. */
export type BadgeTone = 'muted' | 'success' | 'danger' | 'primary';

export interface BadgeProps extends Omit<HTMLAttributes<HTMLSpanElement>, 'children'> {
  tone?: BadgeTone;
  children?: ReactNode;
}

/**
 * A pill stating one fact about the thing it sits next to: enabled, online,
 * included. 11 sites in admin, most of them a boolean rendered as Yes/No.
 *
 * It carries no icon and no dismiss control. A badge that can be pressed is a
 * button, and a badge that can be closed is a chip; both are different
 * components, and neither exists in this house yet.
 */
export function Badge({ tone = 'muted', className, children, ...rest }: BadgeProps) {
  const classes = ['aerie-badge', `aerie-badge--${tone}`];
  if (className) classes.push(className);

  return (
    <span className={classes.join(' ')} {...rest}>
      {children}
    </span>
  );
}
