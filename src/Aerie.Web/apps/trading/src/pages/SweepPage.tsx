import { Link, useParams } from 'react-router-dom';
import { Card, PageHeader, Text } from '@aerie/ui';
import { api } from '../api/client';
import { when } from '../lib/format';
import { useAsync, usePoll } from '../lib/useAsync';
import { RunTable } from '../components/RunTable';

/**
 * One batch, and the runs it produced.
 *
 * Where a launch lands, which is why it polls: a sweep opened a second after
 * it was enqueued is a page of queued rows, and the interesting thing about it
 * is watching them stop being queued. The poll stops when the batch is
 * complete, so a finished sweep is not a request every few seconds forever.
 */
export function SweepPage() {
  const { id = '' } = useParams();
  const sweepId = Number(id);
  const { data, error, loading, reload } = useAsync(() => api.sweep(sweepId), [sweepId]);
  const sweep = data?.sweeps[0];
  usePoll(reload, Boolean(sweep && !sweep.progress.complete && !sweep.cancelled_at));

  if (error && !data) return <Text tone="danger">{error}</Text>;
  if (!data || !sweep) return <Text tone="muted">{loading ? 'Loading…' : 'Nothing to show.'}</Text>;

  const running = sweep.progress.by_status.running ?? 0;
  const failed = sweep.progress.by_status.failed ?? 0;

  return (
    <>
      <PageHeader
        title={sweep.name}
        description={`${sweep.trials.toLocaleString()} parameter set(s) of ${sweep.strategy}, launched ${when(sweep.created_at)}.`}
      />

      <Card>
        <dl className="tr-facts">
          <div>
            <dt>Progress</dt>
            <dd>
              {sweep.progress.finished.toLocaleString()} of {sweep.progress.total_runs.toLocaleString()}{' '}
              finished{running > 0 && `, ${running.toLocaleString()} running`}
              {failed > 0 && `, ${failed.toLocaleString()} failed`}
            </dd>
          </div>
          <div>
            <dt>Strategy</dt>
            <dd>
              <Link to={`/strategies/${encodeURIComponent(sweep.strategy)}`}>{sweep.strategy}</Link>
            </dd>
          </div>
          <div>
            <dt>Origin</dt>
            {/* The seeded flag, said in words. It is the difference between a
                leaderboard somebody built and one that came in the box, and on
                a fresh install every row is the second kind. */}
            <dd>{sweep.seeded ? 'seeded with this install' : 'launched here'}</dd>
          </div>
          <div>
            <dt>State</dt>
            <dd>
              {sweep.cancelled_at
                ? `cancelled ${when(sweep.cancelled_at)} — runs already in flight were left to finish`
                : sweep.progress.complete
                  ? 'complete'
                  : 'working'}
            </dd>
          </div>
          <div>
            <dt>Build</dt>
            <dd className="tr-mono">{sweep.aerie_revision ?? '—'}</dd>
          </div>
        </dl>
        <Text tone="muted">
          The count above includes one walk-forward run beside the grid. It is not one of the trials — the
          selection accounting on each row is computed against {sweep.trials.toLocaleString()}, which is what
          the search actually tried.
        </Text>
      </Card>

      <RunTable rows={data.runs} empty="This sweep has no runs, which should not be possible." />
    </>
  );
}
