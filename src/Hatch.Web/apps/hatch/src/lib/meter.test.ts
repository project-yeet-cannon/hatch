import { describe, expect, it } from 'vitest';
import { donePercent, meterLabel, meterSegments } from './meter';
import type { Rollup, Status } from '../types';

const status = (id: number, name: string, sortOrder: number, isTerminal = false): Status => ({
  id,
  name,
  sortOrder,
  isTerminal,
  isDeferred: false,
  color: '#6b7280',
});

/* The board as it is seeded, in the order the board hands it over. */
const TODO = status(2, 'To Do', 20);
const DOING = status(3, 'In Progress', 30);
const DONE = status(4, 'Done', 40, true);
const STATUSES = [status(1, 'Inbox', 10), TODO, DOING, DONE];

const rollup = (slices: [number, number][], done: number, waiting = 0): Rollup => ({
  leaves: slices.reduce((sum, [, count]) => sum + count, 0),
  done,
  waiting,
  slices: slices.map(([statusId, count]) => ({ statusId, count })),
});

const EMPTY: Rollup = { leaves: 0, done: 0, waiting: 0, slices: [] };

describe('meterSegments', () => {
  it('draws one segment per status holding a leaf, in board order', () => {
    const segments = meterSegments(rollup([[4, 12], [2, 20], [3, 11]], 12), STATUSES);

    expect(segments.map((s) => s.statusId)).toEqual([2, 3, 4]);
    expect(segments.map((s) => s.count)).toEqual([20, 11, 12]);
  });

  it('carries the name and the colour the operator painted the column', () => {
    const painted = [{ ...TODO, color: '#3ac200' }];
    const [segment] = meterSegments(rollup([[2, 3]], 0), painted);

    expect(segment).toMatchObject({ statusId: 2, name: 'To Do', color: '#3ac200', count: 3, percent: 100 });
  });

  it('omits a status no leaf is sitting in', () => {
    const segments = meterSegments(rollup([[2, 4]], 0), STATUSES);

    expect(segments).toHaveLength(1);
    expect(segments[0].statusId).toBe(2);
  });

  it('sums to exactly 100 percent where independent rounding would not', () => {
    /* Three equal thirds: 33.33 each, which rounds to 99 unless the leftover
       is apportioned. */
    const segments = meterSegments(rollup([[2, 1], [3, 1], [4, 1]], 1), STATUSES);

    expect(segments.map((s) => s.percent)).toEqual([34, 33, 33]);
    expect(segments.reduce((sum, s) => sum + s.percent, 0)).toBe(100);
  });

  it('sums to exactly 100 percent on a lopsided split too', () => {
    const segments = meterSegments(rollup([[2, 1], [3, 1], [4, 199]], 199), STATUSES);

    expect(segments.reduce((sum, s) => sum + s.percent, 0)).toBe(100);
    /* One task out of two hundred rounds to almost nothing, so the count is
       what the bar has to draw from - see StatusMeter's floor on a segment. */
    expect(segments.map((s) => s.count)).toEqual([1, 1, 199]);
    expect(segments[2].percent).toBe(99);
  });

  it('drops a slice naming a status that no longer exists rather than throwing', () => {
    const segments = meterSegments(rollup([[2, 3], [99, 5]], 0), STATUSES);

    expect(segments.map((s) => s.statusId)).toEqual([2]);
    expect(segments[0].percent).toBe(100);
  });

  it('draws nothing for a rollup with no leaves', () => {
    expect(meterSegments(EMPTY, STATUSES)).toEqual([]);
  });

  it('draws nothing when every slice names a status that is gone', () => {
    expect(meterSegments(rollup([[98, 2], [99, 3]], 0), STATUSES)).toEqual([]);
  });
});

describe('donePercent', () => {
  it('is the share of every leaf sitting in a terminal column', () => {
    expect(donePercent(rollup([[2, 20], [3, 11], [4, 12]], 12))).toBe(28);
  });

  it('is zero rather than a division by nothing on an empty rollup', () => {
    expect(donePercent(EMPTY)).toBe(0);
  });
});

describe('meterLabel', () => {
  it('says the headline, then the spread', () => {
    const label = meterLabel(rollup([[2, 20], [3, 11], [4, 12]], 12), STATUSES);

    expect(label).toBe('12 of 43 done — 20 to do, 11 in progress, 12 done');
  });

  it('leaves out the status that is gone, as the bar does', () => {
    expect(meterLabel(rollup([[2, 3], [99, 5]], 0), STATUSES)).toBe('0 of 8 done — 3 to do');
  });

  it('says nothing is filed rather than reading as nothing done', () => {
    expect(meterLabel(EMPTY, STATUSES)).toBe('Nothing filed yet');
  });
});
