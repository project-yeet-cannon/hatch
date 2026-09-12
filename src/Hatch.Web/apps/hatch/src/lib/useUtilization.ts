import { useCallback, useEffect, useState } from 'react';
import { getUtilization } from '../api/client';
import type { Utilization } from '../types';

/** How often the page re-asks. Half the server's five-minute window, so what
    the nav shows is never older than the window itself. */
export const POLL_MS = 2 * 60 * 1000;

export interface UtilizationState {
  /** Null both before the first answer and for the 204. The two are the same
      thing to draw - nothing at all - so nothing here tells them apart. */
  reading: Utilization | null;
  /** Asks the server to go and look now, bypassing its freshness window. */
  refresh: () => Promise<void>;
}

/**
 * The one piece of state behind the battery and its modal.
 *
 * Owned by the nav and passed down, not fetched twice: the modal renders the
 * reading the nav is already holding, so opening and closing it repeatedly
 * issues no request at all, and a refresh from inside it updates both. Two
 * hooks would be two readings that can disagree, which on a number about
 * headroom is the failure worth designing out.
 *
 * Three things move it:
 *
 * - The first load.
 * - A two-minute poll. The server holds a reading for five, so this is what
 *   makes "no older than five minutes" true without anybody pressing anything;
 *   most of these polls are answered from the server's own cache.
 * - `visibilitychange`. A backgrounded tab's timers are throttled to the point
 *   of stopping, so a tab brought forward after an hour would otherwise show an
 *   hour-old number until the next tick. Focus is not enough on its own: a tab
 *   revealed without being clicked is never focused.
 *
 * Nothing here reports an error. A failure to reach Hatch's own endpoint leaves
 * the last reading in place - and the endpoint's own degraded answers (`stale`,
 * `unknown`) are readings, not errors. A nav strip is not where a fetch failure
 * gets announced.
 */
export function useUtilization(): UtilizationState {
  const [reading, setReading] = useState<Utilization | null>(null);

  const load = useCallback(async (refresh: boolean) => {
    try {
      setReading(await getUtilization(refresh));
    } catch {
      // Deliberately silent - see the note above.
    }
  }, []);

  const refresh = useCallback(() => load(true), [load]);

  useEffect(() => {
    void load(false);

    const timer = setInterval(() => void load(false), POLL_MS);
    const onVisible = () => {
      if (document.visibilityState === 'visible') void load(false);
    };
    document.addEventListener('visibilitychange', onVisible);

    return () => {
      clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [load]);

  return { reading, refresh };
}
