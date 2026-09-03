import { describe, expect, it } from 'vitest';
import { daysUntil, fromInputs, isWaiting, momentWords, parseMoment, toInputs, urgencyOf } from './schedule';
import type { Moment } from './schedule';

/* These run in whatever zone the machine is in, on purpose - the bugs this
   module exists to prevent are all timezone bugs, and a suite pinned to UTC
   would not see one of them.

   The trick that makes that possible: `now` is built from local components, so
   it is noon on 2 September wherever the test runs, and date-only moments are
   written as the bare strings the API sends. Nothing here assumes an offset. */
const noon = (year: number, month: number, day: number, hour = 12) => new Date(year, month - 1, day, hour, 0, 0, 0);

const NOW = noon(2026, 9, 2);

const moment = (text: string): Moment => {
  const parsed = parseMoment(text);
  if (!parsed) throw new Error(`${text} did not parse`);
  return parsed;
};

describe('parseMoment', () => {
  it('reads a bare date as carrying no time of day', () => {
    expect(moment('2026-09-12').hasTime).toBe(false);
  });

  it('reads an instant as carrying one', () => {
    expect(moment('2026-09-12T17:00:00Z').hasTime).toBe(true);
  });

  it('has nothing to say about null or nonsense', () => {
    expect(parseMoment(null)).toBeNull();
    expect(parseMoment('')).toBeNull();
    expect(parseMoment('whenever')).toBeNull();
  });
});

describe('daysUntil', () => {
  /* The reason this module exists. A date-only moment is read in UTC, so its
     day is the one that was typed - in every zone, including the ones where
     UTC midnight falls on the day before or the day after. */
  it('reads a date-only moment as the day it was written, in any zone', () => {
    expect(daysUntil(moment('2026-09-02'), NOW)).toBe(0);
    expect(daysUntil(moment('2026-09-03'), NOW)).toBe(1);
    expect(daysUntil(moment('2026-09-01'), NOW)).toBe(-1);
  });

  it('counts calendar days rather than elapsed hours', () => {
    // 9am tomorrow, from 11pm tonight: 10 hours away, and still tomorrow.
    const lateTonight = noon(2026, 9, 2, 23);
    expect(daysUntil(moment(fromInputs({ date: '2026-09-03', time: '09:00' })), lateTonight)).toBe(1);
  });

  it('crosses a month boundary', () => {
    expect(daysUntil(moment('2026-10-01'), NOW)).toBe(29);
  });
});

describe('urgencyOf', () => {
  it('warms as the date approaches', () => {
    expect(urgencyOf(moment('2026-09-20'), NOW)).toBe('later');
    expect(urgencyOf(moment('2026-09-05'), NOW)).toBe('soon');
    expect(urgencyOf(moment('2026-09-03'), NOW)).toBe('soon');
    expect(urgencyOf(moment('2026-09-02'), NOW)).toBe('today');
    expect(urgencyOf(moment('2026-09-01'), NOW)).toBe('overdue');
  });

  it('warns from exactly three days out and not four', () => {
    expect(urgencyOf(moment('2026-09-05'), NOW)).toBe('soon');
    expect(urgencyOf(moment('2026-09-06'), NOW)).toBe('later');
  });

  /* A date is a promise about a day. Something due "the 2nd" is not late at one
     minute past midnight on the 2nd, and a board that says it is will be
     ignored by lunchtime. */
  it('leaves a date-only moment alone all day', () => {
    expect(urgencyOf(moment('2026-09-02'), noon(2026, 9, 2, 0))).toBe('today');
    expect(urgencyOf(moment('2026-09-02'), noon(2026, 9, 2, 23))).toBe('today');
  });

  /* An instant is a promise about a minute, and goes overdue on it. */
  it('takes a time of day at its word', () => {
    const fivePm = moment(fromInputs({ date: '2026-09-02', time: '17:00' }));

    expect(urgencyOf(fivePm, noon(2026, 9, 2, 16))).toBe('today');
    expect(urgencyOf(fivePm, noon(2026, 9, 2, 18))).toBe('overdue');
  });
});

describe('isWaiting', () => {
  it('holds an issue back until the day it names', () => {
    expect(isWaiting('2026-09-03', NOW)).toBe(true);
    expect(isWaiting('2027-01-01', NOW)).toBe(true);
  });

  /* Ready at the start of its day, whatever hour it was set to: a ticket
     nobody may look at over breakfast is a rule with no purpose. */
  it('releases it at the start of that day, not at the hour set', () => {
    expect(isWaiting('2026-09-02', NOW)).toBe(false);
    expect(isWaiting(fromInputs({ date: '2026-09-02', time: '17:00' }), noon(2026, 9, 2, 9))).toBe(false);
  });

  it('leaves an issue with no ready date alone', () => {
    expect(isWaiting(null, NOW)).toBe(false);
    expect(isWaiting('', NOW)).toBe(false);
  });

  it('does not hold back an issue whose date has passed', () => {
    expect(isWaiting('2026-08-01', NOW)).toBe(false);
  });
});

describe('momentWords', () => {
  it('says the near days in words', () => {
    expect(momentWords(moment('2026-09-02'), NOW)).toBe('Today');
    expect(momentWords(moment('2026-09-03'), NOW)).toBe('Tomorrow');
    expect(momentWords(moment('2026-09-01'), NOW)).toBe('Yesterday');
  });

  it('names the date once it is further off', () => {
    expect(momentWords(moment('2026-09-20'), NOW)).toContain('20');
    expect(momentWords(moment('2026-09-20'), NOW)).not.toContain('2026');
  });

  it('says the year when it is not this one', () => {
    expect(momentWords(moment('2027-09-20'), NOW)).toContain('2027');
  });

  it('appends the time when one was meant', () => {
    const words = momentWords(moment(fromInputs({ date: '2026-09-02', time: '17:00' })), NOW);
    expect(words).toContain('Today');
    expect(words).toMatch(/5:00|17:00/);
  });
});

describe('the pickers', () => {
  it('round-trips a date', () => {
    expect(fromInputs(toInputs('2026-09-12'))).toBe('2026-09-12');
  });

  it('round-trips an instant through the viewer’s own clock', () => {
    const stored = fromInputs({ date: '2026-09-12', time: '17:00' });

    expect(toInputs(stored)).toEqual({ date: '2026-09-12', time: '17:00' });
    expect(fromInputs(toInputs(stored))).toBe(stored);
  });

  it('sends the empty string for the clear', () => {
    expect(fromInputs({ date: '', time: '' })).toBe('');
    expect(fromInputs({ date: '', time: '17:00' })).toBe('');
  });

  it('shows empty inputs for an issue with no date', () => {
    expect(toInputs(null)).toEqual({ date: '', time: '' });
  });

  /* The whole point of the two-input control: dropping the time has to leave
     the date behind, and it cannot if the value is one datetime-local. */
  it('drops back to a bare date when the time is cleared', () => {
    const withTime = fromInputs({ date: '2026-09-12', time: '17:00' });

    expect(fromInputs({ ...toInputs(withTime), time: '' })).toBe('2026-09-12');
  });
});
