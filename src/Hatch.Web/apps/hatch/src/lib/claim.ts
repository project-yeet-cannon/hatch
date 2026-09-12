/* What a claim says out loud, and the one judgement a client is allowed to make
   about one, apart from the components that draw it - the way utilization.ts is
   apart from the battery.

   The one judgement is *quiet*, not *expired*. An expired claim never reaches a
   client at all: IssueClaims.Project answers null past the cutoff, on the board
   read and on the issue read alike, so nothing here compares a heartbeat
   against a lease to decide whether there is a claim. What is left is the
   narrower question of whether a claim that exists is being kept up - a runner
   that has stopped answering still holds its lease for a while, and the board
   is worth more if it says so before the lease runs out rather than after.

   Pure functions taking `now` rather than reading it, so a board judges every
   card against one instant and the tests are exact. */

import type { IssueClaim } from '../types';

/** Fresh, or gone quiet: the two states a claim a client can see is in. There
    is no 'expired' - an expired claim arrives as null. */
export type ClaimHealth = 'fresh' | 'quiet';

/** How much of the lease may pass without a word before the claim wants looking
    at. Half, because the runner heartbeats at a fifth of the TTL: one missed
    beat is a slow request, and two and a half is a runner that has stopped
    answering - with half a lease still left to notice it in.

    A fraction rather than a count of minutes so that a changed
    Hatch:ClaimTtlSeconds moves the warning with it instead of silently making
    it meaningless. */
export const QUIET_FRACTION = 0.5;

/**
 * Whether the holder is being heard from, or has gone quiet on a lease it still
 * holds.
 *
 * Exactly at the threshold is fresh, one second past it is quiet - the same
 * edge the server draws at its own cutoff, so the two never disagree about
 * which side of a boundary an instant is on. A heartbeat in the future (a
 * runner's clock a few seconds ahead of this one) is fresh rather than
 * enormously stale.
 */
export function claimHealth(claim: IssueClaim, now: Date): ClaimHealth {
  const beat = Date.parse(claim.heartbeatAt);
  // An unparseable heartbeat is the one case where the honest answer is "look
  // at this": it is a claim whose age cannot be told, not a claim that is fine.
  if (Number.isNaN(beat)) return 'quiet';

  const silence = now.getTime() - beat;
  return silence > claim.ttlSeconds * 1000 * QUIET_FRACTION ? 'quiet' : 'fresh';
}

/**
 * How long ago, in the words a person reads on a card: `just now`, `40 seconds
 * ago`, `4 minutes ago`, `2 hours ago`, `3 days ago`.
 *
 * Seconds are the resolution that matters at the bottom - a claim is held for
 * minutes and a heartbeat lands every fifth of a lease, so a claim taken
 * moments ago and one taken a minute ago are different facts. Hours and days
 * are reachable even though a live claim's heartbeat never is: `claimedAt` on a
 * runner three hours into an increment, and `chatterAt` on one that has
 * heartbeated all morning without saying anything new.
 *
 * A future instant - clock skew between a runner's box and this one - reads
 * `just now` rather than as a negative.
 *
 * lib/utilization.ts has an agePhrase that is deliberately not reused: it has
 * the word `read` baked into every branch and stops at minutes.
 */
export function agoPhrase(at: string | null | undefined, now: Date): string {
  if (!at) return 'never';

  const when = Date.parse(at);
  if (Number.isNaN(when)) return 'never';

  const seconds = Math.floor(Math.max(0, now.getTime() - when) / 1000);
  if (seconds < 10) return 'just now';
  if (seconds < 60) return `${seconds} seconds ago`;

  const minutes = Math.floor(seconds / 60);
  if (minutes === 1) return '1 minute ago';
  if (minutes < 60) return `${minutes} minutes ago`;

  const hours = Math.floor(minutes / 60);
  if (hours === 1) return '1 hour ago';
  if (hours < 24) return `${hours} hours ago`;

  const days = Math.floor(hours / 24);
  return days === 1 ? '1 day ago' : `${days} days ago`;
}

/**
 * The whole claim in one sentence: `hatch is working this from
 * somewhere:/checkouts/one, last heard from 4 minutes ago`.
 *
 * The card's tooltip, and the line the clear dialog opens with. Every fact the
 * card cannot draw at the size of a dot, said where a hover can reach it.
 *
 * IssueClaims.Sentence on the server says nearly the same thing and is
 * deliberately not plumbed here: it is written for the 409 a runner reads at a
 * terminal, and it rides no read a browser makes.
 */
export function claimTitle(claim: IssueClaim, now: Date): string {
  return `${claim.claimedBy} is working this from ${claim.runner}, last heard from ${agoPhrase(claim.heartbeatAt, now)}`;
}
