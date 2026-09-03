/* The board's filter: which types to draw, and a search box.

   Deliberately dumb, and dumb in a stated way - the board already holds every
   issue in the house in the browser (BoardDto is one request, unpaged), so this
   is a pass over an array rather than a round trip, and it can afford to be
   obvious. Anything cleverer - fuzzy matching, ranking, stemming - would make
   "why is that card not showing" a question with no answer.

   Filtering is the browser's, not the server's, on purpose: the filter is a
   view of the board, and a board that refetched on every keystroke would drop
   the drag it was in the middle of. */

import type { IssueCard, IssueType } from '../types';

export interface CardFilter {
  /** The types to draw. Empty means every type - an empty filter shows the board, not nothing. */
  types: IssueType[];
  /** What was typed in the search box, raw. */
  query: string;
}

export const NO_FILTER: CardFilter = { types: [], query: '' };

/** Whether this filter is hiding anything, which is what decides if the board says so out loud. */
export const isFiltering = (filter: CardFilter): boolean =>
  filter.types.length > 0 || filter.query.trim() !== '';

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

export const matchesFilter = (card: IssueCard, filter: CardFilter): boolean =>
  (filter.types.length === 0 || filter.types.includes(card.type)) && matchesQuery(card, filter.query);

export const filterCards = (cards: IssueCard[], filter: CardFilter): IssueCard[] =>
  cards.filter((card) => matchesFilter(card, filter));

/** Adds or removes one type, which is what a row of toggles does to a filter. */
export const toggleType = (filter: CardFilter, type: IssueType): CardFilter => ({
  ...filter,
  types: filter.types.includes(type) ? filter.types.filter((t) => t !== type) : [...filter.types, type],
});
