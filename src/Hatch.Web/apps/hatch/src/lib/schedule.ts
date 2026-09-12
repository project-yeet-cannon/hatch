/* An issue's two dates: `readyAt`, the day it becomes workable, and `dueAt`,
   the day it is owed. Both arrive as one string in one of two forms, and which
   form it is changes what the string means:

     "2027-08-15"            a date. No time of day was meant.
     "2027-09-01T17:00:00Z"  an instant. Somebody meant 5pm.

   The distinction is not decoration. A bare date has to be pinned to some
   midnight to be stored, and a reader west of UTC who renders that midnight in
   their own zone draws the day before. So: a moment WITH a time is read in the
   viewer's zone, where 5pm means their 5pm; a moment WITHOUT one is read in
   UTC, where the date components are exactly the ones that were typed.

   IssueMoment.cs holds the other half of this contract. */

/** A parsed date, and whether anybody meant the time of day. */
export interface Moment {
  at: Date;
  hasTime: boolean;
}

/** How close a due date has got. The board's chip colors are one of these each. */
export type Urgency = 'later' | 'soon' | 'today' | 'overdue';

/** How many days ahead a due date starts warning. Three: long enough to act
    on, short enough that an amber card still means something. */
export const SOON_DAYS = 3;

/** Ten characters and no `T` is the date form. */
const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;

export function parseMoment(text: string | null | undefined): Moment | null {
  if (!text) return null;

  const at = new Date(text);
  if (Number.isNaN(at.getTime())) return null;

  return { at, hasTime: !DATE_ONLY.test(text) };
}

/* ---- Days ----

   Urgency is a question about calendar days, not about elapsed hours: a due
   date at 9am tomorrow is "tomorrow" at 11pm tonight, two hours away though it
   is. So both sides are reduced to a day number - days since the epoch - and
   compared as integers, which also sidesteps every question about how long a
   day is when the clocks change. */

const DAY_MS = 86_400_000;

const dayNumber = (year: number, month: number, day: number) => Date.UTC(year, month, day) / DAY_MS;

/** The calendar day a viewer would say this instant falls on. */
const localDay = (at: Date) => dayNumber(at.getFullYear(), at.getMonth(), at.getDate());

/** The calendar day a date-only moment was written as - see the header. */
const utcDay = (at: Date) => dayNumber(at.getUTCFullYear(), at.getUTCMonth(), at.getUTCDate());

/** Whichever of the two is right for this moment. */
export const dayOf = (moment: Moment) => (moment.hasTime ? localDay(moment.at) : utcDay(moment.at));

/** How many days from now until this moment's day. Negative is in the past. */
export const daysUntil = (moment: Moment, now: Date) => dayOf(moment) - localDay(now);

/**
 * Where a due date sits on the ladder.
 *
 * A moment with a time goes overdue on the minute, because 5pm was meant. One
 * without goes overdue when its day is behind us, because nothing was promised
 * about the hour - an issue due "the 12th" is not late at one minute past
 * midnight on the 12th.
 */
export function urgencyOf(moment: Moment, now: Date): Urgency {
  const overdue = moment.hasTime ? moment.at.getTime() < now.getTime() : dayOf(moment) < localDay(now);
  if (overdue) return 'overdue';

  const days = daysUntil(moment, now);
  if (days <= 0) return 'today';
  return days <= SOON_DAYS ? 'soon' : 'later';
}

/**
 * Whether an issue is still waiting for its ready date. The board folds these
 * away so it answers "what can I do" rather than "what exists".
 *
 * Ready at the start of the day it names, whatever hour it was set to. A ticket
 * that becomes workable at 09:00 is one nobody may look at over breakfast, and
 * that is a rule with no purpose.
 */
export function isWaiting(readyAt: string | null | undefined, now: Date): boolean {
  const moment = parseMoment(readyAt);
  return moment !== null && daysUntil(moment, now) > 0;
}

/* ---- Words ---- */

/** `Sep 12`, or `Sep 12, 2027` when the year is not this one. */
function dateWords(moment: Moment, now: Date): string {
  const zone = moment.hasTime ? undefined : 'UTC';
  const year = moment.hasTime ? moment.at.getFullYear() : moment.at.getUTCFullYear();

  return moment.at.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    ...(year === now.getFullYear() ? {} : { year: 'numeric' }),
    timeZone: zone,
  });
}

/** `5:00 PM` in the viewer's zone, for a moment that carries one. */
const timeWords = (moment: Moment) =>
  moment.at.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });

/**
 * What the chip says. Near dates get the word rather than the number - "Today"
 * is read at a glance and "Sep 3" has to be compared against something.
 */
export function momentWords(moment: Moment, now: Date): string {
  const days = daysUntil(moment, now);
  const day =
    days === 0 ? 'Today' : days === 1 ? 'Tomorrow' : days === -1 ? 'Yesterday' : dateWords(moment, now);

  return moment.hasTime ? `${day}, ${timeWords(moment)}` : day;
}

/** The unabbreviated version, for a `title` on hover. */
export function momentTitle(moment: Moment): string {
  return moment.at.toLocaleDateString(undefined, {
    weekday: 'long',
    year: 'numeric',
    month: 'long',
    day: 'numeric',
    timeZone: moment.hasTime ? undefined : 'UTC',
  }) + (moment.hasTime ? ` at ${timeWords(moment)}` : '');
}

/* ---- The pickers ----

   A moment is edited as two native inputs: a date and an optional time. Two
   inputs rather than one <input type="datetime-local"> because that control
   cannot express "a date, no time" at all - it demands both or neither, which
   is the one thing this field has to be able to say. */

export interface MomentInputs {
  date: string;
  time: string;
}

const pad = (n: number) => String(n).padStart(2, '0');

/** Splits a stored moment into what the two inputs should show. */
export function toInputs(text: string | null | undefined): MomentInputs {
  const moment = parseMoment(text);
  if (!moment) return { date: '', time: '' };

  const { at, hasTime } = moment;

  // Local components for an instant, UTC components for a date - the same rule
  // as everywhere else here, applied to editing rather than to drawing.
  return hasTime
    ? {
        date: `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`,
        time: `${pad(at.getHours())}:${pad(at.getMinutes())}`,
      }
    : { date: at.toISOString().slice(0, 10), time: '' };
}

/**
 * Puts them back together as the wire form. An empty date is the clear - the
 * empty string, which the API reads as "no date" rather than as "no opinion"
 * (IssuePatchRequest in Dtos.cs).
 *
 * A time with no date is dropped rather than refused: it is a half-finished
 * edit, not an instruction.
 */
export function fromInputs({ date, time }: MomentInputs): string {
  if (!date) return '';
  if (!time) return date;

  const [year, month, day] = date.split('-').map(Number);
  const [hour, minute] = time.split(':').map(Number);

  // Built in the viewer's zone, sent as the instant it names: 5pm here is 5pm
  // for whoever set it, and the server stores the moment rather than the words.
  const at = new Date(year, month - 1, day, hour, minute, 0, 0);
  return Number.isNaN(at.getTime()) ? '' : `${at.toISOString().slice(0, 19)}Z`;
}
