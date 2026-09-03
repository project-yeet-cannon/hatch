import { Badge } from '@aerie/ui';
import type { RunRow, Source } from '../types';

/**
 * Which source a result's numbers came from.
 *
 * docs/plans/trading.md Phase 7: *"Data provenance is visible on every result -
 * a run, a leaderboard row and a strategy's headline number all show which
 * source they came from, synthetic or Schwab. Not a footnote on a settings
 * page. For as long as the app ships seeded with synthetic results, the
 * difference between a number that means something and a number that means
 * nothing is exactly this field, and a UI that hides it is a UI that lies by
 * omission."*
 *
 * So this component exists to be used everywhere a figure is, and it renders
 * the *name the row carries* rather than a lookup: a source this build has
 * never heard of still shows its own name, because the alternative - a label
 * table with two entries in it - is how a third provider becomes an unlabelled
 * badge.
 *
 * The tone is deliberately not `success`/`danger`. Synthetic data is not a
 * warning and real data is not an achievement; what matters is that the reader
 * can tell them apart, which a name does and a colour does not.
 */
export function Provenance({ source }: { source: Source }) {
  return (
    <Badge tone="muted" title={source.description ?? undefined}>
      {source.name}
    </Badge>
  );
}

/**
 * Whether a figure was measured on data its parameters were chosen on.
 *
 * The other half of the honesty layer made visible. The server will not serve
 * an in-sample figure in a field labelled as performance
 * (`honesty/presentation.py`), so this badge is not the guard - it is the
 * label on the guard's output, and it ships visible by default rather than
 * behind a toggle.
 */
export function SampleBadge({ figures }: { figures: RunRow['figures'] }) {
  return figures.sample === 'out_of_sample' ? (
    <Badge tone="success" title="Measured on a window this run's parameters were not chosen on.">
      out of sample
    </Badge>
  ) : (
    <Badge
      tone="muted"
      title="Measured on the same window the parameters were fitted on. Not a performance claim."
    >
      in sample
    </Badge>
  );
}

/** Queued, running, succeeded, failed, cancelled - the queue's own words. */
export function StatusBadge({ status }: { status: RunRow['status'] }) {
  const tone =
    status === 'succeeded' ? 'success' : status === 'failed' ? 'danger' : status === 'running' ? 'primary' : 'muted';
  return <Badge tone={tone}>{status}</Badge>;
}

/** The mark that says a row arrived with the install rather than being asked
    for. Only shown when true: "not seeded" is not a fact worth a pill. */
export function SeededBadge({ row }: { row: Pick<RunRow, 'seeded'> }) {
  if (!row.seeded) return null;
  return (
    <Badge tone="muted" title="Enqueued by the seed job when this install was deployed.">
      seeded
    </Badge>
  );
}
