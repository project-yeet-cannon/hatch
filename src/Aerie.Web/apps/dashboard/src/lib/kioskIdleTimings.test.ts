import { describe, expect, it } from 'vitest';
import { IDLE_DIM_AFTER_MS, IDLE_TIMEOUT_MS } from './kioskIdleTimings';

// The ladder's ordering is the whole contract - the values themselves are
// taste, and one of them is enforced from Kotlin where this test cannot reach.
// What can be pinned here is that the rungs stay in order, so a later edit to
// one number can't quietly invert the escalation.
describe('idle ladder', () => {
  it('escalates: reset, then dim', () => {
    expect(IDLE_TIMEOUT_MS).toBeLessThan(IDLE_DIM_AFTER_MS);
  });
});
