import { useCallback, useMemo } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Badge, Card, EmptyState, Field, PageHeader, Table } from '@hatch/ui';
import { getBoard, getWorkLogHistory, getWorkLogSessions } from '../api/client';
import { IssuePicker } from '../components/IssuePicker';
import { SpendGraph } from '../components/SpendGraph';
import {
  FROM,
  ISSUE,
  MEASURE,
  PODIUM,
  PRESETS,
  RANGE,
  SORT,
  TABLE_LIMIT,
  TO,
  capPhrase,
  emptiness,
  rankedTop,
  rankingCaption,
  resolveQuery,
  shortSession,
  windowPhrase,
} from '../lib/leaderboard';
import { useLoaded } from '../lib/useLoaded';
import {
  compactTokens,
  durationPhrase,
  entryMark,
  entryTitle,
  errorPhrase,
  moneyPhrase,
  totalsPhrase,
} from '../lib/workLog';
import type { Board, SessionSort, WorkLogHistory, WorkLogSession, WorkLogSessions } from '../types';

/** The podium, the table and the graph, arriving together so a half-drawn page
    is never a state - and so the three cannot end up describing different
    windows. */
interface LeaderboardView {
  podium: WorkLogSessions;
  table: WorkLogSessions;
  history: WorkLogHistory;
}

/** The three sortable columns, in the order they are drawn. */
const COLUMNS: { key: SessionSort; label: string }[] = [
  { key: 'tokens', label: 'Tokens' },
  { key: 'cost', label: 'Notional USD' },
  { key: 'ended', label: 'Ran' },
];

/**
 * What the nights actually cost, and on what.
 *
 * Every number on this page comes from `hatch.work_log_entries` - the row the
 * dispatcher writes at the end of every unattended increment, and the only data
 * in the house that knows *which ticket* the spend was for. Account-wide
 * headroom is a different question and is answered by the battery in the nav,
 * read live. **No Claude credential is anywhere in this path**, so a
 * leaderboard on an installation with no subscription token is the whole
 * leaderboard rather than a reduced one.
 *
 * **Nothing here writes.** There is no control that could and `api/client.ts`
 * has no function that could: a work log row is written by the dispatcher with
 * an API key and the server refuses a browser outright. The absence is the
 * guarantee - do not add one for symmetry.
 */
