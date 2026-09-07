/* The parent picker's order and its matching.

   Everything in the picker that has an argument in it, in one module with no
   React in it, because `apps/hatch` has vitest and no DOM: the rule goes in
   `lib/` and is tested, and the layout and the keyboard are looked at in a
   browser. What is left in the component is state and event wiring.

   The order is by key rather than by whatever order the board handed the cards
   over in. BoardController sorts by (StatusId, Rank, Id) so the client can
   slice one list into columns, which from inside a picker reads as arbitrary
   and re-shuffles itself whenever a card is dragged. */

import type { IssueCard } from '../types';

/** A key split into the parts it sorts by, or null when it is not that shape. */
export interface KeyParts {
  project: string;
  index: number;
}

/**
 * Splits `AERIE-745` into `AERIE` and 745.
 *
 * Mirrors IssueKey.TryParse on the server, which is the parse done to the same
 * string at the other end of the wire - including its remark, which is worth
 * keeping: the split is on the *last* hyphen because project keys cannot hold
 * one today, and relying on that "would make the day one can a day when every
 * old key silently resolves to the wrong project".
 *
 * The tail is tested with a regex rather than handed to `Number` alone, which
 * reads `''` and `1e3` as numbers, or to `parseInt`, which reads `12abc` as 12.
 * That is stricter than the server's `int.TryParse` - it takes a sign and
 * surrounding space - and a superset of the keys IssueKey.Format can produce,
 * which is the only source of these strings.
 */
export function splitKey(key: string): KeyParts | null {
  const hyphen = key.lastIndexOf('-');
  if (hyphen <= 0 || hyphen === key.length - 1) return null;

  const tail = key.slice(hyphen + 1);
  if (!/^\d+$/.test(tail)) return null;

  const index = Number(tail);
  if (index <= 0) return null;

  // Upper-cased as the server does: a key typed in lower case is not a mistake
  // - see normalizeProjectKey in lib/projectKey.ts.
  return { project: key.slice(0, hyphen).toUpperCase(), index };
}

/**
 * Project A→Z, then the number after the dash ascending - so AERIE-9 comes
 * before AERIE-10 and AERIE-1000 comes last rather than second.
 *
 * A key that does not split sorts after every key that does, ordered against
 * its own kind by plain text. Keys come off the wire and a comparator is not
 * the place to discover a malformed one.
 *
 * The project half is compared first even though nothing on the issue page can
 * produce a cross-project list: the component is built for the bulk page next,
 * where the list is every issue in the house, and a comparator that only knew
 * about numbers would interleave two projects into nonsense.
 */
export function compareIssueKeys(a: string, b: string): number {
  const left = splitKey(a);
  const right = splitKey(b);

  if (!left || !right) {
    if (left) return -1;
    if (right) return 1;
    return a < b ? -1 : a > b ? 1 : 0;
  }

  if (left.project !== right.project) return left.project < right.project ? -1 : 1;
  return left.index - right.index;
}

/**
 * The key and the title, and nothing else.
 *
 * Deliberately not the board's haystack (see `haystack` in lib/filter.ts),
 * which also carries the type and the parent key. That is right for a board
 * filter and noise here: this list is already filtered to the types the issue
 * may legally hang under, so typing `epic` matching every row would tell
 * nobody anything.
 */
const haystack = (card: IssueCard): string => `${card.key} ${card.title}`.toLowerCase();

/**
 * Whether a candidate matches what was typed. Every whitespace-separated term
 * has to appear somewhere, in any order - the same rule matchesQuery follows,
 * so the two search boxes in this app behave the same way. An empty or
 * whitespace-only query matches everything.
 *
 * Matching on any part of the key rather than on the leading text is the whole
 * complaint the picker is answering: a native select does prefix typeahead, so
 * typing `745` found nothing because every option starts with the project key.
 */
export function matchesPickerQuery(card: IssueCard, query: string): boolean {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return true;

  const text = haystack(card);
  return terms.every((term) => text.includes(term));
}

/**
 * The rows to draw: the candidates the query matches, in key order.
 *
 * Sorts the array `filter` already copied - it must not mutate what it was
 * handed, because `board.issues` is state other things on the page are drawing
 * from. Filtering before sorting is also the cheaper order.
 */
export function pickerRows(cards: IssueCard[], query: string): IssueCard[] {
  return cards
    .filter((card) => matchesPickerQuery(card, query))
    .sort((a, b) => compareIssueKeys(a.key, b.key));
}
