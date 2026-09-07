/* Everything the leaderboard could be wrong about: the ranges it offers, what
   it reads out of the URL, the podium it takes off a ranked list, and the four
   sentences it says.

   Apart from the page the way workLog.ts is apart from the work log section and
   utilization.ts is apart from the battery. This app has no DOM test setup and
   the leaderboard does not add one; the house pattern instead is that the
   arithmetic and the prose live here with their tests beside them, and the
   component is left thin enough to read.

   Two of these carry a decision rather than a formula, and they are the reason
   the file exists rather than being three helpers inside the page:

   - The presets are computed onto **the server's own bucket grid**, because
     `/work-log/history` snaps a range outward onto that grid before it draws.
     A range already on the grid is what keeps the graph's total and the table's
     total describing one window; a range half an hour off it makes the graph
     legitimately wider than the table, which reads as a disagreement.
   - "Nothing has ever been logged here" and "nothing ran in this window" are
     two different sentences, told apart by `firstSessionAt` rather than by a
     row count - and the page says whichever is true once, at the top, rather
     than three times in three empty sections. */

import type { SessionSort, WorkLogSession, WorkLogSessions, WorkLogTotals } from '../types';

/** How many sessions the ranking draws: enough to be a podium, few enough to
    read before scrolling. The table below it is where the rest lives. */
export const PODIUM = 5;

/** How many rows the table asks for. The endpoint refuses more than 500, and
    the totals cover the whole filter either way - so a capped table still adds
    up honestly and says that it is capped. */
export const TABLE_LIMIT = 100;

/** The URL's whole vocabulary. `measure` belongs to the graph and is read here
    with the rest of it - the URL is one state, and one file resolves it. */
export const RANGE = 'range';
export const ISSUE = 'issue';
export const SORT = 'sort';
export const FROM = 'from';
export const TO = 'to';
export const MEASURE = 'measure';

/** Which of the two figures the graph draws. They differ by six orders of
    magnitude and never share an axis - a second y-axis would draw two lies
    crossing - so the graph shows one at a time with a control between them.

    The type and its parser live here rather than in lib/spend.ts because this
    is the file that owns the URL's vocabulary, and lib/spend.ts reads from here
    rather than the other way round. */
export type SpendMeasure = 'tokens' | 'cost';

/** Tokens, per AERIE-735 decision 2: on a subscription the dollars are notional
    list price rather than money that left an account. */
export const DEFAULT_MEASURE: SpendMeasure = 'tokens';

export type RangePreset = '24h' | 'today' | '7d' | '14d' | '30d';

/** Fourteen nights: enough to see a shape, and the same default the server
    takes when a caller names no range at all. */
export const DEFAULT_PRESET: RangePreset = '14d';

export const DEFAULT_SORT: SessionSort = 'tokens';

/** In the order they are drawn, shortest first. */
export const PRESETS: { key: RangePreset; label: string }[] = [
  { key: '24h', label: 'Last 24 hours' },
  { key: 'today', label: 'Today' },
  { key: '7d', label: 'Last 7 days' },
  { key: '14d', label: 'Last 14 days' },
  { key: '30d', label: 'Last 30 days' },
];

const HOUR = 3_600_000;
const DAY = 24 * HOUR;

/** How many days back each daily preset reaches. `today` is the day itself. */
const SPAN: Record<Exclude<RangePreset, '24h'>, number> = {
  today: 1,
  '7d': 7,
  '14d': 14,
  '30d': 30,
};

/**
 * A preset as two instants, **on the server's bucket grid**.
 *
 * The grid is a whole day for the four daily presets and a whole hour for
 * `24h`, which is exactly what `WorkLogRollup.Align` uses: a daily bucket is
 * shifted to the reader's midnight and an hourly one is not, because every
 * whole-hour zone lands on the same hourly grid anyway.
 *
 * `to` is the first grid boundary **strictly after** `now`, so the last bucket
 * is the one running now, drawn as far as it has got. `from` is that boundary
 * minus the span.
 *
 * @param offsetMinutes Minutes east of UTC - `-new Date().getTimezoneOffset()`,
 *   the form `getNextWorkUnder` already sends and the value the graph passes to
 *   `history` as well, so the grid the page aligned to and the grid the server
 *   buckets on are one grid.
 */
export function presetRange(
  preset: RangePreset,
  now: Date,
  offsetMinutes: number,
): { from: string; to: string } {
  const length = preset === '24h' ? HOUR : DAY;
  const shift = preset === '24h' ? 0 : offsetMinutes * 60_000;

  // Strictly after: `Math.floor(x) + 1` rather than `Math.ceil(x)`, so an
  // instant that lands exactly on a boundary still gets the bucket it starts.
  const to = (Math.floor((now.getTime() + shift) / length) + 1) * length - shift;
  const span = preset === '24h' ? DAY : SPAN[preset] * DAY;

  return { from: new Date(to - span).toISOString(), to: new Date(to).toISOString() };
}

