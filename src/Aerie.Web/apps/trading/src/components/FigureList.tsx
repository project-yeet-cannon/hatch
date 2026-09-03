import { Text } from '@aerie/ui';
import { betterWhenPositive, formatMetric, metricLabel } from '../lib/format';
import type { Figures } from '../types';

/**
 * One run's numbers, in the three groups the server put them in.
 *
 * The grouping is not this component's opinion - it arrives that way, from
 * `honesty/presentation.figures_for`, which is the only constructor any
 * serving code may use. What is decided here is only how the groups are
 * *introduced*, and each heading is written to say what the numbers under it
 * are rather than how good they are:
 *
 * - **Performance** appears only for a run whose window was held out. The
 *   server never populates `headline` for anything else, so this section is
 *   absent rather than empty on an ordinary sweep run.
 * - **Fitted figures** is the same set of metrics over a window the parameters
 *   were chosen on. It is shown, not withheld - hiding it would hide the
 *   divergence between the two, which is the thing worth looking at - under a
 *   name that says what it is.
 * - **What it did** is everything descriptive: exposure, turnover, trade
 *   count, drawdown. Honest from either window, which is why the server
 *   serves them regardless.
 */
export function FigureList({ figures }: { figures: Figures }) {
  const groups: { title: string; note: string; values: Record<string, string> }[] = [];

  if (Object.keys(figures.headline).length > 0) {
    groups.push({
      title: 'Performance',
      note: 'Measured on held-out data. These are the only figures on this page that are a performance claim.',
      values: figures.headline,
    });
  }
  if (Object.keys(figures.in_sample).length > 0) {
    groups.push({
      title: 'Fitted figures',
      note: 'Measured on the window these parameters were chosen on. Not a claim about how this would have done.',
      values: figures.in_sample,
    });
  }
  if (Object.keys(figures.descriptive).length > 0) {
    groups.push({
      title: 'What it did',
      note: 'Behaviour rather than performance: true of the run whichever window it covered.',
      values: figures.descriptive,
    });
  }

  if (groups.length === 0) {
    return <Text tone="muted">No numbers yet — this run has not finished.</Text>;
  }

  return (
    <div className="tr-figures">
      {groups.map((group) => (
        <section key={group.title} className="tr-figure-group">
          <h3 className="tr-figure-title">{group.title}</h3>
          <Text tone="muted" className="tr-figure-note">
            {group.note}
          </Text>
          <dl className="tr-figure-grid">
            {Object.entries(group.values)
              .sort(([left], [right]) => metricLabel(left).localeCompare(metricLabel(right)))
              .map(([name, value]) => (
                <div key={name} className="tr-figure">
                  <dt>{metricLabel(name)}</dt>
                  <dd className={signClass(name, value)}>{formatMetric(name, value)}</dd>
                </div>
              ))}
          </dl>
        </section>
      ))}
    </div>
  );
}

/**
 * The one bit of colour a figure carries, and only where the sign means
 * something.
 *
 * A drawdown of 12% is not "bad news in red" - every strategy has one - so the
 * rule is about the *direction* the metric improves in, and metrics where the
 * question does not apply (a trade count, an exposure) stay in the resting
 * colour rather than being coloured by accident of sign.
 */
function signClass(name: string, value: string): string {
  const better = betterWhenPositive(name);
  if (better === undefined) return 'tr-figure-value';
  const parsed = Number(value);
  if (!Number.isFinite(parsed) || parsed === 0) return 'tr-figure-value';
  const good = better ? parsed > 0 : parsed < 0;
  return `tr-figure-value ${good ? 'tr-good' : 'tr-bad'}`;
}
