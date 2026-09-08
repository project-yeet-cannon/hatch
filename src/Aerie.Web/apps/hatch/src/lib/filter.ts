/* The board's filter: which types to draw, and a search box.

   Deliberately dumb, and dumb in a stated way - the board already holds every
   issue in the house in the browser (BoardDto is one request, unpaged), so this
   is a pass over an array rather than a round trip, and it can afford to be
   obvious. Anything cleverer - fuzzy matching, ranking, stemming - would make
   "why is that card not showing" a question with no answer.

   Filtering is the browser's, not the server's, on purpose: the filter is a
   view of the board, and a board that refetched on every keystroke would drop
   the drag it was in the middle of. */

import { UNASSIGNED, assigneeToken, compareAssignees } from './assignee';
import type { Assignee, IssueCard, IssueType } from '../types';

export interface CardFilter {
  /** The types to draw. Empty means every type - an empty filter shows the board, not nothing. */
  types: IssueType[];
  /** What was typed in the search box, raw. */
  query: string;
  /** Only the cards holding a question nobody has answered.
      Its own switch rather than a search term, because "what is waiting on me"
      is the question an operator opens the board with when the loop has stopped
      moving, and it should not depend on remembering a word to type. */
  waiting: boolean;
  /** Whose cards to draw: `''` is everybody's, `UNASSIGNED` is the ones nobody
      owns, and anything else is one identity's token (lib/assignee.ts).

      A string rather than an `Assignee | null | undefined`, because a <select>
      holds a string and the three states have to be told apart - and "nobody"
      is a choice somebody makes, not the absence of one. */
  assignee: string;
}

export const NO_FILTER: CardFilter = { types: [], query: '', waiting: false, assignee: '' };

/** Whether this filter is hiding anything, which is what decides if the board says so out loud. */
export const isFiltering = (filter: CardFilter): boolean =>
  filter.types.length > 0 || filter.query.trim() !== '' || filter.waiting || filter.assignee !== '';

/**
 * Everything about a card that a search box can see: its key, its title, its
 * type, and the parent it hangs under. Not its description - the board does not
 * carry one (IssueCardDto), and searching text the browser does not have would
 * find nothing while looking like it worked.
 */
const haystack = (card: IssueCard): string =>
  `${card.key} ${card.title} ${card.type} ${card.parentKey ?? ''}`.toLowerCase();

/**
 * Whether a card matches what was typed. Every whitespace-separated term has to
 * appear somewhere, in any order - so "bug cert" finds the certificate bug
 * without anybody having to type the words the way the title has them.
 */
export function matchesQuery(card: IssueCard, query: string): boolean {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return true;

  const text = haystack(card);
  return terms.every((term) => text.includes(term));
}

/**
 * Whether a card belongs to whoever was chosen. Off matches everything, as an
 * unticked type toggle does; `UNASSIGNED` is the cards with no assignee at all,
 * which includes the ones whose person has been deleted or whose key has been
 * revoked - the server has already resolved those to null, so there is nothing
 * for the browser to know about it.
 */
export const matchesAssignee = (card: IssueCard, assignee: string): boolean => {
  if (assignee === '') return true;
  if (assignee === UNASSIGNED) return card.assignee === null;
  return card.assignee !== null && assigneeToken(card.assignee) === assignee;
};

export const matchesFilter = (card: IssueCard, filter: CardFilter): boolean =>
  (filter.types.length === 0 || filter.types.includes(card.type)) &&
  (!filter.waiting || card.openQuestions > 0) &&
  matchesAssignee(card, filter.assignee) &&
  matchesQuery(card, filter.query);

export const filterCards = (cards: IssueCard[], filter: CardFilter): IssueCard[] =>
  cards.filter((card) => matchesFilter(card, filter));

/**
 * The assignees the board actually has cards for, in picker order and with
 * duplicates collapsed.
 *
 * Derived from the cards rather than fetched, for the reason this whole module
 * gives: the board is already in the browser, and a facet offering somebody
 * with no cards on it is a choice that can only blank the screen. The picker on
 * the issue page is the other question - who *could* own this - and that one is
 * a read.
 */
export function assigneeFacets(cards: IssueCard[]): Assignee[] {
  const seen = new Map<string, Assignee>();
  for (const card of cards) {
    if (card.assignee) seen.set(assigneeToken(card.assignee), card.assignee);
  }

  return [...seen.values()].sort(compareAssignees);
}

/** Flips the waiting switch. */
export const toggleWaiting = (filter: CardFilter): CardFilter => ({ ...filter, waiting: !filter.waiting });

/** Adds or removes one type, which is what a row of toggles does to a filter. */
export const toggleType = (filter: CardFilter, type: IssueType): CardFilter => ({
  ...filter,
  types: filter.types.includes(type) ? filter.types.filter((t) => t !== type) : [...filter.types, type],
});
