import { Link, useParams } from 'react-router-dom';
import { Card, PageHeader, Table, Text } from '@aerie/ui';
import { api } from '../api/client';
import { when } from '../lib/format';
import { useAsync, usePoll } from '../lib/useAsync';
import { RunTable } from '../components/RunTable';
import { SweepLauncher } from '../components/SweepLauncher';
import type { SweepRow } from '../types';

/**
 * One strategy: its parameter ranges, a sweep launcher, and the runs that came
 * out of it.
 *
 * The parameter table is read straight off the strategy's own model on the
 * server, which is what the launcher walks. That matters more than it looks: a
 * range shown here that differed from the one the model validates against
 * would be a form offering values the strategy then refuses, one worker at a
 * time, a minute after the sweep was launched.
 */
export function StrategyPage() {
  const { name = '' } = useParams();
  const { data, error, loading, reload } = useAsync(() => api.strategy(name), [name]);
  const working = (data?.strategy.runs.queued ?? 0) + (data?.strategy.runs.running ?? 0) > 0;
  usePoll(reload, working);

  if (error && !data) return <Text tone="danger">{error}</Text>;
  if (!data) return <Text tone="muted">{loading ? 'Loading…' : 'Nothing to show.'}</Text>;

  const { strategy, sweeps, runs } = data;

  return (
    <>
      <PageHeader title={strategy.name} description={strategy.description} />
      {error && <Text tone="danger">{error}</Text>}

      <Card>
        <h2 className="tr-section-title">Parameter space</h2>
        <Text tone="muted">
          A parameter with a declared range is one a sweep can walk. One without is configuration for a
          run — which symbol, which mode — and sweeping it would be searching over the question rather
          than the answer.
        </Text>
        <Table>
          <thead>
            <tr>
              <th scope="col">Parameter</th>
              <th scope="col">Default</th>
              <th scope="col">Range</th>
              <th scope="col">Step</th>
              <th scope="col" className="tr-numeric">Values</th>
              <th scope="col">What it is</th>
            </tr>
          </thead>
          <tbody>
            {strategy.parameters.map((parameter) => (
              <tr key={parameter.name}>
                <th scope="row">{parameter.name}</th>
                <td>{parameter.default ?? '—'}</td>
                <td>{parameter.swept ? `${parameter.low} – ${parameter.high}` : 'not swept'}</td>
                <td>{parameter.step ?? '—'}</td>
                <td className="tr-numeric">{parameter.count ?? '—'}</td>
                <td>{parameter.description || '—'}</td>
              </tr>
            ))}
          </tbody>
        </Table>
      </Card>

      {strategy.shipped ? (
        <SweepLauncher strategy={strategy} onLaunched={reload} />
      ) : (
        <Card>
          <Text tone="muted">
            This build does not ship {strategy.name}, so it cannot be swept. Its results are kept because a
            leaderboard from a build that dropped a strategy still has to know what it was.
          </Text>
        </Card>
      )}

      <Card>
        <h2 className="tr-section-title">Sweeps</h2>
        {sweeps.length === 0 ? (
          <Text tone="muted">Nothing has been swept yet.</Text>
        ) : (
          <Table scroll>
            <thead>
              <tr>
                <th scope="col">Sweep</th>
                <th scope="col">Launched</th>
                <th scope="col">Trials</th>
                <th scope="col">Progress</th>
                <th scope="col">State</th>
              </tr>
            </thead>
            <tbody>
              {sweeps.map((sweep) => (
                <SweepLine key={sweep.id} sweep={sweep} />
              ))}
            </tbody>
          </Table>
        )}
      </Card>

      <Card>
        <h2 className="tr-section-title">Recent runs</h2>
        <RunTable rows={runs} empty="No runs yet." />
      </Card>
    </>
  );
}

function SweepLine({ sweep }: { sweep: SweepRow }) {
  return (
    <tr>
      <td>
        <Link to={`/sweeps/${sweep.id}`}>{sweep.name}</Link>
        {sweep.seeded && <span className="tr-kind"> seeded</span>}
      </td>
      <td>{when(sweep.created_at)}</td>
      {/* The grid, not the row count: the walk-forward run beside it is
          machinery rather than a trial, and counting it here would disagree
          with the number the selection haircut was computed against. */}
      <td className="tr-numeric">{sweep.trials.toLocaleString()}</td>
      <td>
        {sweep.progress.finished.toLocaleString()} / {sweep.progress.total_runs.toLocaleString()}
      </td>
      <td>
        {sweep.cancelled_at
          ? 'cancelled'
          : sweep.progress.complete
            ? 'complete'
            : `${(sweep.progress.by_status.running ?? 0).toLocaleString()} running`}
      </td>
    </tr>
  );
}
