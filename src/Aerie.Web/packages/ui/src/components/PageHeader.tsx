import type { ReactNode } from 'react';
import './PageHeader.css';

/** 1 for the page's own heading — the default, and there is one per page. 2
    for a header that opens a section *within* a page, which is what admin's
    Revisions page uses this shape for. */
export type PageHeaderLevel = 1 | 2;

export interface PageHeaderProps {
  title: ReactNode;
  /** One line saying what the page is for. Renders nothing when absent — no
      reserved slot, no placeholder. */
  description?: ReactNode;
  /** The page's actions, right-aligned on the title's line. */
  actions?: ReactNode;
  level?: PageHeaderLevel;
  className?: string;
}

/**
 * The top of a page, and **the component that owns the page's <h1>**.
 *
 * That is the whole reason it exists at this size. Phase 3 moved the app name
 * out of the heading outline and into the banner landmark, so admin's pages
 * were left titling themselves with <h2> and no <h1> above them anywhere on
 * the page. Every admin page rendering this component is what closes that, and
 * doing it here makes the lift one edit rather than twelve.
 *
 * The lift is the one visible type change Phase 4 makes: --t-heading (22px) to
 * --t-title (28px). It is listed in the plan as such.
 *
 * The header does not draw a rule under itself and does not repeat the app
 * name. The bar above says where you are; this says what is on the page.
 */
export function PageHeader({ title, description, actions, level = 1, className }: PageHeaderProps) {
  const Heading = level === 1 ? 'h1' : 'h2';
  const classes = ['aerie-page-header'];
  if (description) classes.push('aerie-page-header--described');
  if (className) classes.push(className);

  return (
    <header className={classes.join(' ')}>
      <div className="aerie-page-header__titles">
        <Heading>{title}</Heading>
        {description ? <p className="aerie-page-header__description">{description}</p> : null}
      </div>
      {actions ? <div className="aerie-page-header__actions">{actions}</div> : null}
    </header>
  );
}
