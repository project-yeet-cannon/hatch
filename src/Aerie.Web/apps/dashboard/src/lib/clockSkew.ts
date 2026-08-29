/**
 * Whether the tablet's own clock can still be trusted.
 *
 * The clock is the floor everything else in
 * docs/kiosk-architecture.md stands on - it is what the boot fallback
 * screen shows, what the native error screen shows, and what the header shows -
 * so a tablet whose clock has drifted is a wall that lies confidently in
 * exactly the situations the rest of this work made safe. A device that has
 * been offline long enough can drift, and nothing was checking.
 *
 * Deliberately does *not* correct the displayed time. A wall that silently
 * disagrees with the phone in your hand is a bug worth seeing, and quietly
 * papering over it would mean the one visible symptom of a broken clock is the
 * one thing removed.
 *
 * Measured against the snapshot's `generatedAt` rather than a `Date` header:
 * it is a server timestamp already in hand, so it costs nothing, and network
 * latency inflates it by well under a second - immaterial against a threshold
 * measured in minutes.
 */

/**
 * How far apart the two clocks have to be before it is worth saying. Two
 * minutes is past any plausible sum of request latency, snapshot age and
 * rounding, and short of it there is nothing anyone would act on.
 */
export const SKEW_REPORT_THRESHOLD_MS = 2 * 60_000;

/**
 * How much the skew has to move before it is worth saying *again*. Without
 * this, a tablet an hour out would report on every 60s poll forever and fill
 * the 64-slot buffer with one fact.
 */
export const SKEW_RESTATE_DELTA_MS = 60_000;

export interface SkewWatcher {
  /**
   * @returns the skew in ms when it is worth reporting, null otherwise.
   * Positive means the device clock is ahead of the server's.
   */
  note(deviceNowMs: number, serverNowIso: string): number | null;
}

export function createSkewWatcher(): SkewWatcher {
  let lastReported: number | null = null;

  return {
    note(deviceNowMs, serverNowIso) {
      const serverNowMs = Date.parse(serverNowIso);
      if (Number.isNaN(serverNowMs)) return null;

      const skewMs = deviceNowMs - serverNowMs;
      if (Math.abs(skewMs) < SKEW_REPORT_THRESHOLD_MS) {
        // Back in agreement. Forgetting here is what lets a clock that drifts
        // out again be reported again.
        lastReported = null;
        return null;
      }

      if (lastReported !== null && Math.abs(skewMs - lastReported) < SKEW_RESTATE_DELTA_MS) return null;

      lastReported = skewMs;
      return skewMs;
    },
  };
}
