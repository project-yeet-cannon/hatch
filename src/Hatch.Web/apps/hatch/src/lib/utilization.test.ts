import { describe, expect, it } from 'vitest';
import {
  SESSION_WINDOW_MS,
  agePhrase,
  batteryLabel,
  hasBattery,
  percentLabel,
  resetPhrase,
  ringFraction,
  sessionLimit,
  toneClass,
} from './utilization';
import type { Utilization, UtilizationLimit } from '../types';

const NOW = new Date('2026-09-07T08:00:00Z');

const at = (msFromNow: number) => new Date(NOW.getTime() + msFromNow).toISOString();

const limit = (over: Partial<UtilizationLimit> = {}): UtilizationLimit => ({
  window: 'session',
  label: 'Session',
  percent: 17,
  tone: 'normal',
  resetsAt: at(90 * 60_000),
  isActive: true,
  ...over,
});

const reading = (over: Partial<Utilization> = {}): Utilization => ({
  state: 'ok',
  readAt: at(0),
  limits: [limit()],
  credits: null,
  ...over,
});

describe('ringFraction', () => {
  it('is a full ring a whole window out, and empty at the instant of the reset', () => {
    expect(ringFraction(at(SESSION_WINDOW_MS), NOW)).toBe(1);
    expect(ringFraction(at(0), NOW)).toBe(0);
  });

  it('is the share of the window still to run', () => {
    expect(ringFraction(at(SESSION_WINDOW_MS / 2), NOW)).toBeCloseTo(0.5);
    expect(ringFraction(at(SESSION_WINDOW_MS / 4), NOW)).toBeCloseTo(0.25);
  });

  it('clamps past both ends rather than drawing a negative or a wrapping arc', () => {
    expect(ringFraction(at(-60 * 60_000), NOW)).toBe(0);
    expect(ringFraction(at(SESSION_WINDOW_MS * 3), NOW)).toBe(1);
  });

  /* The trap the whole story is built around: the account sends rows with no
     reset instant, and unknown is a third answer rather than a zero. */
  it('is unknown, not zero and not full, when there is nothing to count to', () => {
    expect(ringFraction(null, NOW)).toBeNull();
    expect(ringFraction(undefined, NOW)).toBeNull();
    expect(ringFraction('not an instant', NOW)).toBeNull();
  });
});

describe('resetPhrase', () => {
  it('says hours and minutes', () => {
    expect(resetPhrase(at(80 * 60_000), NOW)).toBe('resets in 1h 20m');
  });

  it('drops the minutes on a whole hour, and the hours below one', () => {
    expect(resetPhrase(at(120 * 60_000), NOW)).toBe('resets in 2h');
    expect(resetPhrase(at(45 * 60_000), NOW)).toBe('resets in 45m');
  });

  /* "resets in 0m" for fifty-nine seconds reads as broken, so the last minute
     is a phrase rather than a number. */
  it('says under a minute for the last minute, and for one already past', () => {
    expect(resetPhrase(at(30_000), NOW)).toBe('resets in under a minute');
    expect(resetPhrase(at(-1), NOW)).toBe('resets in under a minute');
  });

  it('says the reset time is unknown when there is no instant', () => {
    expect(resetPhrase(null, NOW)).toBe('reset time unknown');
    expect(resetPhrase('whenever', NOW)).toBe('reset time unknown');
  });
});

describe('agePhrase', () => {
  it('says just now inside the first minute', () => {
    expect(agePhrase(at(0), NOW)).toBe('read just now');
    expect(agePhrase(at(-30_000), NOW)).toBe('read just now');
  });

  it('counts minutes, singular and plural', () => {
    expect(agePhrase(at(-60_000), NOW)).toBe('read 1 minute ago');
    expect(agePhrase(at(-4 * 60_000), NOW)).toBe('read 4 minutes ago');
  });

  it('counts hours once the minutes run out', () => {
    expect(agePhrase(at(-60 * 60_000), NOW)).toBe('read 1 hour ago');
    expect(agePhrase(at(-150 * 60_000), NOW)).toBe('read 2 hours ago');
  });

  /* A reading taken a moment ahead of this clock is "just now", not a negative
     age: the server's clock and the browser's are not the same clock. */
  it('does not run backwards for a reading from the future', () => {
    expect(agePhrase(at(30_000), NOW)).toBe('read just now');
  });

  it('says there has never been one when there is no instant', () => {
    expect(agePhrase(null, NOW)).toBe('never read');
  });
});

