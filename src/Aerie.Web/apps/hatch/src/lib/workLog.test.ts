import { describe, expect, it } from 'vitest';
import {
  compactTokens,
  durationPhrase,
  entryMark,
  entryTitle,
  errorPhrase,
  hasDescendantSpend,
  moneyPhrase,
  ownPhrase,
  resumeCommand,
  showsWorkLog,
  totalsPhrase,
} from './workLog';
import type { WorkLog, WorkLogEntry, WorkLogTotals } from '../types';

const totals = (over: Partial<WorkLogTotals> = {}): WorkLogTotals => ({
  sessions: 1,
  errors: 0,
  inputTokens: 900,
  outputTokens: 280,
  cacheCreationTokens: 3_200,
  cacheReadTokens: 59_000,
  totalTokens: 63_380,
  costUsd: 3.41,
  ...over,
});

const entry = (over: Partial<WorkLogEntry> = {}): WorkLogEntry => ({
  id: 1,
  sessionId: '3d1abf4f-aaaa-4000-8000-000000000001',
  startedAt: '2026-09-07T02:40:00Z',
  endedAt: '2026-09-07T02:54:02Z',
  durationMs: 842_000,
  title: 'Wired the battery into the nav',
  summary: 'Read the headroom server-side and drew the ring.',
  described: true,
  isError: false,
  turns: 41,
  costUsd: 3.41,
  inputTokens: 900,
  outputTokens: 280,
  cacheCreationTokens: 3_200,
  cacheReadTokens: 59_000,
  totalTokens: 63_380,
  models: [],
  ...over,
});

const log = (over: Partial<WorkLog> = {}): WorkLog => ({
  key: 'AER-12',
  totals: totals(),
  own: totals(),
  entries: [entry()],
  ...over,
});

describe('compactTokens', () => {
  it('says millions to one place and thousands to none', () => {
    expect(compactTokens(1_400_000)).toBe('1.4M');
    expect(compactTokens(912_400)).toBe('912k');
    expect(compactTokens(840)).toBe('840');
  });

  it('crosses each threshold the way a reader expects', () => {
    // 999_500 rounds to 1000k, which reads as a bug. The million branch takes
    // it first.
    expect(compactTokens(1_000_000)).toBe('1M');
    expect(compactTokens(999)).toBe('999');
    expect(compactTokens(1_000)).toBe('1k');
  });

  it('says zero rather than nothing', () => {
    expect(compactTokens(0)).toBe('0');
    expect(compactTokens(-1)).toBe('0');
    expect(compactTokens(Number.NaN)).toBe('0');
  });
});

describe('moneyPhrase', () => {
  it('keeps enough places that a cheap session does not read as free', () => {
    expect(moneyPhrase(3.4123)).toBe('$3.41');
    expect(moneyPhrase(0.0012)).toBe('$0.0012');
    expect(moneyPhrase(0.01)).toBe('$0.01');
  });

  it('says $0 only when it is', () => {
    expect(moneyPhrase(0)).toBe('$0');
    expect(moneyPhrase(Number.NaN)).toBe('$0');
  });
});

describe('durationPhrase', () => {
  it('matches the clock the terminal prints', () => {
    expect(durationPhrase(842_000)).toBe('14m02s');
    expect(durationPhrase(4_888)).toBe('4s');
    expect(durationPhrase(3_720_000)).toBe('1h02m');
  });

  it('does not say a run took no time when it took some', () => {
    expect(durationPhrase(900)).toBe('0s');
    expect(durationPhrase(0)).toBe('0s');
    expect(durationPhrase(-5)).toBe('0s');
  });
});

describe('resumeCommand', () => {
  it('is the whole command, so what is read is what is copied', () => {
    expect(resumeCommand('abc-123')).toBe('claude --resume abc-123');
  });
});

describe('entryMark', () => {
  it('is null on an ordinary entry', () => {
    expect(entryMark(entry())).toBeNull();
  });

  it('marks a session that never said what it did', () => {
    expect(entryMark(entry({ described: false, title: null, summary: null }))).toBe('undescribed');
  });

  it('marks an errored session, and says so even when it also said nothing', () => {
    expect(entryMark(entry({ isError: true }))).toBe('error');
    expect(entryMark(entry({ isError: true, described: false, title: null }))).toBe('error');
  });
});

describe('entryTitle', () => {
  it('is the title when there is one', () => {
    expect(entryTitle(entry())).toBe('Wired the battery into the nav');
  });

  it('names the undescribed entry rather than leaving a blank', () => {
    expect(entryTitle(entry({ described: false, title: null }))).toBe('A session that did not say what it did');

    // Described but empty is the server disagreeing with itself; the client
    // still draws something readable.
    expect(entryTitle(entry({ described: true, title: '' }))).toBe('A session that did not say what it did');
  });
});

describe('showsWorkLog', () => {
  it('draws nothing at all for an issue with no sessions anywhere beneath it', () => {
    expect(showsWorkLog(null)).toBe(false);
    expect(showsWorkLog(log({ entries: [], totals: totals({ sessions: 0, totalTokens: 0 }) }))).toBe(false);
  });

  it('draws for an epic whose stories cost money and which has no session of its own', () => {
    expect(showsWorkLog(log({ entries: [], own: totals({ sessions: 0 }), totals: totals({ sessions: 4 }) }))).toBe(
      true,
    );
  });

  it('draws for an issue with entries of its own', () => {
    expect(showsWorkLog(log())).toBe(true);
  });
});

describe('the sentences the totals say', () => {
  it('reads the subtree, in tokens first and dollars second', () => {
    expect(totalsPhrase(totals({ sessions: 3, totalTokens: 1_400_000, costUsd: 3.41 }))).toBe(
      '3 sessions · 1.4M tokens · $3.41',
    );
  });

  it('says session, singular, at one', () => {
    expect(totalsPhrase(totals({ sessions: 1 }))).toContain('1 session ·');
  });

  it('says when the subtree cost more than the issue itself did', () => {
    const epic = log({ own: totals({ sessions: 1, totalTokens: 20_000 }), totals: totals({ sessions: 4 }) });

    expect(hasDescendantSpend(epic)).toBe(true);
    expect(ownPhrase(epic)).toBe('20k of it on this issue itself');
  });

  it('says so plainly when an epic has spent nothing of its own', () => {
    const epic = log({ entries: [], own: totals({ sessions: 0, totalTokens: 0 }), totals: totals({ sessions: 4 }) });

    expect(hasDescendantSpend(epic)).toBe(true);
    expect(ownPhrase(epic)).toBe('none of it on this issue itself');
  });

  it('says nothing extra when every session belongs to this issue', () => {
    expect(hasDescendantSpend(log())).toBe(false);
  });

  it('counts the errors, or stays quiet', () => {
    expect(errorPhrase(totals())).toBeNull();
    expect(errorPhrase(totals({ errors: 1 }))).toBe('1 session ended with an error');
    expect(errorPhrase(totals({ errors: 2 }))).toBe('2 sessions ended with an error');
  });
});
