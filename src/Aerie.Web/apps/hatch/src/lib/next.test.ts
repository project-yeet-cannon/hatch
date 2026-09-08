import { describe, expect, it } from 'vitest';
import { waitingChild } from './next';
import type { ChildRollup, IssueRollup } from '../types';

const child = (key: string, waiting: number): ChildRollup => ({
  issue: {
    key,
    projectKey: key.split('-')[0],
    type: 'story',
    title: key,
    statusId: 2,
    rank: 0,
    parentKey: 'AER-1',
    readyAt: null,
    dueAt: null,
    openQuestions: waiting,
    assignee: null,
    claim: null,
  },
  isLeaf: true,
  rollup: { leaves: 1, done: 0, waiting, slices: [] },
});

/** A parent whose own waiting total is the sum of its children's unless a
    third argument says otherwise - which is how a question asked on the parent
    itself shows up. */
const under = (children: ChildRollup[], waiting?: number): IssueRollup => ({
  key: 'AER-1',
  rollup: {
    leaves: children.length,
    done: 0,
    waiting: waiting ?? children.reduce((sum, c) => sum + c.rollup.waiting, 0),
    slices: [],
  },
  children,
});

describe('waitingChild', () => {
  it('is the one child holding every unanswered question below', () => {
    expect(waitingChild(under([child('AER-2', 0), child('AER-3', 2)]))).toBe('AER-3');
  });

  it('is null when nothing is waiting', () => {
    expect(waitingChild(under([child('AER-2', 0), child('AER-3', 0)]))).toBeNull();
  });

  it('is null when two children are waiting - there is no one ticket to send anybody to', () => {
    expect(waitingChild(under([child('AER-2', 1), child('AER-3', 1)]))).toBeNull();
  });

  it('is null when the issue itself is holding one, even with a single waiting child', () => {
    // 2 below, 3 in total: the third is on the issue being read, and it is
    // already in the card at the top of that page.
    expect(waitingChild(under([child('AER-2', 2)], 3))).toBeNull();
  });

  it('is null when there are no children at all', () => {
    expect(waitingChild(under([]))).toBeNull();
  });
});
