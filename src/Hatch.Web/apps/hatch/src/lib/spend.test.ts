import { describe, expect, it } from 'vitest';
import {
  bars,
  bucketPhrase,
  graphLabel,
  measureLabel,
  peak,
  peakLabel,
  spanPhrase,
  stillGathering,
} from './spend';
import { parseMeasure } from './leaderboard';
import type { WorkLogBucket, WorkLogHistory, WorkLogTotals } from '../types';

const totals = (over: Partial<WorkLogTotals> = {}): WorkLogTotals => ({
  sessions: 1,
  errors: 0,
  inputTokens: 0,
  outputTokens: 0,
  cacheCreationTokens: 0,
  cacheReadTokens: 0,
  totalTokens: 1_000,
  costUsd: 1,
  ...over,
});

const empty: WorkLogTotals = {
  sessions: 0,
  errors: 0,
  inputTokens: 0,
  outputTokens: 0,
  cacheCreationTokens: 0,
  cacheReadTokens: 0,
  totalTokens: 0,
  costUsd: 0,
};

const bucket = (start: string, over?: Partial<WorkLogTotals>): WorkLogBucket => ({
  start,
  end: new Date(new Date(start).getTime() + 86_400_000).toISOString(),
  totals: over ? totals(over) : { ...empty },
});

const history = (over: Partial<WorkLogHistory> = {}): WorkLogHistory => ({
  from: '2026-08-24T12:00:00Z',
  to: '2026-09-07T12:00:00Z',
  bucket: 'day',
  totals: totals({ sessions: 3, totalTokens: 1_000_000, costUsd: 43 }),
  firstSessionAt: '2026-08-19T12:00:00Z',
  lastSessionAt: '2026-09-07T12:00:00Z',
  /* The tokens peak in the first bucket and the money in the third, so a
     measure switch is proved to rescale rather than only relabel. */
  buckets: [
    bucket('2026-09-04T12:00:00Z', { totalTokens: 800_000, costUsd: 3 }),
    bucket('2026-09-05T12:00:00Z'),
    bucket('2026-09-06T12:00:00Z', { totalTokens: 200_000, costUsd: 40 }),
  ],
  ...over,
});

describe('bars', () => {
  it('draws one per bucket, oldest first, keeping the empty one in its place', () => {
    const drawn = bars(history(), 'tokens', 'en-GB');

    // An hour in which nothing ran cost nothing, which is a measurement - so it
    // is a zero rather than a gap.
    expect(drawn).toHaveLength(3);
    expect(drawn.map((b) => b.start)).toEqual(history().buckets.map((b) => b.start));
    expect(drawn[1].value).toBe(0);
    expect(drawn[1].fraction).toBe(0);
  });

  it('scales against the tallest bucket in the range', () => {
    const drawn = bars(history(), 'tokens');

    expect(drawn[0].fraction).toBe(1);
    expect(drawn[2].fraction).toBeCloseTo(0.25);
  });

  it('rescales rather than relabels when the measure changes', () => {
    const inTokens = bars(history(), 'tokens');
    const inMoney = bars(history(), 'cost');

    // Tokens peak in the first bucket, the money in the third. Two measures on
    // one axis would draw two lies crossing; this is the reason they never are.
    expect(inTokens[0].fraction).toBe(1);
    expect(inMoney[0].fraction).toBeCloseTo(0.075);
    expect(inMoney[2].fraction).toBe(1);
  });

  it('gives every bar a fraction of zero when nothing ran at all', () => {
    const flat = history({ buckets: [bucket('2026-09-04T12:00:00Z'), bucket('2026-09-05T12:00:00Z')] });

    expect(peak(flat, 'tokens')).toBe(0);
    // Zero rather than NaN or Infinity: nothing divides by the peak.
    expect(bars(flat, 'tokens').map((b) => b.fraction)).toEqual([0, 0]);
    expect(bars(flat, 'cost').map((b) => b.fraction)).toEqual([0, 0]);
  });

  it('labels a daily bucket by its day and an hourly one by its hour', () => {
    expect(bars(history(), 'tokens', 'en-GB')[0].label).toBe('Fri 4 Sept — 800k tokens');
    expect(bars(history({ bucket: 'hour' }), 'cost', 'en-GB')[2].label).toContain('Sun 6 Sept, ');
    expect(bars(history({ bucket: 'hour' }), 'cost', 'en-GB')[2].label).toContain('— $40.00');
  });
});

describe('the labels', () => {
  it('names the bucket size the server chose, not the one that was asked for', () => {
    // A range asked for in days that came back hourly is exactly the case the
    // graph has to name from the answer.
    expect(bucketPhrase(history())).toBe('by day');
    expect(bucketPhrase(history({ bucket: 'hour' }))).toBe('by hour');
  });

  it('says the window that came back, snapped', () => {
    expect(spanPhrase(history(), 'en-GB')).toBe('24 Aug to 7 Sept');
  });

  it('names the top of the scale in the measure it is drawing', () => {
    expect(measureLabel('tokens')).toBe('tokens');
    expect(measureLabel('cost')).toBe('notional USD');
    expect(peakLabel(history(), 'tokens')).toBe('800k tokens');
    expect(peakLabel(history(), 'cost')).toBe('$40.00');
  });

  it('says the whole picture in one sentence, for a screen reader', () => {
    expect(graphLabel(history(), 'tokens', 'en-GB')).toBe(
      'Spend by day, 24 Aug to 7 Sept, tokens, peaking at 800k tokens',
    );
  });
});

describe('stillGathering', () => {
  it('is true only when nothing has ever been logged', () => {
    expect(stillGathering(history({ firstSessionAt: null }))).toBe(true);

    // Every bucket empty and a log that is not: a flat range is still drawn.
    const quiet = history({ buckets: [bucket('2026-09-04T12:00:00Z')] });
    expect(stillGathering(quiet)).toBe(false);
  });
});

describe('parseMeasure', () => {
  it('opens on tokens, and falls back rather than refusing', () => {
    expect(parseMeasure(null)).toBe('tokens');
    expect(parseMeasure('turns')).toBe('tokens');
    expect(parseMeasure('cost')).toBe('cost');
  });
});