describe('sessionLimit', () => {
  it('finds the session row wherever the account put it', () => {
    const found = sessionLimit(
      reading({
        limits: [limit({ window: 'weekly', label: 'Weekly' }), limit({ window: 'session', percent: 42 })],
      }),
    );

    expect(found?.percent).toBe(42);
  });

  it('is null when there is no session row, and when there is no reading', () => {
    expect(sessionLimit(reading({ limits: [] }))).toBeNull();
    expect(sessionLimit(null)).toBeNull();
  });
});

describe('toneClass', () => {
  it('paints the three tones the server sends', () => {
    expect(toneClass('normal')).toBe('hatch-battery-normal');
    expect(toneClass('warn')).toBe('hatch-battery-warn');
    expect(toneClass('danger')).toBe('hatch-battery-danger');
  });

  /* An unexpected word must not produce an unstyled glyph. The server decides
     the tone; this only guarantees the class exists. */
  it('falls back to normal for anything else', () => {
    expect(toneClass('chartreuse')).toBe('hatch-battery-normal');
    expect(toneClass(null)).toBe('hatch-battery-normal');
    expect(toneClass(undefined)).toBe('hatch-battery-normal');
  });
});

describe('hasBattery', () => {
  /* The 204: no token configured. No element, no placeholder, no reserved
     space - which is the property the whole story is built to protect. */
  it('is false only for the 204', () => {
    expect(hasBattery(null)).toBe(false);
    expect(hasBattery(reading())).toBe(true);
    expect(hasBattery(reading({ state: 'stale' }))).toBe(true);
    expect(hasBattery(reading({ state: 'unknown', readAt: null, limits: [] }))).toBe(true);
  });
});

describe('percentLabel', () => {
  it('states the percentage as a whole number', () => {
    expect(percentLabel(limit({ percent: 17 }))).toBe('17%');
  });

  it('is an em dash when there is no reading behind it', () => {
    expect(percentLabel(null)).toBe('—');
  });
});

describe('batteryLabel', () => {
  it('says the number and the time left', () => {
    expect(batteryLabel(reading(), NOW)).toBe('Claude session usage 17%, resets in 1h 30m');
  });

  it('says the reset is unknown when the row carries no instant', () => {
    expect(batteryLabel(reading({ limits: [limit({ resetsAt: null })] }), NOW)).toBe(
      'Claude session usage 17%, reset time unknown',
    );
  });

  it('carries the age of a stale reading, so an old number says it is old', () => {
    const stale = reading({ state: 'stale', readAt: at(-12 * 60_000) });

    expect(batteryLabel(stale, NOW)).toBe('Claude session usage 17%, resets in 1h 30m (read 12 minutes ago)');
  });

  /* Both degraded answers say the same thing out loud - the account could not
     be reached - because that is all either of them knows. */
  it('reads as unknown when there has never been a reading, and when there is none at all', () => {
    const unknown = reading({ state: 'unknown', readAt: null, limits: [] });

    expect(batteryLabel(unknown, NOW)).toBe('Claude usage unknown — the account could not be reached');
    expect(batteryLabel(null, NOW)).toBe('Claude usage unknown — the account could not be reached');
  });
});

describe('a model-scoped row', () => {
  /* The label is the account's own word for the model and Hatch renders it as
     given - which is why no model name is written down in this repository, and
     why the name in this test is one that appears nowhere else. */
  it('is named by the label the server derived, whatever it says', () => {
    const scoped = limit({ window: 'weeklyModel', label: 'Some Model Nobody Here Names', resetsAt: null });
    const withScoped = reading({ limits: [limit(), scoped] });

    expect(withScoped.limits[1].label).toBe('Some Model Nobody Here Names');
    expect(resetPhrase(withScoped.limits[1].resetsAt, NOW)).toBe('reset time unknown');
    // Still rendered, still in the account's order, and not filtered by tone.
    expect(sessionLimit(withScoped)?.window).toBe('session');
  });
});
