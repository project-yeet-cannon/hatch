import type { ReactNode, TableHTMLAttributes } from 'react';
import './Table.css';

export interface TableProps extends TableHTMLAttributes<HTMLTableElement> {
  /** Wraps the table in a sideways-scrolling box, for a table with more
      columns than the page has room for.

      Off by default, which is admin's behaviour today and not an oversight: an
      `overflow-x` box is a clipping context, and admin puts absolutely-
      positioned things inside table cells — the channel combobox's dropdown is
      one — that a scroll box would cut off at the cell's edge. Turning it on
      is a per-table decision by someone who has looked at what is in the
      cells. */
  scroll?: boolean;
  children?: ReactNode;
}

/**
 * The rows. 9 sites in admin.
 *
 * Deliberately not column-driven. A `columns={[…]}` API would be a smaller
 * call site for the simple tables and a wall for the rest of admin's, which
 * put an editing form in a `colSpan={6}` row, nest a second table in a cell,
 * and render a header with no label above the actions column. Passing <thead>
 * and <tbody> straight through keeps every one of those a plain piece of JSX
 * and makes each page's migration a one-line change.
 *
 * What it does own is the appearance, and an opt-in scroll box for the tables
 * that are wider than the page.
 */
export function Table({ scroll = false, className, children, ...rest }: TableProps) {
  const classes = ['hatch-table'];
  if (className) classes.push(className);
  const table = (
    <table className={classes.join(' ')} {...rest}>
      {children}
    </table>
  );

  return scroll ? <div className="hatch-table-scroll">{table}</div> : table;
}
