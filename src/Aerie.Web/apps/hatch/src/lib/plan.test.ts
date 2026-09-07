import { describe, expect, it } from 'vitest';
import { comparePlanEntries, fractionDone, isFinished, orderPlan } from './plan';
import type { PlanEntry, Rollup } from '../types';

const rollup = (leaves: number, done: number, waiting = 0): Rollup => ({
  leaves,
  done,
  waiting,
  // The page never reads the slices through this module; the meter does, and
  // meter.test.ts is where they are exercised.
  slices: [],
});

const epic = (key: string, leaves: number, done: number, children: PlanEntry[] = []): PlanEntry => ({
  issue: {
    key,
    projectKey: key.split('-')[0],
    type: 'epic',
    title: key,
    statusId: 2,
    rank: 0,
    parentKey: null,
    readyAt: null,
    dueAt: null,
    openQuestions: 0,
    claim: null,
  },
  isLeaf: children.length === 0 && leaves === 0,
  rollup: rollup(leaves, done),
  children,
});

const keys = (entries: PlanEntry[]) => entries.map((entry) => entry.issue.key);

describe('fractionDone', () => {
  it('is the share of leaves in a terminal column', () => {
    expect(fractionDone(rollup(4, 1))).toBe(0.25);
    expect(fractionDone(rollup(43, 12))).toBeCloseTo(12 / 43);
  });

  it('is 0 when nothing is filed, not 1 - an empty epic is at the start of its life', () => {
    expect(fractionDone(rollup(0, 0))).toBe(0);
  });
});

describe('isFinished', () => {
  it('is true only when every leaf is done', () => {
    expect(isFinished(epic('AER-1', 5, 5))).toBe(true);
    expect(isFinished(epic('AER-2', 5, 4))).toBe(false);
  });

  it('is false for an epic with nothing under it', () => {
    expect(isFinished(epic('AER-3', 0, 0))).toBe(false);
  });
});

describe('orderPlan', () => {
  it('puts the epic nearest the line at the top', () => {
    const ordered = orderPlan([epic('AER-1', 10, 1), epic('AER-2', 10, 8), epic('AER-3', 10, 4)]);

    expect(keys(ordered.live)).toEqual(['AER-2', 'AER-3', 'AER-1']);
  });

  it('moves a finished epic out of the live list and into Finished', () => {
    const ordered = orderPlan([epic('AER-1', 4, 4), epic('AER-2', 10, 2)]);

    expect(keys(ordered.live)).toEqual(['AER-2']);
    expect(keys(ordered.finished)).toEqual(['AER-1']);
  });

  it('keeps a finished epic below the live ones however far along they are', () => {
    // The whole point of the split: 4/4 would otherwise outrank 9/10 forever.
    const ordered = orderPlan([epic('AER-1', 4, 4), epic('AER-2', 10, 9)]);

    expect(keys(ordered.live)).toEqual(['AER-2']);
    expect(keys(ordered.finished)).toEqual(['AER-1']);
  });

  it('leaves an empty epic in the live list, at the bottom', () => {
    const ordered = orderPlan([epic('AER-1', 0, 0), epic('AER-2', 8, 1)]);

    expect(keys(ordered.live)).toEqual(['AER-2', 'AER-1']);
    expect(ordered.finished).toEqual([]);
  });

  it('breaks a tie on fraction with the larger epic', () => {
    const ordered = orderPlan([epic('AER-1', 4, 2), epic('AER-2', 20, 10), epic('AER-3', 8, 4)]);

    expect(keys(ordered.live)).toEqual(['AER-2', 'AER-3', 'AER-1']);
  });

  it('breaks a tie on size with the key, numerically', () => {
    const ordered = orderPlan([epic('AER-10', 6, 3), epic('AER-9', 6, 3), epic('AER-2', 6, 3)]);

    expect(keys(ordered.live)).toEqual(['AER-2', 'AER-9', 'AER-10']);
  });

  it('orders across projects by project key before number', () => {
    const ordered = orderPlan([epic('ZED-1', 6, 3), epic('AER-99', 6, 3)]);

    expect(keys(ordered.live)).toEqual(['AER-99', 'ZED-1']);
  });

  it('is stable across refetches - the same plan in any arrival order sorts the same', () => {
    const plan = [epic('AER-1', 6, 3), epic('AER-2', 6, 3), epic('AER-3', 10, 5), epic('AER-4', 2, 2)];
    const shuffled = [plan[2], plan[0], plan[3], plan[1]];

    expect(keys(orderPlan(plan).live)).toEqual(keys(orderPlan(shuffled).live));
    expect(keys(orderPlan(plan).finished)).toEqual(keys(orderPlan(shuffled).finished));
  });

  it('sorts the finished section too, largest first', () => {
    const ordered = orderPlan([epic('AER-1', 3, 3), epic('AER-2', 30, 30)]);

    expect(keys(ordered.finished)).toEqual(['AER-2', 'AER-1']);
  });

  it('applies the same order to nested epics', () => {
    const parent = epic('AER-1', 20, 5, [epic('AER-5', 10, 1), epic('AER-6', 10, 7)]);
    const ordered = orderPlan([parent]);

    expect(keys(ordered.live[0].children)).toEqual(['AER-6', 'AER-5']);
  });

  it('leaves a finished child nested where it is, at the end of its parent', () => {
    const parent = epic('AER-1', 20, 12, [epic('AER-5', 10, 10), epic('AER-6', 10, 2)]);
    const ordered = orderPlan([parent]);

    expect(keys(ordered.live)).toEqual(['AER-1']);
    expect(keys(ordered.live[0].children)).toEqual(['AER-6', 'AER-5']);
  });

  it('does not mutate what it was handed', () => {
    const plan = [epic('AER-1', 10, 1), epic('AER-2', 10, 8)];
    orderPlan(plan);

    expect(keys(plan)).toEqual(['AER-1', 'AER-2']);
  });

  it('has nothing to say about an empty tracker', () => {
    expect(orderPlan([])).toEqual({ live: [], finished: [] });
  });
});

describe('comparePlanEntries', () => {
  it('is antisymmetric on every branch it decides', () => {
    const pairs: [PlanEntry, PlanEntry][] = [
      [epic('AER-1', 10, 8), epic('AER-2', 10, 1)],
      [epic('AER-1', 10, 3), epic('AER-2', 4, 4)],
      [epic('AER-1', 20, 10), epic('AER-2', 4, 2)],
      [epic('AER-1', 6, 3), epic('AER-2', 6, 3)],
    ];

    for (const [a, b] of pairs) {
      expect(Math.sign(comparePlanEntries(a, b))).toBe(-Math.sign(comparePlanEntries(b, a)));
    }
  });

  it('calls an entry equal to itself', () => {
    const only = epic('AER-1', 6, 3);
    expect(comparePlanEntries(only, only)).toBe(0);
  });
});
