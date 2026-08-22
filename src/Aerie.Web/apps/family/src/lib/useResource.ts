import { useCallback, useEffect, useRef, useState } from 'react';
import { HttpError } from './http';

/*
  The shell's data-loading plumbing: one hook for reads, one for writes, and the
  one place that turns a thrown anything into a sentence a person can read.

  It lived in modules/storage/ while Storage was the only module. Gather is the
  second one that wants it, which per App.css is exactly when a thing moves up
  here.
*/

/**
 * A fetch failure is not an Error with a useful message: offline, or off the
 * tailnet, both arrive as `TypeError: Failed to fetch`. That's the single most
 * likely failure in a garage, so it gets words instead of jargon.
 */
export function errorMessage(err: unknown): string {
  if (err instanceof HttpError) return err.message;
  if (err instanceof TypeError) return "Couldn't reach Aerie — check you're on the network.";
  return err instanceof Error ? err.message : String(err);
}

export interface Resource<T> {
  data: T | null;
  error: string | null;
  status: number | null;
  loading: boolean;
  /**
   * True when a background refresh failed but there is still data on screen.
   * The list is out of date, not gone - which is worth saying quietly and is
   * not worth replacing the screen over.
   */
  stale: boolean;
  /** Re-runs the loader in the foreground - what a write calls once the server has the change. */
  reload: () => void;
  /**
   * Re-runs the loader in the background: no spinner, and a failure leaves the
   * data alone and sets `stale` instead. This is the poll path - a list that
   * blanks itself every time a phone walks past a thick wall is worse than a
   * list that is ten seconds old.
   */
  refresh: () => void;
  /** Drops in a value the caller already has, so a write doesn't cost a round trip. */
  set: (value: T) => void;
}

interface Run {
  n: number;
  background: boolean;
}

/**
 * Loads once per `key` change, and aborts in flight on unmount. The abort
 * matters more than usual here: tapping a crate and immediately tapping back is
 * a normal gesture on a phone, and an unaborted response lands on a component
 * that no longer exists.
 *
 * `key` rather than a dependency array because the loader is a fresh closure
 * every render - it is never a sound dependency. The key is the caller saying
 * what identifies this load ("crate:D2QDYM"), which is the thing that should
 * actually re-run it.
 */
export function useResource<T>(key: string, load: (signal: AbortSignal) => Promise<T>): Resource<T> {
  const [state, setState] = useState<Omit<Resource<T>, 'reload' | 'refresh' | 'set'>>({
    data: null,
    error: null,
    status: null,
    loading: true,
    stale: false,
  });
  const [run, setRun] = useState<Run>({ n: 0, background: false });

  // Always call the newest closure, without the effect restarting because a
  // re-render produced a new one.
  const latest = useRef(load);
  latest.current = load;

  // A new key is a different resource, never a background refresh of this one -
  // whatever is on screen belongs to the old key and has to go.
  const loadedKey = useRef<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    const background = run.background && loadedKey.current === key;
    loadedKey.current = key;

    if (!background) setState((prev) => ({ ...prev, loading: true }));

    latest.current(controller.signal).then(
      (data) => {
        if (!controller.signal.aborted) setState({ data, error: null, status: null, loading: false, stale: false });
      },
      (err: unknown) => {
        // An abort is this component going away, not a failure to report.
        if (controller.signal.aborted) return;

        setState((prev) => {
          // A background failure with something already drawn keeps it, and says
          // so quietly. With nothing drawn there is no "stale" to offer, so it
          // reports like any other failed read.
          if (background && prev.data !== null) return { ...prev, loading: false, stale: true };

          return {
            data: null,
            error: errorMessage(err),
            status: err instanceof HttpError ? err.status : null,
            loading: false,
            stale: false,
          };
        });
      },
    );

    return () => controller.abort();
  }, [key, run]);

  return {
    ...state,
    reload: useCallback(() => setRun((prev) => ({ n: prev.n + 1, background: false })), []),
    refresh: useCallback(() => setRun((prev) => ({ n: prev.n + 1, background: true })), []),
    set: useCallback(
      (value: T) => setState({ data: value, error: null, status: null, loading: false, stale: false }),
      [],
    ),
  };
}

/**
 * `value`, but only once it has stopped changing for `delayMs`. Typing is what
 * this exists for: search runs on the server now, and one request per character
 * thumbed into the box would be a dozen queries to answer one question. The
 * input itself never waits - only the query does.
 */
export function useDebounced<T>(value: T, delayMs: number): T {
  const [settled, setSettled] = useState(value);

  useEffect(() => {
    const timer = setTimeout(() => setSettled(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);

  return settled;
}

export interface Mutation {
  busy: boolean;
  error: string | null;
  /** Runs a write, reporting failure rather than throwing. Resolves true if it succeeded. */
  run: (operation: () => Promise<unknown>) => Promise<boolean>;
  clear: () => void;
}

/**
 * The write counterpart. Every screen here needs the same three things around a
 * save - a disabled button, a message when it fails, and the failure *not*
 * being an unhandled rejection that only shows up in the log stream.
 */
export function useMutation(): Mutation {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const run = useCallback(async (operation: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await operation();
      return true;
    } catch (err) {
      setError(errorMessage(err));
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  return { busy, error, run, clear: useCallback(() => setError(null), []) };
}
