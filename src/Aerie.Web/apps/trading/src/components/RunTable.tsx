import { Link } from 'react-router-dom';
import { EmptyState, Table } from '@aerie/ui';
import { describeParams, formatMetric, metricLabel, when } from '../lib/format';
import type { RunRow } from '../types';
import { Provenance, SampleBadge, SeededBadge, StatusBadge } from './Provenance';

/**
 * Runs as rows, used by the leaderboard, the strategy screen and the sweep
 * screen alike.
 *
 * One table for all three because the server serves one row type for all
 * three, and for the same reason: a leaderboard that carried fewer columns
 * than a strategy's list of runs would be two shapes for one thing, and the
 * honesty columns are the ones that would go missing from the smaller one.
 *
 * **The honesty columns are not optional and are not behind a toggle.** Every
 * row states the window its figures came from, the source that produced its
 * data, and - where the server computed them - the selection-adjusted figure
 * and the baselines beside the headline. The plan's wording is that they
 * *"ship visible by default"*, and a column list that could be edited down to
 * hide them would be a way to un-ship them.
 */
export interface RunTableProps {
  rows: RunRow[];
  /** The metric the caller sorted on, shown as its own column so the reader
      can see the number the ordering is by. */
  sort?: string;
  /** What to say when there is nothing. Written by the caller because "no runs
      yet" and "nothing matched this filter" are different sentences. */
  empty: string;
}

/** Beside the sorted figure: the columns that qualify it. Their absence on a
    row is not a gap - a run with no sweep has no trial count - so each renders
    an em dash rather than being dropped. */
const QUALIFIERS = ['deflated_sharpe', 'excess_return', 'excess_return_index', 'stressed_sharpe'];

export function RunTable({ rows, sort = 'sharpe', empty }: RunTableProps) {
  if (rows.length === 0) return <EmptyState message={empty} />;

  return (
    <Table scroll>
      <thead>
        <tr>
          <th scope="col">Run</th>
          <th scope="col">Strategy</th>
          <th scope="col">Parameters</th>
          <th scope="col">Window</th>
          <th scope="col" className="tr-numeric">{metricLabel(sort)}</th>
          {QUALIFIERS.map((name) => (
            <th key={name} scope="col" className="tr-numeric">
              {metricLabel(name)}
            </th>
          ))}
          <th scope="col">Source</th>
          <th scope="col">Finished</th>
          <th scope="col">Status</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((row) => {
          /* One place decides which bucket a figure is read from, and it is
             the bucket the *server* put it in. A UI that looked in `headline`
             first and fell back to `in_sample` would be quietly undoing the
             distinction the two names exist to make - so the sample badge in
             the same row always says which one this number came from. */
          const figures = row.figures.sample === 'out_of_sample' ? row.figures.headline : row.figures.in_sample;
          return (
            <tr key={row.id}>
              <td>
                <Link to={`/runs/${row.id}`}>#{row.id}</Link>{' '}
                {row.kind === 'walk_forward' && <span className="tr-kind">walk-forward</span>}
              </td>
              <td>
                <Link to={`/strategies/${encodeURIComponent(row.strategy)}`}>{row.strategy}</Link>
              </td>
              <td className="tr-params">{describeParams(row.params)}</td>
              <td>
                <SampleBadge figures={row.figures} />
              </td>
              <td className="tr-numeric">{cell(sort, figures)}</td>
              {QUALIFIERS.map((name) => (
                <td key={name} className="tr-numeric">
                  {cell(name, { ...figures, ...row.figures.descriptive })}
                </td>
              ))}
              <td>
                <Provenance source={row.source} /> <SeededBadge row={row} />
              </td>
              <td>{when(row.finished_at)}</td>
              <td>
                <StatusBadge status={row.status} />
              </td>
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}

function cell(name: string, values: Record<string, string>): string {
  const value = values[name];
  return value === undefined ? '—' : formatMetric(name, value);
}
