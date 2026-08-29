import type { CalendarDay, CalendarEventSummary } from '../types';
import { formatShortTime } from './format';

/**
 * How many event rows the stage's agenda face shows. Four fits the face at
 * every measured wall viewport (docs/plans/dashboard-redesign.md, Phase 0) -
 * the face is a glance, deliberately not a scroller, so the cap is a constant
 * rather than a measurement.
 */
export const AGENDA_GLANCE_MAX = 4;

export interface AgendaGlance {
  /** The rows to render, chronological - backfilled past first, then current/upcoming. */
  events: CalendarEventSummary[];
  /** How many of today's still-relevant events didn't fit. Zero renders nothing. */
  overflowCount: number;
}

/**
 * Picks the agenda face's rows: everything still relevant first - all-day
 * events, whatever is in progress, whatever hasn't started - in the server's
 * stored order, capped at [AGENDA_GLANCE_MAX]. If the day is winding down and
 * fewer remain, the most recent finished events backfill the space at their
 * usual fade, keeping the day's shape without stealing room from what's
 * ahead. Overflow counts only the still-relevant rows that didn't fit; the
 * past is never "more".
 */
export function glanceRows(today: CalendarDay | undefined, now: Date): AgendaGlance {
  const events = today?.events ?? [];
  const nowMs = now.getTime();
  // An all-day event applies to the whole day - it is never "over" while the
  // day is, which is CalendarSection's rule too.
  const isPast = (event: CalendarEventSummary) => !event.isAllDay && Date.parse(event.endsAt) <= nowMs;

  const relevant = events.filter((event) => !isPast(event));
  const past = events.filter(isPast);

  const shown = relevant.slice(0, AGENDA_GLANCE_MAX);
  const backfill = past.slice(Math.max(0, past.length - (AGENDA_GLANCE_MAX - shown.length)));

  return {
    events: [...backfill, ...shown],
    overflowCount: relevant.length - shown.length,
  };
}

/**
 * The one-line footer that keeps tomorrow from being invisible while today
 * owns the face: "Tomorrow · 3 events · first at 8:30am". The time is
 * formatShortTime's prose form, not the gutter's "8a" shorthand - this is a
 * sentence, not a column. It names the first *timed* event - an all-day event
 * has no hour worth naming - and drops off entirely when tomorrow is only
 * all-day entries. Null when tomorrow is empty or the agenda window doesn't
 * include it: nothing to whisper.
 */
export function tomorrowWhisper(tomorrow: CalendarDay | undefined, timeZone: string): string | null {
  const events = tomorrow?.events ?? [];
  if (events.length === 0) return null;

  const count = `${events.length} ${events.length === 1 ? 'event' : 'events'}`;
  const firstTimed = events.find((event) => !event.isAllDay);
  if (firstTimed === undefined) return `Tomorrow · ${count}`;
  return `Tomorrow · ${count} · first at ${formatShortTime(firstTimed.startsAt, timeZone)}`;
}
