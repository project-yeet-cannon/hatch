import { useCallback, useEffect, useState } from 'react';

/**
 * One request, its three states, and a way to ask again.
 *
 * Every screen here is a read of something that is *changing while you look at
 * it* - a sweep launched a minute ago is filling in - so "ask again" is a
 * first-class part of this rather than a page reload. `reload` is what the
 * refresh control and the poll below both call.
 *
 * The state is one object rather than three, because the three are not
 * independent: a reload that fails must not blank the table that is already on
 * screen, and separate `data`/`error`/`loading` flags is how that happens by
 * accident. Here a failure keeps the last good value and shows the message
 * beside it.
 */
export interface Async<T> {
  data: T | null;
  error: string | null;
  loading: boolean;
  reload: () => void;
}

export function useAsync<T>(load: () => Promise<T>, deps: unknown[]): Async<T> {
  const [state, setState] = useState<{ data: T | null; error: string | null; loading: boolean }>({
    data: null,
    error: null,
    loading: true,
  });
  const [nonce, setNonce] = useState(0);

  // `load` is a fresh closure on every render, so it cannot be the dependency:
  // it would re-run the request on every render forever. The caller passes the
  // values the request actually depends on instead.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const run = useCallback(load, deps);

  useEffect(() => {
    let live = true;
    setState((previous) => ({ ...previous, loading: true }));
    run().then(
      (data) => {
        if (live) setState({ data, error: null, loading: false });
      },
      (failure: unknown) => {
        if (live) {
          setState((previous) => ({
            /* The last good value stays. A reload that failed should leave the
               table you were reading where it is and say what happened. */
            data: previous.data,
            error: failure instanceof Error ? failure.message : String(failure),
            loading: false,
          }));
        }
      },
    );
    return () => {
      live = false;
    };
  }, [run, nonce]);

  return { ...state, reload: useCallback(() => setNonce((value) => value + 1), []) };
}

/**
 * Ask again every `seconds`, while `active`.
 *
 * Used by the screens that show a queue: a cold boot has runs in flight for a
 * minute or two, and a page that needed a manual refresh to notice would make
 * the seeded demo look like it had failed. Stops when there is nothing moving,
 * so a finished leaderboard is not a request every few seconds forever.
 */
export function usePoll(reload: () => void, active: boolean, seconds = 5): void {
  useEffect(() => {
    if (!active) return;
    const timer = window.setInterval(reload, seconds * 1000);
    return () => window.clearInterval(timer);
  }, [reload, active, seconds]);
}
