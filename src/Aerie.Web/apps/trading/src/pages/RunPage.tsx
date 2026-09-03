import { Link, useParams } from 'react-router-dom';
import { Card, PageHeader, Table, Text } from '@aerie/ui';
import { api } from '../api/client';
import { day, decimal, describeParams, duration, money, when } from '../lib/format';
import { useAsync, usePoll } from '../lib/useAsync';
import { EquityChart } from '../components/EquityChart';
import { FigureList } from '../components/FigureList';
import { Provenance, SampleBadge, SeededBadge, StatusBadge } from '../components/Provenance';

/**
 * *"Trades, the equity curve, the metrics, and the exact parameters and
 * revision, so a result can be reproduced."*
 *
 * The last clause is what the "How this was produced" block is for. A result is
 * reproducible from its row, and the row's reproducing fields are the build
 * that computed it, the fingerprint of the bars it read, and the fingerprint of
 * what it produced - so two runs that disagree on the last while agreeing on
 * the first two are a determinism bug, visible from this page without a query.
 */
export function RunPage() {
  const { id = '' } = useParams();
  const runId = Number(id);
  const { data, error, loading, reload } = useAsync(() => api.run(runId), [runId]);
  const moving = data?.run.status === 'queued' || data?.run.status === 'running';
  usePoll(reload, Boolean(moving));

  if (error && !data) return <Text tone="danger">{error}</Text>;
  if (!data) return <Text tone="muted">{loading ? 'Loading…' : 'Nothing to show.'}</Text>;

  const { run, sweep, trades, curve, folds } = data;

  return (
    <>
      <PageHeader
        title={`Run #${run.id}`}
        description={`${run.strategy} over ${run.symbols.join(', ')} at ${run.interval}, ${day(run.window_start)} to ${day(run.window_end)}.`}
      />

      <div className="tr-badges">
        <StatusBadge status={run.status} />
        <SampleBadge figures={run.figures} />
        <Provenance source={run.source} />
        <SeededBadge row={run} />
        {run.kind === 'walk_forward' && <span className="tr-kind">walk-forward</span>}
      </div>

      {run.error && <Text tone="danger">{run.error}</Text>}

      <Card>
        <h2 className="tr-section-title">Equity</h2>
        <EquityChart curve={curve} startingCash={run.starting_cash} />
      </Card>

      <Card>
        <FigureList figures={run.figures} />
      </Card>

      <Card>
        <h2 className="tr-section-title">How this was produced</h2>
        <dl className="tr-facts">
          <Fact label="Parameters" value={describeParams(run.params)} />
          <Fact label="Universe" value={run.symbols.join(', ')} />
          <Fact label="Interval" value={run.interval} />
          <Fact label="Window" value={`${day(run.window_start)} – ${day(run.window_end)}`} />
          <Fact
            label="Held out"
            value={
              run.figures.out_of_sample_start
                ? `${day(run.figures.out_of_sample_start)} – ${day(run.figures.out_of_sample_end)}`
                : 'nothing — every figure here was fitted'
            }
          />
          <Fact label="Starting cash" value={money(run.starting_cash)} />
          <Fact label="Costs" value={Object.values(run.costs).join(' · ')} />
          <Fact label="Data source" value={run.source.name} />
          <Fact label="Build" value={run.aerie_revision ?? '—'} mono />
          <Fact label="Data fingerprint" value={run.data_fingerprint ?? '—'} mono />
          <Fact label="Result fingerprint" value={run.result_fingerprint ?? '—'} mono />
          <Fact label="Bars" value={run.bars?.toLocaleString() ?? '—'} />
          <Fact label="Took" value={duration(run.duration_ms)} />
          <Fact label="Finished" value={when(run.finished_at)} />
          <Fact
            label="Sweep"
            value={
              sweep ? (
                <Link to={`/sweeps/${sweep.id}`}>
                  {sweep.name} ({sweep.trials.toLocaleString()} trials)
                </Link>
              ) : (
                'launched on its own'
              )
            }
          />
        </dl>
      </Card>

      {folds.length > 0 && (
        <Card>
          <h2 className="tr-section-title">What each fold chose</h2>
          <Text tone="muted">
            Parameters are picked on each train window and scored on the test window that follows it. The
            account is carried from one fold to the next, so the equity above is one continuous curve rather
            than a stitching of separate ones — and a fold boundary is a liquidation and re-entry charged
            nothing.
          </Text>
          <Table scroll>
            <thead>
              <tr>
                <th scope="col">Fold</th>
                <th scope="col">Trained on</th>
                <th scope="col">Tested on</th>
                <th scope="col">Chose</th>
                <th scope="col" className="tr-numeric">Candidates</th>
                <th scope="col" className="tr-numeric">Train score</th>
                <th scope="col" className="tr-numeric">Started</th>
                <th scope="col" className="tr-numeric">Ended</th>
              </tr>
            </thead>
            <tbody>
              {folds.map((fold) => (
                <tr key={fold.fold}>
                  <th scope="row">{fold.fold + 1}</th>
                  <td>{day(fold.train_start)} – {day(fold.train_end)}</td>
                  <td>{day(fold.test_start)} – {day(fold.test_end)}</td>
                  <td className="tr-params">{describeParams(fold.params)}</td>
                  <td className="tr-numeric">{fold.candidates}</td>
                  <td className="tr-numeric">{decimal(fold.train_objective)}</td>
                  <td className="tr-numeric">{money(fold.starting_cash)}</td>
                  <td className="tr-numeric">{money(fold.ending_equity)}</td>
                </tr>
              ))}
            </tbody>
          </Table>
        </Card>
      )}

      <Card>
        <h2 className="tr-section-title">Blotter</h2>
        {trades.length === 0 ? (
          <Text tone="muted">This run placed no trades.</Text>
        ) : (
          <Table scroll>
            <thead>
              <tr>
                <th scope="col">#</th>
                <th scope="col">Filled</th>
                <th scope="col">Symbol</th>
                <th scope="col" className="tr-numeric">Quantity</th>
                <th scope="col" className="tr-numeric">Price</th>
                {/* The bar's own price beside the fill price is where a cost
                    model becomes visible in the blotter rather than only in a
                    metric: the difference is the slippage that was charged. */}
                <th scope="col" className="tr-numeric">Bar price</th>
                <th scope="col" className="tr-numeric">Commission</th>
                <th scope="col" className="tr-numeric">Realized</th>
                <th scope="col">Why</th>
              </tr>
            </thead>
            <tbody>
              {trades.map((trade) => (
                <tr key={trade.sequence}>
                  <td>{trade.sequence}</td>
                  <td>{when(trade.filled_at)}</td>
                  <td>{trade.symbol}</td>
                  <td className="tr-numeric">{trade.quantity.toLocaleString()}</td>
                  <td className="tr-numeric">{money(trade.price)}</td>
                  <td className="tr-numeric">{money(trade.reference_price)}</td>
                  <td className="tr-numeric">{money(trade.commission)}</td>
                  <td className="tr-numeric">{money(trade.realized_pnl)}</td>
                  <td>{trade.tag || '—'}</td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
    </>
  );
}

function Fact({ label, value, mono }: { label: string; value: React.ReactNode; mono?: boolean }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd className={mono ? 'tr-mono' : undefined}>{value}</dd>
    </div>
  );
}
