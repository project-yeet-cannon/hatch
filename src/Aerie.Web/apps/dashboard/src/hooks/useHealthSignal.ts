import { useCallback, useEffect, useRef, useState } from 'react';
import { loggedEntries, subscribeToLog, type LoggedEntry } from '../lib/clientLogger';
import { createHealthSignal, type HealthLevel, type HealthSignal } from '../lib/healthSignal';

/**
 * React's half of the health signal: the log buffer in, a level and a list out.
 * The decay itself lives in lib/healthSignal.ts, where it is unit-tested.
 *
 * Nothing in the app has to be changed to feed this. Every failure path already
 * calls clientLogger.error, and clientLogger now keeps a bounded buffer of
 * those and publishes it - so this hook subscribes to a feed that is already
 * complete rather than to a new one that would need maintaining.
 */
export function useHealthSignal(): {
  level: HealthLevel;
  entries: readonly LoggedEntry[];
  /** Call on each successful dashboard snapshot. The only thing that fades the dot. */
  notePollSuccess: () => void;
} {
  const [level, setLevel] = useState<HealthLevel>(0);
  const [entries, setEntries] = useState<readonly LoggedEntry[]>(() => loggedEntries());
  const signalRef = useRef<HealthSignal | null>(null);
  // Entry ids are monotonic, so "has anything new gone wrong" is one comparison
  // rather than a diff of two arrays on every publish.
  const lastErrorIdRef = useRef(0);

  if (signalRef.current === null) {
    signalRef.current = createHealthSignal((snapshot) => setLevel(snapshot.level));
  }

  useEffect(() => {
    const signal = signalRef.current;
    if (!signal) return;

    const consume = (next: readonly LoggedEntry[]) => {
      setEntries(next);
      let newest = lastErrorIdRef.current;
      for (const entry of next) {
        if (entry.level === 'error' && entry.id > newest) newest = entry.id;
      }
      if (newest > lastErrorIdRef.current) {
        lastErrorIdRef.current = newest;
        signal.noteError();
      }
    };

    // Seeded before subscribing, not after: a failure during boot - a bundle
    // asset that 404'd, a first poll that never landed - is recorded before
    // this component exists, and is exactly the failure most worth showing.
    consume(loggedEntries());
    return subscribeToLog(consume);
  }, []);

  const notePollSuccess = useCallback(() => {
    signalRef.current?.notePollSuccess();
  }, []);

  return { level, entries, notePollSuccess };
}
