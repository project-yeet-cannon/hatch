import { useState } from 'react';
import { Card, Field, PageHeader, Text } from '@aerie/ui';
import { api } from '../api/client';
import { metricLabel } from '../lib/format';
import { useAsync, usePoll } from '../lib/useAsync';
import { RunTable } from '../components/RunTable';

/**
 * *"The leaderboard answers 'what did best today' in one glance"* - the ask's
 * own success criterion.
 *
 * Three controls, and each is one of the plan's own slices: what window, what
 * kind of figure, and backtest or live. The fourth control - what to sort on -
 * is the leaderboard being *"sortable across everything"*.
 *
 * **The default is the honest board.** `sample=out_of_sample` is what loads,
 * so the first thing anybody sees is the set of runs whose figures may be
 * called performance at all. The in-sample board is one control away and is
 * labelled on every row, because hiding it would hide the divergence between
 * the two - which is the thing worth looking at - but it is not what the page
 * opens on.
 */
const SORTS = ['sharpe', 'total_return', 'annualized_return', 'sortino', 'deflated_sharpe', 'excess_return'];

const SAMPLES: { value: string; label: string; note: string }[] = [
  {
    value: 'out_of_sample',
    label: 'Walk-forward only',
    note: 'Runs measured on windows their parameters were not chosen on. The only figures here that are a performance claim.',
  },
  {
    value: 'in_sample',
    label: 'Fitted runs',
    note: 'Every figure below was measured on the same data its parameters were chosen on. Read them as descriptions, not as results.',
  },
  {
    value: 'all',
    label: 'Everything',
    note: 'Both kinds, mixed. The badge in each row says which window that row was measured on.',
  },
];

const WINDOWS: { value: string; label: string }[] = [
  { value: 'today', label: 'Today' },
  { value: 'week', label: 'This week' },
  { value: 'inception', label: 'Since inception' },
];

export function LeaderboardPage() {
  const [sort, setSort] = useState('sharpe');
  const [sample, setSample] = useState('out_of_sample');
  const [since, setSince] = useState('inception');
  const [mode, setMode] = useState('backtest');

  const { data, error, loading, reload } = useAsync(
    () => api.leaderboard({ sort, sample, since, mode, limit: 100 }),
    [sort, sample, since, mode],
  );
  const { data: queue, reload: reloadQueue } = useAsync(() => api.queue(), []);
  const working = (queue?.queued ?? 0) + (queue?.running ?? 0) > 0;
  usePoll(() => {
    reload();
    reloadQueue();
  }, working);

  const note = SAMPLES.find((entry) => entry.value === sample)?.note ?? '';

  return (
    <>
      <PageHeader
        title="Leaderboard"
        description="Every finished run, sortable across everything, sliced by when it finished and by whether its figures were fitted."
      />

      <Card>
        <div className="tr-filters">
          <Field label="Sort by">
            <select value={sort} onChange={(event) => setSort(event.target.value)}>
              {SORTS.map((name) => (
                <option key={name} value={name}>
                  {metricLabel(name)}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Figures">
            <select value={sample} onChange={(event) => setSample(event.target.value)}>
              {SAMPLES.map((entry) => (
                <option key={entry.value} value={entry.value}>
                  {entry.label}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Finished">
            <select value={since} onChange={(event) => setSince(event.target.value)}>
              {WINDOWS.map((entry) => (
                <option key={entry.value} value={entry.value}>
                  {entry.label}
                </option>
              ))}
            </select>
          </Field>
          <Field label="Source of results">
            <select value={mode} onChange={(event) => setMode(event.target.value)}>
              <option value="backtest">Backtests</option>
              <option value="live">Live trading</option>
            </select>
          </Field>
        </div>
        <Text tone="muted">{note}</Text>
      </Card>

      {error && <Text tone="danger">{error}</Text>}
      {!data && loading && <Text tone="muted">Loading…</Text>}

      {/* The server's own sentence about a slice it can only answer with
          nothing - shown instead of an unexplained empty table. */}
      {data?.note && <Text tone="muted">{data.note}</Text>}

      {data && (
        <RunTable
          rows={data.rows}
          sort={data.sort}
          empty={
            data.mode === 'live'
              ? 'Nothing has traded live.'
              : 'No finished runs match this slice yet. A sweep launched a moment ago takes a minute to fill in.'
          }
        />
      )}
    </>
  );
}
