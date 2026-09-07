/* The work log's arithmetic and every sentence it says, apart from the
   component the way utilization.ts is apart from the battery.

   The hatch app has no DOM test setup and this story does not add one. The
   house pattern instead is that everything worth being wrong about lives here
   and is tested here, and the component is left thin enough to read.

   Three of these are only interesting at their edges, which is where the bugs
   are:

   - Tokens are the headline and dollars are secondary, because on a
     subscription the dollars are notional list price rather than money that
     left an account. But a session that cost a third of a cent must not read as
     free, so the money has enough places to say so.
   - "Undescribed" and "errored" are two different marks and a row can carry
     both. One predicate answers which, so the component never grows two
     conditions that can disagree.
   - Whether there is a work log at all is a question about the subtree, not
     about the entries: an epic whose stories cost money has a log worth drawing
     with no session of its own. */

import type { WorkLog, WorkLogEntry } from '../types';

/**
 * The headline figure, short enough to sit in a row: `1.4M`, `912k`, `840`.
 *
 * One decimal place in the millions and none below, because the difference
 * between 1.4M and 1.5M is a decision and the difference between 912k and 913k
 * is not.
 */
export function compactTokens(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '0';

  if (n >= 1_000_000) {
    const millions = Math.round(n / 100_000) / 10;
    return `${millions}M`;
  }

  if (n >= 1_000) return `${Math.round(n / 1_000)}k`;

  return `${Math.round(n)}`;
}

/**
 * The secondary figure: `$3.41`, `$0.0012`, `$0`.
 *
 * Four places under a cent rather than two, so a cheap session does not read as
 * free - a night of them adds up, and a column of `$0.00` would say the
 * opposite. Notional list price throughout; nothing here claims money moved.
 */
export function moneyPhrase(usd: number): string {
  if (!Number.isFinite(usd) || usd <= 0) return '$0';

  return usd >= 0.01 ? `$${usd.toFixed(2)}` : `$${usd.toFixed(4)}`;
}

/**
 * How long it ran: `14m02s`, `1h02m`, `4s`.
 *
 * The minutes-and-seconds shape matches the `clock` the terminal prints at the
 * end of a run, so the line somebody watched and the row they read afterwards
 * are the same number said the same way.
 */
export function durationPhrase(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '0s';

  const seconds = Math.floor(ms / 1_000);
  if (seconds < 60) return `${seconds}s`;

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m${pad(seconds % 60)}s`;

  return `${Math.floor(minutes / 60)}h${pad(minutes % 60)}m`;
}

const pad = (n: number): string => (n < 10 ? `0${n}` : `${n}`);

/**
 * The one string that gets copied, built here rather than assembled in JSX, so
 * what is on screen and what lands on the clipboard cannot drift apart.
 */
export const resumeCommand = (sessionId: string): string => `claude --resume ${sessionId}`;

/** What is wrong with an entry, if anything - one predicate rather than two
    conditions in a component. An errored run is the louder fact of the two, so
    it wins when a row is both. */
export function entryMark(entry: WorkLogEntry): 'error' | 'undescribed' | null {
  if (entry.isError) return 'error';
  return entry.described ? null : 'undescribed';
}

/** What an undescribed entry is called, instead of nothing. */
export const entryTitle = (entry: WorkLogEntry): string =>
  entry.described && entry.title ? entry.title : 'A session that did not say what it did';

/**
 * Whether to draw the section at all.
 *
 * Entries **or** a non-zero subtree total: an epic whose stories cost money has
 * a work log worth drawing even with no session of its own. An issue with
 * neither renders nothing at all rather than an empty section - a heading over
 * a blank space is a page saying "this feature exists and has failed you".
 */
export const showsWorkLog = (log: WorkLog | null): log is WorkLog =>
  log !== null && (log.entries.length > 0 || log.totals.sessions > 0);

/**
 * Whether the subtree's total is more than this issue's own, which is the one
 * case where drawing a single number would let one figure pass for the other.
 *
 * Asked of the session count and not of the tokens: an epic with a session of
 * its own and a story with a session of its own are two rows on two pages, and
 * that is true however cheap either was.
 */
export const hasDescendantSpend = (log: WorkLog): boolean => log.totals.sessions > log.own.sessions;

/** What the totals line says: `3 sessions · 1.4M tokens · $3.41`. Singular at
    one, because "1 sessions" is the kind of thing that makes a page look
    unfinished. */
export const totalsPhrase = (log: WorkLog): string =>
  [
    `${log.totals.sessions} ${log.totals.sessions === 1 ? 'session' : 'sessions'}`,
    `${compactTokens(log.totals.totalTokens)} tokens`,
    moneyPhrase(log.totals.costUsd),
  ].join(' · ');

/** The same for what this issue spent on its own, said only when it differs -
    see `hasDescendantSpend`. */
export const ownPhrase = (log: WorkLog): string =>
  log.own.sessions === 0
    ? 'none of it on this issue itself'
    : `${compactTokens(log.own.totalTokens)} of it on this issue itself`;

/** How many of the sessions in the total ended badly, or null when none did.
    Null rather than "0 errors": a log with nothing wrong should say nothing. */
export const errorPhrase = (log: WorkLog): string | null =>
  log.totals.errors === 0
    ? null
    : `${log.totals.errors} ${log.totals.errors === 1 ? 'session' : 'sessions'} ended with an error`;
