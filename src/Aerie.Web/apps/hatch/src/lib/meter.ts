/* Turning a rollup into a bar: which segments, how wide, and what it says out
   loud.

   Apart from the component on purpose, the way filter.ts and place.ts are, so
   the arithmetic can be tested without a DOM - and so the two hard parts of it
   are stated once rather than re-derived at each of the three screens that draw
   a meter.

   The two hard parts:

   - A slice can name a status this browser has never heard of. The server
     computes a rollup over whatever columns exist when it is asked; a column
     deleted between that request and this render leaves a slice pointing at
     nothing. Dropping it is the only sane answer - a page that throws because
     somebody tidied the Statuses page is a page that blanks for everybody.

   - Whole percentages do not add up. Three equal thirds rounded each to 33
     make 99, and a bar labelled "99%" of itself is a bug report waiting to be
     filed. So the percentages are apportioned by largest remainder rather than
     rounded independently, and they sum to exactly 100. */

import type { Rollup, Status } from '../types';

/** One stripe of the bar: a column, its colour, and its share. */
export interface MeterSegment {
  statusId: number;
  name: string;
  /** As the operator stored it. Paintable or not - `statusVars` in lib/color.ts
      decides the fallback, and it decides it in one place. */
  color: string;
  count: number;
  /** Whole percent of the bar. Sums to exactly 100 across every segment. */
  percent: number;
}

/**
 * Whole percentages that sum to 100, by largest remainder.
 *
 * Everybody gets their floor; the leftovers go to whoever was rounded down
 * hardest, ties broken by position so the answer does not depend on sort
 * stability. `counts` is never empty here and `total` is its sum.
 */
function apportion(counts: number[], total: number): number[] {
  const exact = counts.map((count) => (count * 100) / total);
  const percents = exact.map(Math.floor);

  let left = 100 - percents.reduce((sum, percent) => sum + percent, 0);
  const hungriest = exact
    .map((value, at) => ({ at, remainder: value - Math.floor(value) }))
    .sort((a, b) => b.remainder - a.remainder || a.at - b.at);

  for (const { at } of hungriest) {
    if (left <= 0) break;
    percents[at] += 1;
    left -= 1;
  }

  return percents;
}

/**
 * The bar, in board order: one segment per status holding at least one leaf.
 *
 * Board order comes from `statuses` rather than from the slices, so the
 * segments read left to right in the same order as the columns whatever order
 * the rollup arrived in. A status no leaf is in is absent, not a zero-width
 * segment - `slices` already omits it, and a stripe of nothing is not a stripe.
 *
 * Percentages are shares of what is drawn, not of `rollup.leaves`: if a slice
 * was dropped for naming a status that no longer exists, the bar still adds up
 * to itself.
 */
export function meterSegments(rollup: Rollup, statuses: Status[]): MeterSegment[] {
  const counts = new Map(rollup.slices.map((slice) => [slice.statusId, slice.count]));

  const found = statuses
    .map((status) => ({ status, count: counts.get(status.id) ?? 0 }))
    .filter((entry) => entry.count > 0);

  const total = found.reduce((sum, entry) => sum + entry.count, 0);
  if (total === 0) return [];

  const percents = apportion(
    found.map((entry) => entry.count),
    total,
  );

  return found.map(({ status, count }, at) => ({
    statusId: status.id,
    name: status.name,
    color: status.color,
    count,
    percent: percents[at],
  }));
}

/** How far along the subtree is, as a whole percent of every leaf in it. */
export const donePercent = (rollup: Rollup): number =>
  rollup.leaves === 0 ? 0 : Math.round((rollup.done * 100) / rollup.leaves);

/** Nothing filed under it yet, which is not the same as nothing done. */
const NOTHING_FILED = 'Nothing filed yet';

/**
 * The sentence a screen reader is given for the bar:
 * `"12 of 43 done — 20 to do, 11 in progress, 12 done"`.
 *
 * A stacked bar is a picture of a number, and a picture of a number is
 * unreadable without this - so it says the headline first, which is what
 * somebody skimming a list of epics wants, then the distribution the bar is
 * drawing, which is why the bar is there at all.
 *
 * Column names are lower-cased so the clause reads as prose rather than as a
 * row of proper nouns - "20 to do", not "20 To Do".
 */
export function meterLabel(rollup: Rollup, statuses: Status[]): string {
  const segments = meterSegments(rollup, statuses);
  if (segments.length === 0) return NOTHING_FILED;

  const spread = segments.map((segment) => `${segment.count} ${segment.name.toLowerCase()}`).join(', ');
  return `${rollup.done} of ${rollup.leaves} done — ${spread}`;
}
