/* What closes when an issue closes, and what is shelved when an issue is.

   Moving a parent into a terminal column has never moved anything under it, so
   an epic reads shipped while eleven leaves still count as waiting. The board
   now offers to close them too - and this module is the whole of that offer:
   whether there is anything to ask about, which cards, and in what order.

   Deferring is the same offer with the same shape, because it is the same
   mistake in the other direction: an epic put on the shelf with eleven live
   tasks under it has not been shelved, it has been hidden, and the work under
   it goes on being dispatched from a board nobody can see the parent on. A
   stack of work is deferred as a unit or not at all.

   It is a module rather than a helper inside a page for the reason lib/place.ts
   gives: getting it slightly wrong has no visible symptom. The wrong number of
   issues quietly closes, and the board goes on looking plausible. There is also
   no DOM in the web test run, so every decision the feature makes lives here
   and gets tested here; what is left at each of the two call sites is wiring
   with no branch in it.

   Nothing here is a transition rule. Hatch has none on purpose (docs/hatch.md,
   "Non-goals") and this adds none: the answer is a person's, asked after the
   move has already committed. */

import { isSettled } from './columns';
import type { Board, IssueCard, Status } from '../types';

export interface CloseOffer {
  /** The issue that was closed. */
  key: string;
  /** The column it landed in - terminal or deferred, and the one its
      descendants would join. `column.isDeferred` is what the dialog words
      itself from: the two offers are one mechanism and two sentences. */
  column: Status;
  /** The descendants that would move, in board order. */
  cards: IssueCard[];
}

/**
 * Every descendant at any depth that has not already stopped.
 *
 * A descendant already sitting in a terminal or a deferred column is left out
 * rather than restamped into the parent's column: it has already stopped, and
 * stopping it again in a different flavour rewrites what happened to it. That
 * cuts both ways, and deliberately - a task that shipped is not un-shipped by
 * its epic being shelved, and a task somebody parked on purpose is not quietly
 * marked done by its epic closing.
 *
 * The result comes back in **board order** - the order the API handed the cards
 * over, `(statusId, rank, id)` - rather than in the order the walk visited
 * them, because that is the order the dialog lists and the order the bulk edit
 * names, and one definition of "the order of the board" is enough to keep in
 * step.
 */
export function closeSubtree(cards: IssueCard[], key: string, statuses: Status[]): IssueCard[] {
  const children = new Map<string, IssueCard[]>();
  for (const card of cards) {
    if (card.parentKey === null) continue;
    const siblings = children.get(card.parentKey);
    if (siblings) siblings.push(card);
    else children.set(card.parentKey, [card]);
  }

  // Seeded with the issue itself, so it can never come back as its own
  // descendant. The set is not decoration otherwise either: parenting refuses
  // to close a loop, but one that got in some other way - a restored backup, a
  // hand-written UPDATE - must make this return a wrong answer rather than
  // spin the browser. Same reason Rollup.Descend carries one.
  const seen = new Set<string>([key]);
  const found = new Set<string>();
  let generation = [key];

  while (generation.length > 0) {
    const next: string[] = [];
    for (const parent of generation)
      for (const child of children.get(parent) ?? [])
        if (!seen.has(child.key)) {
          seen.add(child.key);
          found.add(child.key);
          next.push(child.key);
        }
    generation = next;
  }

  // Applied last, so a *stopped* issue with open work under it does not hide
  // that work: the walk reaches through it, and only the cards themselves are
  // filtered. Ready dates are not consulted at all - the fold is a view, and a
  // card folded off the board is still work under this issue.
  const stopped = new Set(statuses.filter(isSettled).map((s) => s.id));
  return cards.filter((card) => found.has(card.key) && !stopped.has(card.statusId));
}

/**
 * The question to ask after a move, or null when there is nothing to ask.
 *
 * The whole of "is there anything to offer", so that neither screen has to
 * spell it out: a drop into a column that neither ships nor shelves, a reorder
 * inside the column a card was already in, an issue with no children, and an
 * issue whose every descendant has already stopped all come back null and
 * behave exactly as they do today.
 *
 * @param from The column the issue was in before the move, or null if unknown.
 * @param to The column it landed in.
 */
export function closeOffer(board: Board, key: string, from: number | null, to: number): CloseOffer | null {
  // A card dragged up or down inside the column it is already in is a reorder,
  // and a reorder closes nothing.
  if (from === to) return null;

  const column = board.statuses.find((s) => s.id === to);
  if (!column || !isSettled(column)) return null;

  const cards = closeSubtree(board.issues, key, board.statuses);
  return cards.length > 0 ? { key, column, cards } : null;
}
