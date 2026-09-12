/* The battery's arithmetic and its wording, apart from the component the way
   meter.ts is apart from StatusMeter.

   The hatch app has no DOM test setup and this story does not add one. The
   house pattern instead is that everything worth being wrong about lives here
   and is tested here, and the component is left thin enough to read: every
   sentence the battery says out loud, the fraction its ring is drawn from, and
   the one predicate deciding whether there is a battery at all.

   Two of them are only interesting at their edges, which is where the bugs are:

   - The ring runs down a five-hour window, and a row can arrive with no reset
     instant at all. Unknown is a third answer, not a zero - a ring drawn empty
     says "no time left", which is the opposite of "we do not know".
   - A reading can be older than the poll that fetched it. `state` is the whole
     of that story and `readAt` is how old, so the wording never guesses at
     staleness from a timestamp. */

import type { Utilization, UtilizationLimit } from '../types';

/** The window the ring runs down, in milliseconds. The session limit's own,
    which the account resets on. */
export const SESSION_WINDOW_MS = 5 * 60 * 60 * 1000;

/**
 * The share of the five-hour window still to run, as 0..1, or null when there
 * is nothing to count to.
 *
 * Clamped at both ends rather than trusted: a reset instant that has just
 * passed is 0 rather than a negative arc, and one further out than the window
 * (an account that reset while the tab was asleep, or a clock a few seconds
 * apart from the server's) is a full ring rather than an arc that wraps.
 */
export function ringFraction(resetsAt: string | null | undefined, now: Date): number | null {
  if (!resetsAt) return null;

  const at = Date.parse(resetsAt);
  if (Number.isNaN(at)) return null;

  return Math.min(1, Math.max(0, (at - now.getTime()) / SESSION_WINDOW_MS));
}

/**
 * When the window comes back, in plain words: `resets in 1h 20m`, `resets in
 * under a minute`, `reset time unknown`.
 *
 * This is the battery's accessible name and its tooltip, so it is a sentence
 * rather than a duration. "Under a minute" rather than "0m" for the same
 * reason: a battery that says `resets in 0m` for fifty-nine seconds reads as
 * broken.
 */
export function resetPhrase(resetsAt: string | null | undefined, now: Date): string {
  if (!resetsAt) return 'reset time unknown';

  const at = Date.parse(resetsAt);
  if (Number.isNaN(at)) return 'reset time unknown';

  const left = at - now.getTime();
  if (left <= 0) return 'resets in under a minute';

  const minutes = Math.floor(left / 60_000);
  if (minutes < 1) return 'resets in under a minute';

  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;

  if (hours === 0) return `resets in ${minutes}m`;
  return rest === 0 ? `resets in ${hours}h` : `resets in ${hours}h ${rest}m`;
}

/**
 * How old the reading is: `read just now`, `read 4 minutes ago`, `read 2 hours
 * ago`.
 *
 * Said in the modal rather than in the nav, because it only matters once
 * somebody is asking why a number looks wrong - and it is the whole of what a
 * `stale` answer is for.
 */
export function agePhrase(readAt: string | null | undefined, now: Date): string {
  if (!readAt) return 'never read';

  const at = Date.parse(readAt);
  if (Number.isNaN(at)) return 'never read';

  const minutes = Math.floor(Math.max(0, now.getTime() - at) / 60_000);
  if (minutes < 1) return 'read just now';
  if (minutes === 1) return 'read 1 minute ago';
  if (minutes < 60) return `read ${minutes} minutes ago`;

  const hours = Math.floor(minutes / 60);
  return hours === 1 ? 'read 1 hour ago' : `read ${hours} hours ago`;
}

/**
 * The row the glyph in the nav is drawn from - the five-hour session window.
 *
 * Found by `window` rather than by position: the account decides the order and
 * this does not depend on it. Null when the reading carries no session row at
 * all, which is what `state: "unknown"` looks like.
 */
export const sessionLimit = (reading: Utilization | null): UtilizationLimit | null =>
  reading?.limits.find((limit) => limit.window === 'session') ?? null;

/** The three tones the server can send. Anything else paints as normal rather
    than as nothing, so an unexpected word cannot produce an unstyled glyph. */
const TONES = new Set(['normal', 'warn', 'danger']);

/** The class the server's `tone` becomes. One place, so a tone reaching CSS is
    always a class that exists. */
export const toneClass = (tone: string | null | undefined): string =>
  `hatch-battery-${tone && TONES.has(tone) ? tone : 'normal'}`;

/**
 * Whether there is a battery on this installation at all.
 *
 * Null is the 204 - no token configured - and it is the common case: no
 * element, no placeholder, no reserved space, and nothing anywhere saying so.
 * Everything else, `unknown` included, draws: a nav that has lost contact
 * should say it has lost contact rather than quietly losing an element.
 */
export const hasBattery = (reading: Utilization | null): reading is Utilization => reading !== null;

/**
 * The number beside the glyph. An em dash when there is no reading behind it,
 * which is what `state: "unknown"` reads as - a battery that cannot say how
 * full it is must not say "0".
 */
export const percentLabel = (limit: UtilizationLimit | null): string =>
  limit === null ? '—' : `${Math.round(limit.percent)}%`;

/**
 * The whole sentence the battery is announced as, and its tooltip.
 *
 * The state comes first when it is not `ok`, because "this number is old" is
 * the thing somebody reading a stale battery most needs to know and a screen
 * reader reads in order.
 */
export function batteryLabel(reading: Utilization | null, now: Date): string {
  const limit = sessionLimit(reading);

  if (reading === null || reading.state === 'unknown' || limit === null) {
    return 'Claude usage unknown — the account could not be reached';
  }

  const headline = `Claude session usage ${Math.round(limit.percent)}%, ${resetPhrase(limit.resetsAt, now)}`;
  return reading.state === 'stale' ? `${headline} (${agePhrase(reading.readAt, now)})` : headline;
}
