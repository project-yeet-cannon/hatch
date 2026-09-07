/* The spend graph's geometry and every sentence it says, apart from the
   component - the same split lib/utilization.ts makes from the battery, and for
   the same reason: this app has no DOM test setup, so what can be wrong lives in
   a function with a test beside it.

   Three things this file is deliberately not:

   - It is not a charting library, and the app does not carry one. One rect per
     bucket in the house's own tokens is the whole picture; `UtilizationBattery`
     is the precedent for hand-cut SVG here.
   - It draws no gaps. A poll's history has them, because a missing reading
     means nobody looked. A work log has none: an hour in which nothing ran is
     an hour that cost nothing, and the server emits it as a zeroed bucket.
     Drawing that as a break would assert an absence where there is a
     measurement.
   - It never puts the two measures on one axis. They differ by six orders of
     magnitude and a second y-axis would draw two lies crossing, so a measure
     switch rescales the whole graph rather than adding a series to it.

   It reads `dayLabel` and `SpendMeasure` from lib/leaderboard.ts and nothing
   reads back the other way: the leaderboard owns the URL and the page's shared
   vocabulary, and the graph is drawn from it. */

import { type SpendMeasure, dayLabel } from './leaderboard';
import { compactTokens, moneyPhrase } from './workLog';
import type { WorkLogHistory, WorkLogTotals } from '../types';

export type { SpendMeasure };

/** One bar. `start` and `end` are the bucket's own, from the server. */
export interface SpendBar {
  start: string;
  end: string;
  value: number;
  /** 0…1 against the tallest bucket in the range. Zero for every bar when
      nothing ran, rather than NaN - see `bars`. */
  fraction: number;
  /** What a hover and a screen reader get: `Sat 6 Sep, 14:00 — 1.4M tokens`. */
  label: string;
}

/** What the control's two buttons say, and what the caption calls the axis. */
export const measureLabel = (measure: SpendMeasure): string =>
  measure === 'cost' ? 'notional USD' : 'tokens';

/** The figure a measure reads off a bucket's totals. */
export const measureValue = (totals: WorkLogTotals, measure: SpendMeasure): number =>
  measure === 'cost' ? totals.costUsd : totals.totalTokens;

/** The same figure said the way the rest of the app says it - `compactTokens`
    and `moneyPhrase` rather than a second vocabulary for one number. */
export const measurePhrase = (value: number, measure: SpendMeasure): string =>
  measure === 'cost' ? moneyPhrase(value) : `${compactTokens(value)} tokens`;

/** The tallest bucket in the range, or 0 when every one of them is empty. */
export const peak = (history: WorkLogHistory, measure: SpendMeasure): number =>
  history.buckets.reduce((tallest, b) => Math.max(tallest, measureValue(b.totals, measure)), 0);

/** The top of the scale, drawn above the bars so a shape has a size. */
export const peakLabel = (history: WorkLogHistory, measure: SpendMeasure): string =>
  measurePhrase(peak(history, measure), measure);

/**
 * One bar per bucket, in the order the server sent them - **the empty ones
 * included and never filtered out**.
 *
 * A range whose every bucket is zero has no tallest bar to scale against, and
 * `fraction` is `0` there rather than `NaN` or `Infinity`: a flat range draws as
 * a flat range, which is a measurement, and not as a broken axis.
 */
export function bars(history: WorkLogHistory, measure: SpendMeasure, locale?: string): SpendBar[] {
  const tallest = peak(history, measure);

  return history.buckets.map((bucket) => {
    const value = measureValue(bucket.totals, measure);

    return {
      start: bucket.start,
      end: bucket.end,
      value,
      fraction: tallest > 0 ? value / tallest : 0,
      label: `${bucketLabel(bucket.start, history.bucket, locale)} — ${measurePhrase(value, measure)}`,
    };
  });
}

/** A bucket's own moment: the hour for an hourly bucket, the day for a daily
    one. Nothing gains from reading `00:00` under every bar of a month. */
export function bucketLabel(start: string, bucket: WorkLogHistory['bucket'], locale?: string): string {
  const at = new Date(start);
  const day = at.toLocaleDateString(locale, { weekday: 'short', day: 'numeric', month: 'short' });

  if (bucket === 'day') return day;

  return `${day}, ${at.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })}`;
}

/** The size the server chose, named from the answer rather than from the
    request - which is the whole of the graph saying what it is drawing. */
export const bucketPhrase = (history: WorkLogHistory): string =>
  history.bucket === 'day' ? 'by day' : 'by hour';

/** The window as it came back, snapped outward onto the bucket grid. Labelled
    from the answer, so a hand-typed range that the server widened reads as a
    wider window rather than as a disagreement with the table. */
export const spanPhrase = (history: WorkLogHistory, locale?: string): string =>
  `${dayLabel(history.from, locale)} to ${dayLabel(history.to, locale)}`;

/** The SVG's accessible name. The marks themselves say nothing to a screen
    reader, so the whole picture is said in one sentence. */
export const graphLabel = (history: WorkLogHistory, measure: SpendMeasure, locale?: string): string =>
  `Spend ${bucketPhrase(history)}, ${spanPhrase(history, locale)}, ${measureLabel(measure)}, ` +
  `peaking at ${peakLabel(history, measure)}`;

/**
 * Whether there is anything to draw an axis over at all.
 *
 * `firstSessionAt` and not the buckets: a range in which nothing ran is a flat
 * graph and is drawn, because a flat range is a measurement. A log that has
 * never been written is a sentence.
 */
export const stillGathering = (history: WorkLogHistory): boolean => history.firstSessionAt === null;
