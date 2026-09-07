import { describe, expect, it } from 'vitest';
import { compareIssueKeys, matchesPickerQuery, pickerRows, splitKey } from './issuePicker';
import type { IssueCard } from '../types';

/* Spread onto rather than written whole, so a new field on IssueCard is one
   edit here and not one per case - the same shape filter.test.ts uses. */
const card = (over: Partial<IssueCard> = {}): IssueCard => ({
  key: 'AERIE-1',
  projectKey: 'AERIE',
  type: 'epic',
  title: 'Hatch Parent Issue Picker Update',
  statusId: 1,
  rank: 1024,
  parentKey: null,
  readyAt: null,
  dueAt: null,
  openQuestions: 0,
  ...over,
});

/** The keys, in the order the picker would draw them. */
const ordered = (...keys: string[]): string[] => [...keys].sort(compareIssueKeys);

describe('splitKey', () => {
  it('splits a key into its project and its number', () => {
    expect(splitKey('AERIE-745')).toEqual({ project: 'AERIE', index: 745 });
  });

  it('upper-cases the project half, as the server does', () => {
    expect(splitKey('aerie-745')).toEqual({ project: 'AERIE', index: 745 });
  });

  it('splits on the last hyphen, not the first', () => {
    expect(splitKey('A-B-12')).toEqual({ project: 'A-B', index: 12 });
  });

  it('refuses anything that is not a project and a number', () => {
    expect(splitKey('AERIE')).toBeNull(); // no hyphen at all
    expect(splitKey('AERIE-')).toBeNull(); // nothing after it
    expect(splitKey('-745')).toBeNull(); // nothing before it
    expect(splitKey('')).toBeNull();
    expect(splitKey('AERIE-12a')).toBeNull(); // not digits
    expect(splitKey('AERIE-1e3')).toBeNull(); // a number to Number, not a key
    expect(splitKey('AERIE-0')).toBeNull(); // numbers start at one
  });
});

describe('compareIssueKeys', () => {
  it('reads the number after the dash as a number', () => {
    expect(ordered('AERIE-10', 'AERIE-1000', 'AERIE-9')).toEqual(['AERIE-9', 'AERIE-10', 'AERIE-1000']);
  });

  it('orders by project first, then by number', () => {
    expect(ordered('AERIE-1', 'AER-2', 'AERIE-2', 'AER-10')).toEqual([
      'AER-2',
      'AER-10',
      'AERIE-1',
      'AERIE-2',
    ]);
  });

  it('ignores case on the project half', () => {
    expect(ordered('AERIE-2', 'aerie-1', 'ZZ-1')).toEqual(['aerie-1', 'AERIE-2', 'ZZ-1']);
  });

  it('sorts a key it cannot read after every key it can', () => {
    expect(ordered('junk', 'AERIE-9', 'AERIE-1')).toEqual(['AERIE-1', 'AERIE-9', 'junk']);
  });

  it('orders two unreadable keys against each other by plain text', () => {
    expect(ordered('zebra', 'AERIE-1', 'apple')).toEqual(['AERIE-1', 'apple', 'zebra']);
    expect(compareIssueKeys('junk', 'junk')).toBe(0);
  });
});

describe('matchesPickerQuery', () => {
  it('matches everything when nothing was typed', () => {
    expect(matchesPickerQuery(card(), '')).toBe(true);
    expect(matchesPickerQuery(card(), '   ')).toBe(true);
  });

  it('reaches a key by the number alone, without the project key being typed', () => {
    expect(matchesPickerQuery(card({ key: 'AERIE-745' }), '745')).toBe(true);
  });

  it('matches any part of a title', () => {
    expect(matchesPickerQuery(card(), 'picker')).toBe(true);
  });

  it('ignores case, both ways', () => {
    expect(matchesPickerQuery(card({ key: 'AERIE-745' }), 'aerie-745')).toBe(true);
    expect(matchesPickerQuery(card({ title: 'FEED THE CAT' }), 'cat')).toBe(true);
  });

  it('wants every term, in any order', () => {
    const it745 = card({ key: 'AERIE-745' });
    expect(matchesPickerQuery(it745, '745 picker')).toBe(true);
    expect(matchesPickerQuery(it745, 'picker 745')).toBe(true);
    expect(matchesPickerQuery(it745, 'picker 746')).toBe(false);
  });

  it('does not see the type or the parent key', () => {
    // The list is already filtered to legal types, so a query matching every
    // row by type would be noise. Unlike the board's filter, deliberately.
    expect(matchesPickerQuery(card({ type: 'epic', title: 'A quiet title' }), 'epic')).toBe(false);
    expect(matchesPickerQuery(card({ parentKey: 'AERIE-9', title: 'A quiet title' }), 'aerie-9')).toBe(false);
  });
});

describe('pickerRows', () => {
  const cards = [
    card({ key: 'AERIE-10', title: 'The board is read' }),
    card({ key: 'AERIE-2', title: 'Parent picker, the rule' }),
    card({ key: 'AERIE-1000', title: 'Parent picker, the control' }),
    card({ key: 'AERIE-9', title: 'Something else entirely' }),
  ];

  it('returns everything in key order when nothing was typed', () => {
    expect(pickerRows(cards, '').map((c) => c.key)).toEqual([
      'AERIE-2',
      'AERIE-9',
      'AERIE-10',
      'AERIE-1000',
    ]);
  });

  it('filters and orders in one pass', () => {
    expect(pickerRows(cards, 'picker').map((c) => c.key)).toEqual(['AERIE-2', 'AERIE-1000']);
  });

  it('answers with nothing when nothing matches', () => {
    expect(pickerRows(cards, 'nothing here')).toEqual([]);
  });

  it('leaves the array it was handed exactly as it was', () => {
    const before = cards.map((c) => c.key);
    pickerRows(cards, '');
    expect(cards.map((c) => c.key)).toEqual(before);
  });
});
