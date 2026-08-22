import { useEffect, useRef } from 'react';

/**
 * Calls `onTick` on an interval while the page is visible and the device thinks
 * it has a network.
 *
 * Polling is how two devices converge here - the kitchen and the aisle - and
 * per docs/plans/gather.md it is deliberately not a realtime transport. Which
 * makes *when not to poll* the interesting half:
 *
 * - Hidden pages don't tick. A phone in a pocket asking every ten seconds is
 *   battery spent on an answer nobody is looking at.
 * - Coming back visible ticks immediately, rather than waiting out the rest of
 *   the interval. Pulling the phone out to look at the list is exactly the
 *   moment the list should be right.
 * - Offline doesn't tick. navigator.onLine is a weak signal (see useOnline),
 *   but when it says false it is right, and the service worker would only serve
 *   the cached copy back anyway.
 */
export function usePolling(intervalMs: number, onTick: () => void, enabled = true): void {
  // The caller's closure is fresh every render; restarting the timer on each one
  // would mean it never fires. Same trick as useResource.
  const latest = useRef(onTick);
  latest.current = onTick;

  useEffect(() => {
    if (!enabled) return;

    let timer: number | undefined;

    const stop = () => {
      if (timer !== undefined) {
        clearInterval(timer);
        timer = undefined;
      }
    };

    const start = () => {
      stop();
      timer = window.setInterval(() => {
        if (navigator.onLine) latest.current();
      }, intervalMs);
    };

    const sync = () => {
      if (document.hidden) {
        stop();
        return;
      }
      // Visible again: answer now, then resume the cadence from here.
      if (navigator.onLine) latest.current();
      start();
    };

    if (!document.hidden) start();
    document.addEventListener('visibilitychange', sync);
    window.addEventListener('online', sync);

    return () => {
      stop();
      document.removeEventListener('visibilitychange', sync);
      window.removeEventListener('online', sync);
    };
  }, [intervalMs, enabled]);
}

/**
 * Runs `onVisible` each time the page becomes visible again, without an
 * interval behind it. For screens whose numbers are glanceable rather than
 * live - the lists index, where the counts only need to be right at the moment
 * someone looks at them.
 */
export function useRefreshOnVisible(onVisible: () => void, enabled = true): void {
  const latest = useRef(onVisible);
  latest.current = onVisible;

  useEffect(() => {
    if (!enabled) return;

    const sync = () => {
      if (!document.hidden && navigator.onLine) latest.current();
    };

    document.addEventListener('visibilitychange', sync);
    window.addEventListener('online', sync);
    return () => {
      document.removeEventListener('visibilitychange', sync);
      window.removeEventListener('online', sync);
    };
  }, [enabled]);
}
