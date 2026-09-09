import { useCallback, useEffect, useState } from 'react';
import { getRunners } from '../api/client';
import type { Runner } from '../types';

/** How often the page re-asks. Shorter than the nav battery's two minutes
    because this is a control surface somebody is watching while they press
    something: a Pause that took two minutes to show as paused would read as a
    button that did nothing. */
export const POLL_MS = 20 * 1000;

export interface RunnersState {
  /** Null until the first answer arrives; an empty list afterwards is a Hatch
      nothing has spoken to lately, which is a different thing and reads as
      one. */
  runners: Runner[] | null;
  /** What went wrong, or null. Unlike the nav battery this does say so: a page
      whose whole purpose is control must not draw a stale row as though it were
      current. */
  error: string | null;
  /** Ask again now - after a press, so the row shows what was just asked for
      without waiting out the interval. */
  reload: () => Promise<void>;
}

/**
 * The runners, kept current.
 *
 * Three things move it, exactly as `useUtilization` is moved and for the same
 * reasons: the first load, an interval, and `visibilitychange` - a backgrounded
 * tab's timers are throttled to a stop, so a tab brought forward after an hour
 * would otherwise show an hour-old page. Focus is not enough on its own,
 * because a tab revealed without being clicked is never focused.
 *
 * Polling on the board was rejected and is still rejected; this is not that. A
 * kanban card changes when somebody moves it, and the person who moved it is
 * looking at it. A runner changes on its own, all night, which is the whole
 * reason there is a page for one.
 */
export function useRunners(): RunnersState {
  const [runners, setRunners] = useState<Runner[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setRunners(await getRunners());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'the runners could not be read');
    }
  }, []);

  useEffect(() => {
    void load();

    const timer = setInterval(() => void load(), POLL_MS);
    const onVisible = () => {
      if (document.visibilityState === 'visible') void load();
    };
    document.addEventListener('visibilitychange', onVisible);

    return () => {
      clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [load]);

  return { runners, error, reload: load };
}
