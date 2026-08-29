/**
 * How loud the wall is about its own failures, over time.
 *
 * The dashboard fails quietly today: `App` sets an error state and renders it
 * only while `data` is null, so every failure after the first successful
 * snapshot changes nothing on screen. Over the nine days sampled in
 * docs/kiosk-architecture.md that hid fourteen real 500s, 502s and
 * network errors from anyone standing in front of the tablet.
 *
 * A dot that is simply on-or-off is the obvious fix and the wrong one. The API
 * flaps, so a binary dot blinks; and it forgets, so a problem that was real
 * five minutes ago leaves no trace by the time someone walks past. This decays
 * instead: full strength on a fault, fading over consecutive clean polls, gone
 * after nine of them. Nine minutes at the dashboard's 60s poll - long enough to
 * still be there when someone comes back to look, short enough that a wall in
 * good health is a wall with nothing on it.
 *
 * Kept as a plain factory rather than the body of a hook for the same reason
 * kioskLifecycle.ts is: what it does is a state machine, "the dot really did go
 * away" has to be asserted rather than eyeballed, and a state machine inside a
 * useEffect closure can only be tested through a renderer.
 */

/** Percent opacity of the dot. 0 means it is not rendered at all. */
export type HealthLevel = 0 | 30 | 60 | 100;

/**
 * Consecutive clean dashboard polls at which the dot steps down. Read as
 * boundaries, not durations: the ladder is only meaningful against the 60s poll
 * in App.tsx, and changing that interval changes what these mean.
 */
export const DECAY_STEPS = [3, 6, 9] as const;

export function levelForCleanPolls(cleanPolls: number): HealthLevel {
  if (cleanPolls >= DECAY_STEPS[2]) return 0;
  if (cleanPolls >= DECAY_STEPS[1]) return 30;
  if (cleanPolls >= DECAY_STEPS[0]) return 60;
  return 100;
}

export interface HealthSnapshot {
  level: HealthLevel;
  /** Consecutive successful dashboard polls since the last fault. */
  cleanPolls: number;
}

export interface HealthSignal {
  /**
   * A fault worth showing. Errors only - warnings are in the buffer and the
   * modal, but they do not raise the dot. "Motion event stream dropped;
   * EventSource will retry" fired 140 times in the sample window and recovers
   * on its own every time; a dot that is up for those is a dot nobody reads.
   */
  noteError(): void;
  /** One successful dashboard snapshot. The only thing that makes the dot fade. */
  notePollSuccess(): void;
  snapshot(): HealthSnapshot;
}

export function createHealthSignal(onChange: (snapshot: HealthSnapshot) => void): HealthSignal {
  let level: HealthLevel = 0;
  let cleanPolls = 0;
  // Distinct from `level > 0`: it stays true through the whole decay, and is
  // what stops a healthy wall's endless successful polls from counting upward
  // forever and re-entering the ladder from the top on the next fault.
  let faulted = false;

  const settle = (next: HealthLevel) => {
    if (next === level) return;
    level = next;
    onChange({ level, cleanPolls });
  };

  return {
    noteError() {
      cleanPolls = 0;
      faulted = true;
      // Always notify, even at an unchanged 100: the modal's contents moved,
      // and a fault arriving during a decay has to restart it.
      if (level === 100) onChange({ level, cleanPolls });
      else settle(100);
    },

    notePollSuccess() {
      if (!faulted) return;
      cleanPolls += 1;
      const next = levelForCleanPolls(cleanPolls);
      if (next === 0) faulted = false;
      settle(next);
    },

    snapshot() {
      return { level, cleanPolls };
    },
  };
}
