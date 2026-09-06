import { describe, expect, it } from 'vitest';
import { childTypes } from './childTypes';
import { ISSUE_TYPES, LEGAL_PARENT_TYPES } from '../types';

describe('childTypes', () => {
  it('offers what each parent may take, in the order a picker draws them', () => {
    // The order is the promise, not just the membership: the first entry is
    // what a picker starts on, and it should be the ordinary child - a story
    // under an epic, a task under a story or a bug.
    expect(childTypes('epic')).toEqual(['epic', 'story', 'bug']);
    expect(childTypes('story')).toEqual(['task', 'bug']);
    expect(childTypes('bug')).toEqual(['task']);
  });

  it('offers nothing under a task', () => {
    // Which is what makes the composer absent there rather than empty.
    expect(childTypes('task')).toEqual([]);
  });

  it('agrees with LEGAL_PARENT_TYPES both ways round, for every pair', () => {
    // The property that keeps the two directions from drifting: one table,
    // read forwards by the parent picker and backwards by the composer.
    for (const parent of ISSUE_TYPES) {
      for (const child of ISSUE_TYPES) {
        expect(childTypes(parent).includes(child)).toBe(LEGAL_PARENT_TYPES[child].includes(parent));
      }
    }
  });

  it('only ever names a type that exists', () => {
    for (const parent of ISSUE_TYPES) {
      for (const offered of childTypes(parent)) {
        expect(ISSUE_TYPES).toContain(offered);
      }
    }
  });
});
