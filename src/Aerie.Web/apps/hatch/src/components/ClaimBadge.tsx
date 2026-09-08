import { claimHealth, claimTitle } from '../lib/claim';
import type { IssueClaim } from '../types';

/**
 * A dot on a card that something is working right now.
 *
 * A dot rather than a word, for the reason `hatch-card-asking` is a bare `?`:
 * the board is read all day and a ticket is claimed for a few minutes, so the
 * marker is the smallest thing that is still visible, and it costs no width
 * beside an assignee name. Everything it cannot say at that size - who, from
 * where, how long since a word - is on the hover.
 *
 * Nothing at all where there is no claim: not a placeholder and not a dash, the
 * same register as the assignee chip beside it. Almost nothing on the board is
 * claimed at any moment, and a grey dot on every card is noise the eye has to
 * skip past to find the two that are live.
 *
 * There is no expiry here and there must not be one. A claim past its lease
 * arrives as null - the server did that arithmetic once, against one instant
 * per board scan - so this draws whatever it is handed. What it does judge is
 * narrower: a holder that has gone quiet on a lease it still holds, which is
 * worth seeing before the lease runs out rather than after.
 */
export function ClaimBadge({ claim }: { claim: IssueClaim | null }) {
  if (!claim) return null;

  // Once per render, exactly as MomentChip does. Nothing here ticks: the card
  // goes stale with the board it was drawn from and comes back current on the
  // next read.
  const now = new Date();

  return (
    <span
      className={`hatch-card-claim hatch-card-claim-${claimHealth(claim, now)}`}
      title={claimTitle(claim, now)}
      aria-label={claimTitle(claim, now)}
    />
  );
}
