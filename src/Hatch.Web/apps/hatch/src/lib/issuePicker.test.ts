import { describe, expect, it } from 'vitest';
import { compareIssueKeys, matchesPickerQuery, pickerRows, splitKey } from './issuePicker';
import type { IssueCard } from '../types';

/* Spread onto rather than written whole, so a new field on IssueCard is one
   edit here and not one per case - the same shape filter.test.ts uses. */
const card = (over: Partial<IssueCard> = {}): IssueCard => ({
  key: 'HATCH-1',
  projectKey: 'HATCH',
  type: 'epic',
  title: 'Hatch Parent Issue Picker Update',
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

/** The keys, in the order the picker would draw them. */
const ordered = (...keys: string[]): string[] => [...keys].sort(compareIssueKeys);

describe('splitKey', () => {
  it('splits a key into its project and its number', () => {
    expect(splitKey('HATCH-745')).toEqual({ project: 'HATCH', index: 745 });
  });

  it('upper-cases the project half, as the server does', () => {
    expect(splitKey('hatch-745')).toEqual({ project: 'HATCH', index: 745 });
  });

  it('splits on the last hyphen, not the first', () => {
    expect(splitKey('A-B-12')).toEqual({ project: 'A-B', index: 12 });
  });

  it('refuses anything that is not a project and a number', () => {
    expect(splitKey('HATCH')).toBeNull(); // no hyphen at all
    expect(splitKey('HATCH-')).toBeNull(); // nothing after it
    expect(splitKey('-745')).toBeNull(); // nothing before it
    expect(splitKey('')).toBeNull();
    expect(splitKey('HATCH-12a')).toBeNull(); // not digits
    expect(splitKey('HATCH-1e3')).toBeNull(); // a number to Number, not a key
    expect(splitKey('HATCH-0')).toBeNull(); // numbers start at one
  });
});

describe('compareIssueKeys', () => {
  it('reads the number after the dash as a number', () => {
    expect(ordered('HATCH-10', 'HATCH-1000', 'HATCH-9')).toEqual(['HATCH-9', 'HATCH-10', 'HATCH-1000']);
  });

  it('orders by project first, then by number', () => {
    expect(ordered('HATCH-1', 'AER-2', 'HATCH-2', 'AER-10')).toEqual([
      'AER-2',
      'AER-10',
      'HATCH-1',
      'HATCH-2',
    ]);
  });

  it('ignores case on the project half', () => {
    expect(ordered('HATCH-2', 'hatch-1', 'ZZ-1')).toEqual(['hatch-1', 'HATCH-2', 'ZZ-1']);
  });

  it('sorts a key it cannot read after every key it can', () => {
    expect(ordered('junk', 'HATCH-9', 'HATCH-1')).toEqual(['HATCH-1', 'HATCH-9', 'junk']);
  });

  it('orders two unreadable keys against each other by plain text', () => {
    expect(ordered('zebra', 'HATCH-1', 'apple')).toEqual(['HATCH-1', 'apple', 'zebra']);
    expect(compareIssueKeys('junk', 'junk')).toBe(0);
  });
});

describe('matchesPickerQuery', () => {
  it('matches everything when nothing was typed', () => {
    expect(matchesPickerQuery(card(), '')).toBe(true);
    expect(matchesPickerQuery(card(), '   ')).toBe(true);
  });

  it('reaches a key by the number alone, without the project key being typed', () => {
    expect(matchesPickerQuery(card({ key: 'HATCH-745' }), '745')).toBe(true);
  });

  it('matches any part of a title', () => {
    expect(matchesPickerQuery(card(), 'picker')).toBe(true);
  });

  it('ignores case, both ways', () => {
    expect(matchesPickerQuery(card({ key: 'HATCH-745' }), 'hatch-745')).toBe(true);
    expect(matchesPickerQuery(card({ title: 'FEED THE CAT' }), 'cat')).toBe(true);
  });

  it('wants every term, in any order', () => {
    const it745 = card({ key: 'HATCH-745' });
    expect(matchesPickerQuery(it745, '745 picker')).toBe(true);
    expect(matchesPickerQuery(it745, 'picker 745')).toBe(true);
    expect(matchesPickerQuery(it745, 'picker 746')).toBe(false);
  });

  it('does not see the type or the parent key', () => {
    // The list is already filtered to legal types, so a query matching every
    // row by type would be noise. Unlike the board's filter, deliberately.
    expect(matchesPickerQuery(card({ type: 'epic', title: 'A quiet title' }), 'epic')).toBe(false);
    expect(matchesPickerQuery(card({ parentKey: 'HATCH-9', title: 'A quiet title' }), 'hatch-9')).toBe(false);
  });
});

describe('pickerRows', () => {
  const cards = [
    card({ key: 'HATCH-10', title: 'The board is read' }),
    card({ key: 'HATCH-2', title: 'Parent picker, the rule' }),
    card({ key: 'HATCH-1000', title: 'Parent picker, the control' }),
    card({ key: 'HATCH-9', title: 'Something else entirely' }),
  ];

  it('returns everything in key order when nothing was typed', () => {
    expect(pickerRows(cards, '').map((c) => c.key)).toEqual([
      'HATCH-2',
      'HATCH-9',
      'HATCH-10',
      'HATCH-1000',
    ]);
  });

  it('filters and orders in one pass', () => {
    expect(pickerRows(cards, 'picker').map((c) => c.key)).toEqual(['HATCH-2', 'HATCH-1000']);
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
