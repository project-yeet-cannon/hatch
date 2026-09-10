import { describe, expect, it } from 'vitest';
import { UNASSIGNED, assigneeToken } from './assignee';
import { NO_FILTER, assigneeFacets, filterCards, isFiltering, matchesQuery, toggleType, toggleWaiting } from './filter';
import type { Assignee, IssueCard, IssueType } from '../types';

const card = (over: Partial<IssueCard> = {}): IssueCard => ({
  key: 'AER-1',
  projectKey: 'AER',
  type: 'task',
  title: 'Renew the wildcard certificate',
  statusId: 1,
  rank: 1024,
  parentKey: null,
  readyAt: null,
  dueAt: null,
  openQuestions: 0,
  assignee: null,
  claim: null,
  expedited: false,
  ...over,
});

const person = (name: string, id = `p-${name}`): Assignee => ({ kind: 'person', id, name });
const key = (name: string, id = `k-${name}`): Assignee => ({ kind: 'key', id, name });

/* Every literal filter below is spread onto this rather than written whole, so
   that adding a switch to CardFilter is one edit here and not one per case. */
const filter = (over: Partial<typeof NO_FILTER> = {}) => ({ ...NO_FILTER, ...over });

describe('matchesQuery', () => {
  it('matches nothing in particular when nothing was typed', () => {
    expect(matchesQuery(card(), '')).toBe(true);
    expect(matchesQuery(card(), '   ')).toBe(true);
  });

  it('reads a title in any case', () => {
    expect(matchesQuery(card(), 'WILDCARD')).toBe(true);
    expect(matchesQuery(card({ title: 'FEED THE CAT' }), 'cat')).toBe(true);
  });

  it('finds a card by the key somebody pasted in', () => {
    expect(matchesQuery(card({ key: 'OPS-42' }), 'ops-42')).toBe(true);
  });

  it('finds a card by its type and by the parent it hangs under', () => {
    expect(matchesQuery(card({ type: 'bug' }), 'bug')).toBe(true);
    expect(matchesQuery(card({ parentKey: 'AER-9' }), 'aer-9')).toBe(true);
  });

  /* The whole point of splitting on whitespace: nobody remembers the word order
     of a title they wrote three weeks ago. */
  it('takes several terms in any order, and wants all of them', () => {
    expect(matchesQuery(card(), 'certificate renew')).toBe(true);
    expect(matchesQuery(card(), 'renew kitchen')).toBe(false);
  });

  it('says no when a term appears nowhere', () => {
    expect(matchesQuery(card(), 'thermostat')).toBe(false);
  });
});

describe('filterCards', () => {
  const cards = [
    card({ key: 'AER-1', type: 'epic', title: 'The plan' }),
    card({ key: 'AER-2', type: 'bug', title: 'The certificate expired' }),
    card({ key: 'AER-3', type: 'task', title: 'Renew the certificate' }),
  ];

  it('draws the whole board when nothing is filtered', () => {
    expect(filterCards(cards, NO_FILTER)).toHaveLength(3);
  });

  it('keeps only the chosen types', () => {
    expect(filterCards(cards, filter({ types: ['bug'] })).map((c) => c.key)).toEqual(['AER-2']);
  });

  it('takes more than one type at a time', () => {
    expect(filterCards(cards, filter({ types: ['bug', 'epic'] })).map((c) => c.key)).toEqual(['AER-1', 'AER-2']);
  });

  /* An empty type list is "every type", not "no types". A board that goes blank
     when the last chip is switched off reads as broken. */
  it('treats no chosen types as every type', () => {
    expect(filterCards(cards, filter())).toHaveLength(3);
  });

  it('applies the type filter and the search together', () => {
    expect(filterCards(cards, filter({ types: ['task'], query: 'certificate' })).map((c) => c.key)).toEqual(['AER-3']);
  });

  it('keeps only the cards holding an unanswered question', () => {
    const asked = [...cards, card({ key: 'AER-4', title: 'Retry policy', openQuestions: 2 })];

    expect(filterCards(asked, filter({ waiting: true })).map((c) => c.key)).toEqual(['AER-4']);
  });

  /* The switch narrows alongside everything else rather than replacing it, so
     "the bugs that are waiting on me" is one board and not two passes. */
  it('narrows with the type filter rather than instead of it', () => {
    const asked = [
      card({ key: 'AER-4', type: 'bug', openQuestions: 1 }),
      card({ key: 'AER-5', type: 'task', openQuestions: 1 }),
    ];

    expect(filterCards(asked, filter({ waiting: true, types: ['bug'] })).map((c) => c.key)).toEqual(['AER-4']);
  });
});

