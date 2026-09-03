import { Link } from 'react-router-dom';
import { Card, EmptyState, Grid, PageHeader, Text } from '@aerie/ui';
import { api } from '../api/client';
import { formatMetric, metricLabel, when } from '../lib/format';
import { useAsync, usePoll } from '../lib/useAsync';
import { Provenance } from '../components/Provenance';
import type { StrategyCard } from '../types';

/**
 * The top-level interaction from the ask: every strategy, what it can be swept
 * over, what its best honest result is, and whether it is trading.
 *
 * **A strategy with no headline number says so rather than showing its best
 * in-sample run.** That is the whole design of this screen. The tempting
 * version puts the highest Sharpe from anywhere on the card, and it would be
 * the luckiest member of the widest sweep - the exact number the honesty layer
 * exists to keep off a front page. So the card shows the walk-forward result
 * or it shows a sentence saying there is not one yet, which on a cold boot is
 * true for a minute or two while the workers get to it.
 */
export function StrategiesPage() {
  const { data, error, loading, reload } = useAsync(() => api.strategies(), []);
  const working = (data ?? []).some((card) => card.runs.queued > 0 || card.runs.running > 0);
  usePoll(reload, working);

  return (
    <>
      <PageHeader
        title="Strategies"
        description="What this build ships, what each can be swept over, and the best result each has that is measured on data it was not fitted to."
      />

      {error && <Text tone="danger">{error}</Text>}
      {!data && loading && <Text tone="muted">Loading…</Text>}

      {data && data.length === 0 && (
        <EmptyState message="No strategies are registered. The seed job registers what this build ships on every deploy." />
      )}

      {data && data.length > 0 && (
        <Grid cols={2}>
          {data.map((card) => (
            <StrategyTile key={card.name} card={card} />
          ))}
        </Grid>
      )}
    </>
  );
}

function StrategyTile({ card }: { card: StrategyCard }) {
  const swept = card.parameters.filter((parameter) => parameter.swept);
  const combinations = swept.reduce((total, parameter) => total * (parameter.count ?? 1), 1);

  return (
    <Card>
      <div className="tr-tile-head">
        <h2 className="tr-tile-title">
          <Link to={`/strategies/${encodeURIComponent(card.name)}`}>{card.name}</Link>
        </h2>
        {!card.shipped && (
          <Text tone="danger" as="span">
            not in this build
          </Text>
        )}
      </div>

      <Text tone="muted">{card.description}</Text>

      <dl className="tr-tile-facts">
        <div>
          <dt>Parameter space</dt>
          <dd>
            {swept.length === 0
              ? 'nothing to sweep'
              : `${swept.map((parameter) => parameter.name).join(', ')} — ${combinations.toLocaleString()} combinations`}
          </dd>
        </div>
        <div>
          <dt>Runs</dt>
          <dd>
            {card.runs.succeeded.toLocaleString()} done
            {card.runs.queued + card.runs.running > 0 &&
              `, ${(card.runs.queued + card.runs.running).toLocaleString()} in flight`}
            {card.runs.failed > 0 && `, ${card.runs.failed.toLocaleString()} failed`}
          </dd>
        </div>
        <div>
          <dt>Last result</dt>
          <dd>{when(card.last_finished_at)}</dd>
        </div>
        <div>
          <dt>Live</dt>
          {/* Stated rather than left to be inferred from an absence: nothing
              in this build trades live, and a card with no such line would
              leave a reader to guess which of these is running money. */}
          <dd>{card.live === 'backtest_only' ? 'not trading — backtests only' : card.live}</dd>
        </div>
      </dl>

      {card.best ? (
        <div className="tr-tile-best">
          <div className="tr-tile-best-head">
            <span>Best walk-forward result</span>
            <Provenance source={card.best.source} />
          </div>
          <div className="tr-tile-best-figures">
            {Object.entries(card.best.figures.headline)
              .filter(([name]) => name === 'sharpe' || name === 'total_return')
              .map(([name, value]) => (
                <div key={name}>
                  <span className="tr-tile-metric">{metricLabel(name)}</span>
                  <strong>{formatMetric(name, value)}</strong>
                </div>
              ))}
          </div>
          <Link to={`/runs/${card.best.id}`}>Run #{card.best.id}</Link>
        </div>
      ) : (
        <Text tone="muted" className="tr-tile-best">
          No walk-forward result yet, so this strategy has no number that may be called performance.
        </Text>
      )}
    </Card>
  );
}
