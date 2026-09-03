import { describe, expect, it } from 'vitest';
import { NO_FILTER, filterCards, isFiltering, matchesQuery, toggleType } from './filter';
import type { IssueCard, IssueType } from '../types';

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
  ...over,
});

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
    expect(filterCards(cards, { types: ['bug'], query: '' }).map((c) => c.key)).toEqual(['AER-2']);
  });

  it('takes more than one type at a time', () => {
    expect(filterCards(cards, { types: ['bug', 'epic'], query: '' }).map((c) => c.key)).toEqual(['AER-1', 'AER-2']);
  });

  /* An empty type list is "every type", not "no types". A board that goes blank
     when the last chip is switched off reads as broken. */
  it('treats no chosen types as every type', () => {
    expect(filterCards(cards, { types: [], query: '' })).toHaveLength(3);
  });

  it('applies the type filter and the search together', () => {
    expect(filterCards(cards, { types: ['task'], query: 'certificate' }).map((c) => c.key)).toEqual(['AER-3']);
  });
});

describe('isFiltering', () => {
  it('is false only when the board is showing everything', () => {
    expect(isFiltering(NO_FILTER)).toBe(false);
    expect(isFiltering({ types: [], query: '  ' })).toBe(false);
    expect(isFiltering({ types: ['bug'], query: '' })).toBe(true);
    expect(isFiltering({ types: [], query: 'cert' })).toBe(true);
  });
});

describe('toggleType', () => {
  it('adds a type that is off and removes one that is on', () => {
    const once = toggleType(NO_FILTER, 'bug');
    expect(once.types).toEqual(['bug']);
    expect(toggleType(once, 'bug').types).toEqual([]);
  });

  it('leaves the query alone', () => {
    expect(toggleType({ types: [], query: 'cert' }, 'epic' as IssueType).query).toBe('cert');
  });
});
