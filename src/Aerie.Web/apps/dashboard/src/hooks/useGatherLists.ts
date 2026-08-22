import { useCallback, useEffect, useState } from 'react';
import { getGatherSource } from '../dataSource';
import { clientLogger } from '../lib/clientLogger';
import type { GatherListSummary } from '../types';

/** The dashboard's own cadence - a shopping list on a wall is not a live feed. */
const POLL_INTERVAL_MS = 60_000;

/**
 * The Gather lists behind the wall tile, polled alongside the rest of the
 * dashboard. A failed load keeps the last known lists rather than blanking the
 * tile: the counts being a minute stale is a smaller lie on a kitchen wall than
 * the list disappearing because one poll missed.
 *
 * `refresh` exists for the moment the overlay closes, when the counts on the
 * tile are known to be wrong and waiting out the poll would show someone the
 * opposite of what they just did.
 */
export function useGatherLists(): { lists: GatherListSummary[]; refresh: () => void } {
  const [lists, setLists] = useState<GatherListSummary[]>([]);
  const [source] = useState(getGatherSource);
  const [reloadToken, setReloadToken] = useState(0);

  useEffect(() => {
    let cancelled = false;

    const load = () => {
      source
        .getLists()
        .then((next) => {
          if (!cancelled) setLists(next);
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          clientLogger.error('Gather lists load failed', {
            reason: err instanceof Error ? err.message : String(err),
          });
        });
    };

    load();
    const poll = setInterval(load, POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(poll);
    };
  }, [source, reloadToken]);

  const refresh = useCallback(() => setReloadToken((token) => token + 1), []);

  return { lists, refresh };
}
