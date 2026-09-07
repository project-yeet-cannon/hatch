import { describe, expect, it } from 'vitest';
import { dependencyCandidates } from './dependencies';
import type { IssueCard } from '../types';

const TODO = 1;

const card = (key: string, parentKey: string | null, projectKey = 'AER'): IssueCard => ({
  key,
  projectKey,
  type: 'task',
  title: `${key} title`,
  statusId: TODO,
  rank: 1024,
  parentKey,
  readyAt: null,
  dueAt: null,
  openQuestions: 0,
  claim: null,
});

const keys = (cards: IssueCard[]) => cards.map((c) => c.key);

/* An epic (AER-1) with two stories under it (AER-2, AER-5) and a task under
   each (AER-3, AER-6), plus a loose issue and one in another project. */
const cards: IssueCard[] = [
  card('AER-1', null),
  card('AER-2', 'AER-1'),
  card('AER-3', 'AER-2'),
  card('AER-5', 'AER-1'),
  card('AER-6', 'AER-5'),
  card('AER-9', null),
  card('OPS-4', null, 'OPS'),
];

describe('dependencyCandidates', () => {
  it('never offers the issue itself', () => {
    expect(keys(dependencyCandidates(cards, 'AER-9', []))).not.toContain('AER-9');
  });

  it('leaves out ancestors at any depth', () => {
    // An edge upwards is a deadlock with a nicer name: a parent is not done
    // until its work is.
    const offered = keys(dependencyCandidates(cards, 'AER-3', []));

    expect(offered).not.toContain('AER-2');
    expect(offered).not.toContain('AER-1');
  });

  it('leaves out descendants at any depth', () => {
    const offered = keys(dependencyCandidates(cards, 'AER-1', []));

    expect(offered).toEqual(['AER-9', 'OPS-4']);
  });

  it('still offers a sibling and its line', () => {
    // The whole point of the feature: two stories under one epic that must
    // land in order say so with an edge.
    expect(keys(dependencyCandidates(cards, 'AER-2', []))).toEqual(['AER-5', 'AER-6', 'AER-9', 'OPS-4']);
  });

  it('leaves out what it already waits on', () => {
    expect(keys(dependencyCandidates(cards, 'AER-9', ['OPS-4']))).toEqual([
      'AER-1',
      'AER-2',
      'AER-3',
      'AER-5',
      'AER-6',
    ]);
  });

  it('offers a card in another project', () => {
    // An edge may cross a project where a parent may not - containment and
    // ordering are different claims.
    expect(keys(dependencyCandidates(cards, 'AER-9', []))).toContain('OPS-4');
  });

  it('terminates on a parent cycle rather than spinning', () => {
    // Parenting refuses to close a loop, but one that got in some other way
    // must produce a wrong answer rather than hang the browser.
    const looped = [card('AER-1', 'AER-2'), card('AER-2', 'AER-1'), card('AER-9', null)];

    expect(keys(dependencyCandidates(looped, 'AER-1', []))).toEqual(['AER-9']);
  });

  it('offers everything on a board where nothing is related', () => {
    const loose = [card('AER-7', null), card('AER-8', null)];

    expect(keys(dependencyCandidates(loose, 'AER-7', []))).toEqual(['AER-8']);
  });
});
