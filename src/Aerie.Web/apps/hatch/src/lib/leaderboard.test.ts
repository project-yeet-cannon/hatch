import { describe, expect, it } from 'vitest';
import {
  DEFAULT_PRESET,
  PODIUM,
  capPhrase,
  dayLabel,
  emptiness,
  presetRange,
  rankedTop,
  rankingCaption,
  resolveQuery,
  windowPhrase,
} from './leaderboard';
import type { WorkLogSession, WorkLogSessions, WorkLogTotals } from '../types';

const totals = (over: Partial<WorkLogTotals> = {}): WorkLogTotals => ({
  sessions: 31,
  errors: 0,
  inputTokens: 900,
  outputTokens: 280,
  cacheCreationTokens: 3_200,
  cacheReadTokens: 59_000,
  totalTokens: 63_380,
  costUsd: 3.41,
  ...over,
});

const session = (over: Partial<WorkLogSession> = {}): WorkLogSession => ({
  id: 1,
  sessionId: '3d1abf4f-aaaa-4000-8000-000000000001',
  issueKey: 'AER-12',
  issueTitle: 'The leaderboard query',
  startedAt: '2026-09-07T02:40:00Z',
  endedAt: '2026-09-07T02:54:02Z',
  durationMs: 842_000,
  title: 'Broke the ranking out of the rollup',
  described: true,
  isError: false,
  turns: 41,
  costUsd: 3.41,
  inputTokens: 900,
  outputTokens: 280,
  cacheCreationTokens: 3_200,
  cacheReadTokens: 59_000,
  totalTokens: 63_380,
  ...over,
});

const view = (over: Partial<WorkLogSessions> = {}): WorkLogSessions => ({
  from: '2026-08-24T00:00:00Z',
  to: '2026-09-08T00:00:00Z',
  sort: 'tokens',
  totals: totals(),
  firstSessionAt: '2026-08-19T12:00:00Z',
  lastSessionAt: '2026-09-07T12:00:00Z',
  sessions: [session()],
  ...over,
});

/* Midday UTC throughout, so no assertion straddles a date boundary in a zone a
   test might be run in. */
const now = new Date('2026-09-07T12:34:00Z');

describe('presetRange', () => {
  it('ends at the first grid boundary strictly after now', () => {
    // The last bucket is the one running now, drawn as far as it has got.
    expect(presetRange('24h', now, 0).to).toBe('2026-09-07T13:00:00.000Z');
    expect(presetRange('14d', now, 0).to).toBe('2026-09-08T00:00:00.000Z');
  });

  it('still gives a whole bucket when now is exactly on a boundary', () => {
    const onTheHour = new Date('2026-09-07T13:00:00Z');

    expect(presetRange('24h', onTheHour, 0).to).toBe('2026-09-07T14:00:00.000Z');
  });

  it('reaches back the span each preset names', () => {
    expect(presetRange('24h', now, 0).from).toBe('2026-09-06T13:00:00.000Z');
    expect(presetRange('today', now, 0).from).toBe('2026-09-07T00:00:00.000Z');
    expect(presetRange('7d', now, 0).from).toBe('2026-09-01T00:00:00.000Z');
    expect(presetRange('14d', now, 0).from).toBe('2026-08-25T00:00:00.000Z');
    expect(presetRange('30d', now, 0).from).toBe('2026-08-09T00:00:00.000Z');
  });

  it("shifts the daily grid to the reader's midnight", () => {
    // Five hours west: the boundary is 05:00Z, which is midnight there.
    const range = presetRange('today', now, -300);

    expect(range.from).toBe('2026-09-07T05:00:00.000Z');
    expect(range.to).toBe('2026-09-08T05:00:00.000Z');
  });

  it('leaves the hourly grid alone, as the server does', () => {
    // Every whole-hour zone lands on the same hourly grid anyway, and a
    // half-hour zone gets a label half an hour off rather than a wrong answer.
    expect(presetRange('24h', now, -300)).toEqual(presetRange('24h', now, 0));
  });
});

