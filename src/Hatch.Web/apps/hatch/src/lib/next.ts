/* Who is holding up a subtree that has nothing an agent may move.

   Apart from the component the way meter.ts and plan.ts are, and for the same
   reason: this is a rule with an argument behind it, and a rule written inline
   in a JSX conditional is one nobody can test.

   The argument: an epic with nothing dispatchable under it and questions
   waiting somewhere beneath is stopped on a person, and the useful next click
   is the ticket holding the question. That click is only offered where it is
   certainly right - see below - because a link to the wrong ticket costs more
   than no link at all. */

import type { IssueRollup } from '../types';

/**
 * The direct child whose subtree accounts for every unanswered question under
 * this issue, or null when no single child does.
 *
 * Null covers three cases that are all the same answer - say the count, offer
 * no link:
 *
 * - Nothing is waiting.
 * - Two children are waiting, so there is no one ticket to send somebody to.
 * - One child is waiting and the total is larger, which means the issue on
 *   screen is holding a question of its own. Its own questions are already in
 *   the card at the top of this page, and linking past them to a child would
 *   send a reader away from the thing they can answer without scrolling.
 */
export function waitingChild(rollup: IssueRollup): string | null {
  const holders = rollup.children.filter((child) => child.rollup.waiting > 0);
  if (holders.length !== 1) return null;

  return holders[0].rollup.waiting === rollup.rollup.waiting ? holders[0].issue.key : null;
}