/** What the page is looking at: one window, one filter, one sort. */
export interface LeaderboardQuery {
  from: string;
  to: string;
  /** Null when `from`/`to` were typed into the URL rather than picked, which is
      the one case where no preset button is pressed. */
  preset: RangePreset | null;
  issue: string | null;
  sort: SessionSort;
  /** The graph's, and in the URL with the rest for the same reason the sort is:
      a pasted link that opens on dollars is the same argument as a pasted
      sort. */
  measure: SpendMeasure;
}

/**
 * The URL, read into the query both reads take.
 *
 * **It never refuses.** A `range` or a `sort` nobody recognises falls back to
 * the default rather than reaching the server to be 400ed - a pasted link with
 * a typo in it should open the page, not an error.
 *
 * A `from` **and** a `to` both present win over `range` and are passed through
 * exactly as typed, which is what makes a pasted window a paste. One of them
 * alone is ignored: half a window is not a window, and guessing the other half
 * would silently show something nobody asked for.
 */
export function resolveQuery(params: URLSearchParams, now: Date, offsetMinutes: number): LeaderboardQuery {
  const issue = params.get(ISSUE)?.trim() || null;
  const sort = parseSort(params.get(SORT));
  const measure = parseMeasure(params.get(MEASURE));

  const from = params.get(FROM)?.trim();
  const to = params.get(TO)?.trim();
  if (from && to) return { from, to, preset: null, issue, sort, measure };

  const preset = parsePreset(params.get(RANGE));

  return { ...presetRange(preset, now, offsetMinutes), preset, issue, sort, measure };
}

export const parsePreset = (raw: string | null): RangePreset =>
  PRESETS.some((p) => p.key === raw) ? (raw as RangePreset) : DEFAULT_PRESET;

export const parseSort = (raw: string | null): SessionSort =>
  raw === 'tokens' || raw === 'cost' || raw === 'ended' ? raw : DEFAULT_SORT;

export const parseMeasure = (raw: string | null): SpendMeasure =>
  raw === 'tokens' || raw === 'cost' ? raw : DEFAULT_MEASURE;

/**
 * The podium, off a list the server already ranked.
 *
 * **It never re-orders.** The rows arrive in the order the endpoint put them
 * in, and re-sorting a capped hundred of them in the browser would rank a
 * sample and call it a leaderboard - which is why the podium is its own read,
 * pinned to the top five by tokens, rather than a slice of the table's.
 */
export const rankedTop = (sessions: WorkLogSession[], n: number = PODIUM): WorkLogSession[] =>
  sessions.slice(0, n);

/** The house's voice under **Top billing**: `The 5 most expensive sessions of
    the 31 that ran in this window.` Singular at one, because "1 sessions" is
    what makes a page look unfinished. */
export function rankingCaption(totals: WorkLogTotals, shown: number): string {
  const ran = `${totals.sessions} that ran in this window`;

  if (shown === 1) return `The most expensive session of the ${ran}.`;

  return `The ${shown} most expensive sessions of the ${ran}.`;
}

/** How many of how many the table is drawing, or null when it is drawing all of
    them. It is what makes a total larger than the visible rows read as a cap
    rather than as an error. */
export function capPhrase(totals: WorkLogTotals, shown: number): string | null {
  if (totals.sessions <= shown) return null;

  return `Showing the top ${shown} of ${totals.sessions} sessions; the totals above are for all of them.`;
}

/**
 * Which of the two empty states is true, or null when neither is.
 *
 * Both are answered by `firstSessionAt`, which ignores the range and respects
 * the issue filter - so the page can tell them apart without fetching the
 * graph's data to do it. `never` wins when both could speak, and the page says
 * it once at the top rather than in each section.
 */
export function emptiness(
  view: WorkLogSessions,
  issueKey: string | null,
  locale?: string,
): { kind: 'never' | 'range'; text: string } | null {
  if (view.firstSessionAt === null) {
    return {
      kind: 'never',
      text:
        `Nothing has ever been logged ${issueKey ? `under ${issueKey}` : 'here'}. ` +
        'A row lands here at the end of every unattended increment.',
    };
  }

  if (view.totals.sessions > 0) return null;

  // The span the log does cover, so "empty" reads as a window chosen badly
  // rather than as a feature that does not work.
  const last = view.lastSessionAt ?? view.firstSessionAt;

  return {
    kind: 'range',
    text:
      'No session ran in this window. ' +
      `The log runs from ${dayLabel(view.firstSessionAt, locale)} to ${dayLabel(last, locale)}.`,
  };
}

/** A day, short: `19 Aug`. The optional locale exists so the tests can pin one;
    the page passes none and gets the reader's. */
export const dayLabel = (iso: string, locale?: string): string =>
  new Date(iso).toLocaleDateString(locale, { day: 'numeric', month: 'short' });

/** The window a view covers, as drawn under the controls: `19 Aug – 7 Sep`. */
export const windowPhrase = (from: string, to: string, locale?: string): string =>
  `${dayLabel(from, locale)} – ${dayLabel(to, locale)}`;

/** A session id, short enough for a table cell. The whole one goes in a
    `title`, so the cell is readable and the id is still copyable. */
export const shortSession = (sessionId: string): string => sessionId.slice(0, 8);
