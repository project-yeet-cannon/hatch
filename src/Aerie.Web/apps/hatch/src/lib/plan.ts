/* The order the Plan page draws its epics in.

   Apart from the component the way meter.ts and filter.ts are, and for the
   same reason: this is the half of the page that has an argument behind it,
   and an argument written inline in a JSX map is one nobody can test.

   The argument, in one line: the page exists to answer "which epic is worth
   furthering today", so the epic nearest the finish line goes at the top and
   the finished ones get out of the way. Sorting by completion alone would do
   the opposite - it would park every shipped epic permanently above the work,
   because 100% sorts above everything. */

import type { PlanEntry, Rollup } from '../types';

/**
 * How far along a subtree is, between 0 and 1.
 *
 * Nothing filed under it is 0 rather than 1, though `0 of 0` is arguably
 * complete: an epic with no stories is at the start of its life, and the whole
 * page is about which epic to push, not which one to congratulate.
 */
export const fractionDone = (rollup: Rollup): number => (rollup.leaves === 0 ? 0 : rollup.done / rollup.leaves);

/**
 * Every leaf below it sits in a terminal column.
 *
 * An epic with nothing under it is deliberately not finished, though it
 * satisfies that sentence vacuously - see `fractionDone`. Folding an empty
 * epic into the collapsed section at the bottom is how a freshly filed epic
 * gets forgotten on the day it most needs breaking down.
 */
export const isFinished = (entry: PlanEntry): boolean =>
  entry.rollup.leaves > 0 && entry.rollup.done === entry.rollup.leaves;

/**
 * Issue keys in the order a person reads them: project, then number.
 *
 * A plain string compare would put AER-10 above AER-9, which is the sort of
 * wrongness that is invisible until there are ten epics and then never stops
 * being visible. Anything that is not `KEY-123` falls back to a string
 * compare, so a hand-edited row still lands somewhere fixed rather than
 * throwing.
 */
const SHAPE = /^(.+)-(\d+)$/;

function byKey(a: string, b: string): number {
  const left = SHAPE.exec(a);
  const right = SHAPE.exec(b);

  if (left && right && left[1] === right[1]) return Number(left[2]) - Number(right[2]);
  return a < b ? -1 : a > b ? 1 : 0;
}

/**
 * The total order over epics, most worth looking at first.
 *
 * Finished last, then nearest the line first, then the larger epic, then the
 * key. The last two tie-breaks are not cosmetic: a rollup is a handful of small
 * integers, so ties are the common case rather than the rare one, and without a
 * final tie-break on something unique two epics would swap places on every
 * refetch. A list that reshuffles under the cursor while nothing has changed is
 * a list nobody trusts.
 *
 * Size before key so that when two epics are equally far along, the one with
 * more work under it is the one being asked about - it is the bigger commitment
 * and the bigger thing to finish.
 */
export function comparePlanEntries(a: PlanEntry, b: PlanEntry): number {
  const finished = Number(isFinished(a)) - Number(isFinished(b));
  if (finished !== 0) return finished;

  return (
    fractionDone(b.rollup) - fractionDone(a.rollup) ||
    b.rollup.leaves - a.rollup.leaves ||
    byKey(a.issue.key, b.issue.key)
  );
}

/** The page's two lists: what is still moving, and what is done with. */
export interface OrderedPlan {
  /** Not finished, nearest the line first. The page proper. */
  live: PlanEntry[];
  /** Every leaf in a terminal column. Collapsed at the bottom. */
  finished: PlanEntry[];
}

/**
 * The same order, all the way down: an epic's nested epics read the way the
 * page reads, so the eye does not have to learn two orders on one screen.
 *
 * A copy rather than an in-place sort. The entries come from `useLoaded`'s
 * state and are handed to React as props; sorting them where they lie would
 * mutate the object a render is already holding.
 */
function sorted(entries: PlanEntry[]): PlanEntry[] {
  return [...entries]
    .sort(comparePlanEntries)
    .map((entry) => (entry.children.length === 0 ? entry : { ...entry, children: sorted(entry.children) }));
}

/**
 * The roots, ordered and split.
 *
 * Only the roots are split: a finished epic *nested* inside one that is still
 * moving stays where it is, sorted to the end of its parent's children. Pulling
 * it out to the bottom of the page would break the one thing the nesting is
 * for, which is showing what an epic is made of.
 */
export function orderPlan(epics: PlanEntry[]): OrderedPlan {
  const all = sorted(epics);

  return {
    live: all.filter((entry) => !isFinished(entry)),
    finished: all.filter(isFinished),
  };
}