export function LeaderboardPage() {
  const [params, setParams] = useSearchParams();

  /* Minutes east of UTC, the form the server takes. Read once per render
     rather than held in state: it is a fact about the browser, and a page that
     cached it would be wrong for exactly as long as somebody left the tab open
     across a DST seam. */
  const offsetMinutes = -new Date().getTimezoneOffset();

  /* Keyed on the URL's own text rather than on `params`, which is a fresh
     object every render - depending on it directly would resolve a new range
     on each one and refetch forever. `now` is read inside, so the window is
     fixed until something actually changes. */
  const search = params.toString();
  const query = useMemo(
    () => resolveQuery(new URLSearchParams(search), new Date(), offsetMinutes),
    [search, offsetMinutes],
  );

  /* Two reads over one range and one ancestorKey, in one Promise.all. The
     podium is pinned to the top five by tokens and the table takes the sort
     from the URL, because a "top billing" list that turned into "the five most
     recent" when somebody sorted the table by time would be a different page
     wearing the same caption - and re-sorting the table's capped rows here
     would rank a sample. When the table happens to be sorted by tokens the two
     overlap, and that redundancy is bought deliberately: one behaviour in every
     sort beats a branch that only sometimes fetches. */
  const load = useCallback(async (): Promise<LeaderboardView> => {
    const window = { from: query.from, to: query.to, ancestorKey: query.issue };
    const [podium, table, history] = await Promise.all([
      getWorkLogSessions({ ...window, sort: 'tokens', limit: PODIUM }),
      getWorkLogSessions({ ...window, sort: query.sort, limit: TABLE_LIMIT }),
      /* No bucket size is sent: the server chooses it from the length of the
         range and names it back, and the graph labels its axis from that. The
         offset is the one the presets were computed on, so the daily grid the
         page aligned to and the daily grid the server buckets on are one
         grid. */
      getWorkLogHistory({ ...window, offsetMinutes }),
    ]);

    return { podium, table, history };
  }, [query, offsetMinutes]);

  const { data, error } = useLoaded<LeaderboardView>(load);

  /* Its own read, on the module-level `getBoard` whose reference is stable - so
     it loads once and on focus, and flipping a sort refetches the sessions
     rather than seven hundred cards. */
  const { data: board } = useLoaded<Board>(getBoard);

  function write(name: string, value: string) {
    const next = new URLSearchParams(params);
    if (value) next.set(name, value);
    else next.delete(name);
    // Replaced rather than pushed: flipping a control is not somewhere anybody
    // wants six presses of Back to walk them through.
    setParams(next, { replace: true });
  }

  function choosePreset(key: string) {
    const next = new URLSearchParams(params);
    next.set(RANGE, key);
    // A preset and a hand-typed window are two ways of saying the same thing,
    // and pressing one has to clear the other or nothing would happen.
    next.delete(FROM);
    next.delete(TO);
    setParams(next, { replace: true });
  }

  if (error && !data) return <p className="text-danger">{error}</p>;
  if (!data) return <p className="text-muted">Loading…</p>;

  const { podium, table, history } = data;
  const empty = emptiness(table, query.issue);
  const top = rankedTop(podium.sessions);
  const errors = errorPhrase(table.totals);
  const cap = capPhrase(table.totals, table.sessions.length);

  return (
    <div className="hatch-page">
      <PageHeader
        title="Leaderboard"
        description="What the nights cost, and on what. Every figure is the work log's own."
      />

      <Card>
        <div className="hatch-leaderboard-controls">
          <div className="hatch-leaderboard-presets" role="group" aria-label="Range">
            {PRESETS.map((preset) => (
              <button
                key={preset.key}
                type="button"
                className={`hatch-leaderboard-preset${query.preset === preset.key ? ' active' : ''}`}
                aria-pressed={query.preset === preset.key}
                onClick={() => choosePreset(preset.key)}
              >
                {preset.label}
              </button>
            ))}
          </div>

          {/* `as="div"` for the reason the issue page's parent field is: the
              picker carries its own accessible name, and a <label> wrapping the
              popup would read the whole candidate list out as the name of the
              control. */}
          <Field label="Issue" as="div" className="hatch-leaderboard-issue">
            <IssuePicker
              label="Issue"
              value={query.issue}
              candidates={board?.issues ?? []}
              placeholder="Everything"
              emptyMessage="Nothing is filed yet."
              /* The handler writes the URL and nothing else. There is nothing
                 to await here and nothing that can fail - the re-read is the
                 effect of the URL changing. */
              onChange={(key) => {
                write(ISSUE, key);
                return Promise.resolve();
              }}
            />
          </Field>

          {/* Labelled from what came back rather than from what was asked for,
              which is what a pasted window needs. */}
          <span className="hatch-leaderboard-window text-muted">{windowPhrase(table.from, table.to)}</span>
        </div>
      </Card>

      {/* Whichever is true, said once for the whole page rather than three
          times in three empty sections. */}
      {empty && <EmptyState message={empty.text} />}

      {/* Drawn on an empty range and not on an empty log: a window nobody
          worked is a row of zeroed buckets, which is a measurement, and a log
          that has never been written is a sentence. */}
      {empty?.kind !== 'never' && (
        <Card>
          <h2 className="hatch-section-title">Spend over time</h2>
          <SpendGraph
            history={history}
            measure={query.measure}
            onMeasure={(measure) => write(MEASURE, measure)}
          />
        </Card>
      )}

      {empty ? null : (
        <>
          <Card>
            <h2 className="hatch-section-title">Top billing</h2>
            <p className="hatch-section-hint text-muted">{rankingCaption(podium.totals, top.length)}</p>

            <ol className="hatch-leaderboard-podium">
              {top.map((session) => (
                <PodiumRow key={session.id} session={session} />
              ))}
            </ol>
          </Card>

          <Card>
            <h2 className="hatch-section-title">Every session</h2>
            <p className="hatch-leaderboard-totals">{totalsPhrase(table.totals)}</p>
            {errors && <p className="text-muted">{errors}.</p>}

            <Table scroll>
              <thead>
                <tr>
                  <th scope="col">Session</th>
                  <th scope="col">Issue</th>
                  {COLUMNS.map((column) => (
                    <th
                      key={column.key}
                      scope="col"
                      /* Descending is the only direction the endpoint offers
                         and the only one this asks for. */
                      aria-sort={query.sort === column.key ? 'descending' : 'none'}
                    >
                      <button
                        type="button"
                        className={`hatch-leaderboard-sort${query.sort === column.key ? ' active' : ''}`}
                        onClick={() => write(SORT, column.key)}
                      >
                        {column.label}
                      </button>
                    </th>
                  ))}
                  <th scope="col">Took</th>
                </tr>
              </thead>
              <tbody>
                {table.sessions.map((session) => (
                  <Row key={session.id} session={session} />
                ))}
              </tbody>
            </Table>

            {/* So a total larger than the visible rows reads as a cap rather
                than as an error. */}
            {cap && <p className="text-muted">{cap}</p>}
          </Card>
        </>
      )}
    </div>
  );
}

/** One ranked session: what it was run against, what it said it did, and what
    it cost - tokens first and the money after it, in that order every time. */
function PodiumRow({ session }: { session: WorkLogSession }) {
  const mark = entryMark(session);

  return (
    <li className="hatch-leaderboard-rank">
      <div className="hatch-leaderboard-rank-head">
        <strong className={session.described ? undefined : 'text-muted'}>{entryTitle(session)}</strong>
        {mark === 'error' && <Badge tone="danger">ended with an error</Badge>}
        {mark === 'undescribed' && <Badge>undescribed</Badge>}
      </div>

      <div className="hatch-leaderboard-rank-facts">
        <span>
          <strong>{compactTokens(session.totalTokens)}</strong> tokens
        </span>
        <span className="text-muted">{moneyPhrase(session.costUsd)}</span>
        <Link to={`/issues/${session.issueKey}`} className="hatch-leaderboard-key">
          {session.issueKey}
        </Link>
        <span className="text-muted">{session.issueTitle}</span>
      </div>
    </li>
  );
}

/** One table row. The order the server ranked them in, not re-derived here. */
function Row({ session }: { session: WorkLogSession }) {
  return (
    <tr>
      {/* Shortened to fit, with the whole one on the cell - so the column is
          readable and the id is still there to copy. */}
      <td title={session.sessionId}>
        <code>{shortSession(session.sessionId)}</code>
        {session.isError && <Badge tone="danger">error</Badge>}
      </td>
      <td>
        <Link to={`/issues/${session.issueKey}`}>{session.issueKey}</Link>
      </td>
      <td>{compactTokens(session.totalTokens)}</td>
      <td>{moneyPhrase(session.costUsd)}</td>
      <td>{new Date(session.endedAt).toLocaleString()}</td>
      <td>{durationPhrase(session.durationMs)}</td>
    </tr>
  );
}
