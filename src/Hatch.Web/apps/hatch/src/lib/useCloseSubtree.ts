/* What happens after the operator answers the offer to close a subtree.

   Two screens ask this question - the board on a drop, the issue page on its
   status bar - and one copy of the answer in each is two error paths that
   drift. So the state the dialog needs and the request it sends live here, and
   a page is left with `const closing = useCloseSubtree(reload)` and six props.

   The cascade names its keys. `closeOffer` computed them from the board the
   operator was looking at, and they are sent as a list rather than as a
   `cascade: true` flag the server would expand at write time - the rule
   IssueBulkEditRequest.Keys states in Dtos.cs: a request that re-ran the query
   server-side could act on a row filed between the operator reading the list
   and pressing the button. */

import { useCallback, useState } from 'react';
import { bulkEditIssues } from '../api/client';
import { summarize } from './bulk';
import { message } from './errors';
import type { CloseOffer } from './closeSubtree';
import type { IssueBulkFailure } from '../types';

export interface CloseSubtree {
  /** The question on screen, or null when none is being asked. */
  offer: CloseOffer | null;
  /** Called with what `closeOffer` returned, once the parent's move has come back. */
  ask: (offer: CloseOffer | null) => void;
  close: () => void;
  confirm: () => void;
  busy: boolean;
  /** What went wrong, as a sentence. Null while nothing has. */
  error: string | null;
  failures: IssueBulkFailure[];
}

/**
 * @param reload How the screen behind the dialog catches up with what the
 * cascade did - the board's `reload`, or the issue page's `load`.
 */
export function useCloseSubtree(reload: () => Promise<void>): CloseSubtree {
  const [offer, setOffer] = useState<CloseOffer | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [failures, setFailures] = useState<IssueBulkFailure[]>([]);

  // Called with null asks nothing, so a call site never has to guard: the whole
  // of "should this be asked at all" is closeOffer's.
  const ask = useCallback((next: CloseOffer | null) => {
    setError(null);
    setFailures([]);
    setOffer(next);
  }, []);

  const close = useCallback(() => {
    setOffer(null);
    setBusy(false);
    setError(null);
    setFailures([]);
  }, []);

  const confirm = useCallback(() => {
    if (!offer || busy) return;

    setBusy(true);
    setError(null);
    setFailures([]);

    void (async () => {
      try {
        const result = await bulkEditIssues({
          keys: offer.cards.map((card) => card.key),
          statusId: offer.column.id,
        });

        // Some of them moved whatever else happened, so the screen behind the
        // dialog is caught up first either way.
        await reload();

        if (result.failures.length === 0) {
          close();
          return;
        }

        // A partial refusal keeps the dialog open saying what did change and
        // naming each refusal with its reason. Pressing confirm again is safe:
        // the keys that already moved come back `unchanged` and write no
        // second event.
        setError(summarize(result));
        setFailures(result.failures);
      } catch (err) {
        // The whole request refused - an unknown column, more than MaxBulkKeys
        // issues, the server down. BulkEdit is one SaveChanges, so nothing was
        // written and there is nothing to catch up with.
        setError(message(err));
      } finally {
        setBusy(false);
      }
    })();
  }, [offer, busy, reload, close]);

  return { offer, ask, close, confirm, busy, error, failures };
}