describe('isFiltering', () => {
  it('is false only when the board is showing everything', () => {
    expect(isFiltering(NO_FILTER)).toBe(false);
    expect(isFiltering(filter({ query: '  ' }))).toBe(false);
    expect(isFiltering(filter({ types: ['bug'] }))).toBe(true);
    expect(isFiltering(filter({ query: 'cert' }))).toBe(true);
    expect(isFiltering(filter({ waiting: true }))).toBe(true);
    expect(isFiltering(filter({ assignee: UNASSIGNED }))).toBe(true);
    expect(isFiltering(filter({ assignee: assigneeToken(person('Ada')) }))).toBe(true);
  });
});

describe('the assignee facet', () => {
  const ada = person('Ada');
  const claude = key('Claude');

  const board = [
    card({ key: 'AER-1', assignee: ada }),
    card({ key: 'AER-2', assignee: claude }),
    card({ key: 'AER-3', assignee: null }),
    card({ key: 'AER-4', assignee: { ...ada, name: 'Ada Lovelace' } }),
  ];

  it('shows the whole board when nobody was chosen', () => {
    expect(filterCards(board, NO_FILTER)).toHaveLength(4);
  });

  it('draws one identity\'s cards, whatever name they were drawn under', () => {
    // The token is the kind and the id; a rename between reads does not split
    // one person into two facets.
    const mine = filter({ assignee: assigneeToken(ada) });
    expect(filterCards(board, mine).map((c) => c.key)).toEqual(['AER-1', 'AER-4']);
  });

  it('draws only the cards nobody owns', () => {
    expect(filterCards(board, filter({ assignee: UNASSIGNED })).map((c) => c.key)).toEqual(['AER-3']);
  });

  it('ANDs with the other facets, as they AND with each other', () => {
    const bug = card({ key: 'AER-5', type: 'bug', assignee: ada });
    const narrowed = filter({ assignee: assigneeToken(ada), types: ['bug'] });

    expect(filterCards([...board, bug], narrowed).map((c) => c.key)).toEqual(['AER-5']);
  });
});

describe('assigneeFacets', () => {
  it('is the assignees the board actually has, deduped and in picker order', () => {
    const ada = person('Ada');
    const zoe = person('Zoe');
    const claude = key('Claude');

    const facets = assigneeFacets([
      card({ assignee: claude }),
      card({ assignee: zoe }),
      card({ assignee: null }),
      card({ assignee: ada }),
      card({ assignee: ada }),
    ]);

    expect(facets.map((a) => a.name)).toEqual(['Ada', 'Zoe', 'Claude']);
  });

  it('offers nobody at all for a board nobody owns anything on', () => {
    // An "Unassigned" entry is BoardFilters' own, added above these rows:
    // deriving one from cards that have no assignee would be deriving a fact
    // from its own absence.
    expect(assigneeFacets([card(), card()])).toEqual([]);
  });
});

describe('toggleWaiting', () => {
  it('turns the switch on and off again', () => {
    const on = toggleWaiting(NO_FILTER);
    expect(on.waiting).toBe(true);
    expect(toggleWaiting(on).waiting).toBe(false);
  });

  it('leaves the rest of the filter alone', () => {
    const narrowed = toggleWaiting(filter({ types: ['bug'], query: 'cert' }));
    expect(narrowed.types).toEqual(['bug']);
    expect(narrowed.query).toBe('cert');
  });
});

describe('toggleType', () => {
  it('adds a type that is off and removes one that is on', () => {
    const once = toggleType(NO_FILTER, 'bug');
    expect(once.types).toEqual(['bug']);
    expect(toggleType(once, 'bug').types).toEqual([]);
  });

  it('leaves the query alone', () => {
    expect(toggleType(filter({ query: 'cert' }), 'epic' as IssueType).query).toBe('cert');
  });
});
