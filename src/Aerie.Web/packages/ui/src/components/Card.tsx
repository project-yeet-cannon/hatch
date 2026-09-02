import type { ElementType, ReactNode } from 'react';
import './Card.css';

export interface CardProps {
  /** Drops the card's padding for content that should run to its own edges —
      a full-bleed table, a list of rows. */
  flush?: boolean;
  /** `section` or `article` where the card is a real division of the page and
      not just a box. A plain div by default, which is what admin's 29 cards
      are. */
  as?: ElementType;
  className?: string;
  children?: ReactNode;
}

/**
 * The surface everything else sits on. 29 sites in admin, and the shape the
 * modal panel is built from.
 *
 * It states no layout for its children on purpose. A card that laid out its
 * contents would be two components — a surface and a stack — welded together,
 * and every site that wanted the surface with a different arrangement inside
 * would be fighting it. Use <Grid> or the page's own rules inside.
 */
export function Card({ flush = false, as, className, children }: CardProps) {
  const Component = as ?? 'div';
  const classes = ['aerie-card'];
  if (flush) classes.push('aerie-card--flush');
  if (className) classes.push(className);

  return <Component className={classes.join(' ')}>{children}</Component>;
}
