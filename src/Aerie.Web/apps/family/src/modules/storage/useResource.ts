import { useCallback, useEffect, useRef, useState } from 'react';
import { HttpError } from './api';

/*
  The module's data-loading plumbing: one hook for reads, one for writes, and
  the one place that turns a thrown anything into a sentence a person can read.

  It lives in the module rather than the shell because it's the only module so
  far - per src/App.css, a thing moves up to the shell when a second module
  wants it, not in anticipation of one.
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
  /** Re-runs the loader - what a write calls once the server has the change. */
  reload: () => void;
  /** Drops in a value the caller already has, so a write doesn't cost a round trip. */
  set: (value: T) => void;
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
  const [state, setState] = useState<Omit<Resource<T>, 'reload' | 'set'>>({
    data: null,
    error: null,
    status: null,
    loading: true,
  });
  const [nonce, setNonce] = useState(0);

  // Always call the newest closure, without the effect restarting because a
  // re-render produced a new one.
  const latest = useRef(load);
  latest.current = load;

  useEffect(() => {
    const controller = new AbortController();
    setState((prev) => ({ ...prev, loading: true }));

    latest.current(controller.signal).then(
      (data) => {
        if (!controller.signal.aborted) setState({ data, error: null, status: null, loading: false });
      },
      (err: unknown) => {
        // An abort is this component going away, not a failure to report.
        if (controller.signal.aborted) return;
        setState({
          data: null,
          error: errorMessage(err),
          status: err instanceof HttpError ? err.status : null,
          loading: false,
        });
      },
    );

    return () => controller.abort();
  }, [key, nonce]);

  return {
    ...state,
    reload: useCallback(() => setNonce((n) => n + 1), []),
    set: useCallback((value: T) => setState({ data: value, error: null, status: null, loading: false }), []),
  };
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
