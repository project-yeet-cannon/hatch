import { describe, expect, it } from 'vitest';
import { grownHeight } from './useAutoGrow';

/* The hook itself is layout - it has to be looked at in a browser, and the
   operator does that. What can be pinned down here is the arithmetic, which is
   where the two ends of the range and the border-box correction live. */
describe('grownHeight', () => {
  it('takes the height the content needs, plus the border', () => {
    expect(grownHeight(100, 400, 2)).toBe(402);
  });

  it('never goes under the height the box opened at', () => {
    expect(grownHeight(402, 90, 2)).toBe(402);
  });

  /* A textarea's scrollHeight is at least its own client height, so a box with
     one line in it measures the floor back and lands exactly on it - which is
     what makes deleting lines shrink to that floor and stop. */
  it('lands on the floor when the content is the floor', () => {
    expect(grownHeight(402, 400, 2)).toBe(402);
  });
});
