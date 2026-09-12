import type { ReactNode } from 'react';
import './Grid.css';

/** How many cells fit across at full width — 2 for form fields that need room
    to read, 3 for short ones. Below that width the grid reflows on its own. */
export type GridCols = 2 | 3;

export interface GridProps {
  cols: GridCols;
  className?: string;
  children?: ReactNode;
}

/**
 * The arrangement admin's forms and card decks are laid out in. 12 sites.
 *
 * `cols` is required rather than defaulting to something: admin has no bare
 * `.grid` — every use picks a width — and a default would be a fourth answer
 * nobody asked for.
 *
 * There is no `gap` prop. One gap, stated once, is what makes two pages built
 * by two people a week apart look like the same product.
 */
export function Grid({ cols, className, children }: GridProps) {
  const classes = ['hatch-grid', `hatch-grid--${cols}`];
  if (className) classes.push(className);

  return <div className={classes.join(' ')}>{children}</div>;
}
