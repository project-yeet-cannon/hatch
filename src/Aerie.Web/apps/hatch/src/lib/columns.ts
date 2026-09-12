/* Which columns are the board, and what counts as work that has stopped.

   Two one-line questions, in a module of their own for the reason lib/place.ts
   and lib/closeSubtree.ts are: they are asked on four screens, and four copies
   of `!s.isDeferred` is four places to forget it. The server holds the same two
   answers in Columns.cs and reads them the same way - a board measured off the
   columns it actually draws, and a ticket that has stopped counting as stopped
   however it got there.

   Nothing here is a transition rule. Every column is still a legal destination
   for every issue (docs/hatch.md, "Non-goals"); this only says which of them
   the board is made of. */

import type { Status } from '../types';

/**
 * The columns the board draws, in the order it draws them.
 *
 * A deferred column is left out because a column on the board is a drop target,
 * and a siding is exactly the place work must not drift into by being dragged
 * one column too far. The way into one is the issue page's status bar, which is
 * a press somebody has to mean.
 *
 * The API sends every column on every read regardless - the issue page needs
 * the deferred ones in order to offer them - so this is the only thing standing
 * between the two, and every board-shaped list goes through it.
 */
export const boardColumns = (statuses: Status[]): Status[] => statuses.filter((s) => !s.isDeferred);

/**
 * Whether work sitting in this column has stopped: shipped, or shelved.
 *
 * The two are not the same fact and are stored apart - a rollup counts one and
 * discards the other - but wherever the question is "is anybody still expecting
 * this to move", they answer it together. A due date on a parked ticket is not
 * a date anybody is late for, and a parked child is not open work to be swept
 * into its parent's column.
 */
export const isSettled = (status: Status | undefined): boolean =>
  status ? status.isTerminal || status.isDeferred : false;