describe('resolveQuery', () => {
  it('opens on the default preset and sort with nothing in the URL', () => {
    const query = resolveQuery(new URLSearchParams(), now, 0);

    expect(query.preset).toBe(DEFAULT_PRESET);
    expect(query.sort).toBe('tokens');
    expect(query.measure).toBe('tokens');
    expect(query.issue).toBeNull();
    expect(query).toMatchObject(presetRange(DEFAULT_PRESET, now, 0));
  });

  it('falls back rather than refusing what it does not recognise', () => {
    const query = resolveQuery(new URLSearchParams({ range: 'fortnight', sort: 'turns', measure: 'turns' }), now, 0);

    expect(query.preset).toBe(DEFAULT_PRESET);
    expect(query.sort).toBe('tokens');
    expect(query.measure).toBe('tokens');
  });

  it('takes the sort, the measure and the issue filter as written', () => {
    const query = resolveQuery(new URLSearchParams({ sort: 'cost', measure: 'cost', issue: 'AER-12' }), now, 0);

    expect(query.sort).toBe('cost');
    // The graph's choice is state like any other: a pasted link that opens on
    // dollars is the same argument as a pasted sort.
    expect(query.measure).toBe('cost');
    expect(query.issue).toBe('AER-12');
  });

  it('lets an explicit window beat the preset and passes it through as typed', () => {
    const query = resolveQuery(
      new URLSearchParams({ range: '7d', from: '2026-01-01T09:15:00Z', to: '2026-01-02T09:15:00Z' }),
      now,
      0,
    );

    // A paste is honoured exactly, off-grid or not, and no preset is pressed.
    expect(query.from).toBe('2026-01-01T09:15:00Z');
    expect(query.to).toBe('2026-01-02T09:15:00Z');
    expect(query.preset).toBeNull();
  });

  it('ignores half a window', () => {
    const query = resolveQuery(new URLSearchParams({ from: '2026-01-01T09:15:00Z' }), now, 0);

    expect(query.preset).toBe(DEFAULT_PRESET);
    expect(query.from).toBe(presetRange(DEFAULT_PRESET, now, 0).from);
  });
});

describe('rankedTop', () => {
  it('takes five from a longer list and all of a shorter one', () => {
    const many = Array.from({ length: 9 }, (_, i) => session({ id: i }));

    expect(rankedTop(many)).toHaveLength(PODIUM);
    expect(rankedTop([session({ id: 1 }), session({ id: 2 })])).toHaveLength(2);
  });

  it('never re-orders what the server ranked', () => {
    const given = [session({ id: 1, totalTokens: 10 }), session({ id: 2, totalTokens: 900_000 })];

    expect(rankedTop(given).map((s) => s.id)).toEqual([1, 2]);
  });
});

describe('the sentences the ranking says', () => {
  it('counts the podium against the whole window', () => {
    expect(rankingCaption(totals({ sessions: 31 }), 5)).toBe(
      'The 5 most expensive sessions of the 31 that ran in this window.',
    );
  });

  it('says session, singular, when there is only one', () => {
    expect(rankingCaption(totals({ sessions: 1 }), 1)).toBe(
      'The most expensive session of the 1 that ran in this window.',
    );
  });
});

describe('capPhrase', () => {
  it('says how many of how many when the filter holds more than the table drew', () => {
    expect(capPhrase(totals({ sessions: 214 }), 100)).toBe(
      'Showing the top 100 of 214 sessions; the totals above are for all of them.',
    );
  });

  it('says nothing when every row is on screen', () => {
    expect(capPhrase(totals({ sessions: 31 }), 31)).toBeNull();
    expect(capPhrase(totals({ sessions: 4 }), 100)).toBeNull();
  });
});

describe('emptiness', () => {
  it('says nothing at all when there are rows', () => {
    expect(emptiness(view(), null)).toBeNull();
  });

  it('tells a log that has never been written from a window nobody worked', () => {
    const never = emptiness(view({ firstSessionAt: null, lastSessionAt: null, totals: totals({ sessions: 0 }) }), null);
    const empty = emptiness(view({ totals: totals({ sessions: 0 }) }), null, 'en-GB');

    // firstSessionAt tells them apart, not the row count - and `never` wins
    // when both could speak.
    expect(never?.kind).toBe('never');
    expect(never?.text).toContain('Nothing has ever been logged here');
    expect(empty?.kind).toBe('range');
    expect(empty?.text).toBe('No session ran in this window. The log runs from 19 Aug to 7 Sept.');
  });

  it('names the issue filter in the never-logged sentence', () => {
    const scoped = emptiness(view({ firstSessionAt: null, lastSessionAt: null }), 'AER-12');

    expect(scoped?.text).toContain('under AER-12');
  });
});

describe('the labels', () => {
  it('says a day short and a window as two of them', () => {
    expect(dayLabel('2026-08-19T12:00:00Z', 'en-GB')).toBe('19 Aug');
    expect(windowPhrase('2026-08-24T12:00:00Z', '2026-09-07T12:00:00Z', 'en-GB')).toBe('24 Aug – 7 Sept');
  });
});
