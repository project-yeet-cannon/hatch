import { useCallback, useEffect, useState } from 'react';
import { message } from './errors';

/**
 * Data from the API, reloaded after every action and whenever the tab is
 * focused again.
 *
 * Focus is the whole live-update story in the MVP, on purpose: Hatch has no
 * websocket and does not need one. The board is edited by one person and one
 * agent, and the moment the operator looks back at the tab is the moment a
 * stale board would be noticed - so that is where the refetch goes.
 *
 * `load` has to be stable across renders (a module-level function, or one
 * wrapped in useCallback) or this reloads on every render.
 */
export function useLoaded<T>(load: () => Promise<T>) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reload = useCallback(async () => {
    try {
      setData(await load());
      setError(null);
    } catch (err) {
      setError(message(err));
    }
  }, [load]);

  useEffect(() => {
    void reload();

    const onFocus = () => void reload();
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [reload]);

  return { data, setData, error, setError, reload };
}
