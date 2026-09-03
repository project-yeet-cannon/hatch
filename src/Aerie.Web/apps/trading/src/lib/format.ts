/**
 * Turning the API's exact strings into something a person reads.
 *
 * Every number arrives as text because the server computed it as a decimal
 * (see `types.ts`). Formatting is the one place this app converts, and it does
 * so for *display only*: nothing here feeds a value back to the API, so a
 * rounded figure on a screen cannot become a rounded figure in a row.
 *
 * The metric names are the server's, from `engine/metrics.py`. They are
 * labelled here rather than there because a label is a UI decision - the same
 * `sharpe` is "Sharpe" on a table header and "Sharpe ratio" in a tooltip - and
 * because a name this build has no label for still renders, as itself.
 */

/** How each metric wants to be read. Anything absent falls back to a plain
    decimal, which is right for a metric a later phase adds. */
const KINDS: Record<string, 'ratio' | 'percent' | 'money' | 'count' | 'days'> = {
  total_return: 'percent',
  annualized_return: 'percent',
  stressed_total_return: 'percent',
  baseline_return: 'percent',
  index_return: 'percent',
  excess_return: 'percent',
  excess_return_index: 'percent',
  max_drawdown: 'percent',
  win_rate: 'percent',
  exposure: 'percent',
  volatility: 'percent',
  cost_sensitivity: 'percent',
  final_equity: 'money',
  commission_paid: 'money',
  slippage_paid: 'money',
  sharpe: 'ratio',
  sortino: 'ratio',
  stressed_sharpe: 'ratio',
  deflated_sharpe: 'ratio',
  expected_max_sharpe: 'ratio',
  turnover: 'ratio',
  trade_count: 'count',
  selection_trials: 'count',
  walk_forward_folds: 'count',
  max_drawdown_days: 'days',
};

/** The label a column header uses. Underscores become spaces for a name this
    build has never heard of, which is a better answer than hiding it. */
const LABELS: Record<string, string> = {
  total_return: 'Return',
  annualized_return: 'Annualized',
  final_equity: 'Final equity',
  sharpe: 'Sharpe',
  sortino: 'Sortino',
  deflated_sharpe: 'Sharpe, after selection',
  expected_max_sharpe: 'Sharpe from luck alone',
  selection_trials: 'Trials',
  max_drawdown: 'Max drawdown',
  max_drawdown_days: 'Drawdown length',
  win_rate: 'Win rate',
  trade_count: 'Trades',
  exposure: 'Exposure',
  turnover: 'Turnover',
  volatility: 'Volatility',
  commission_paid: 'Commission',
  slippage_paid: 'Slippage',
  baseline_return: 'Buy & hold',
  index_return: 'Broad index',
  excess_return: 'vs. buy & hold',
  excess_return_index: 'vs. index',
  stressed_total_return: 'Return at higher costs',
  stressed_sharpe: 'Sharpe at higher costs',
  cost_sensitivity: 'Cost sensitivity',
  walk_forward_folds: 'Folds',
};

export function metricLabel(name: string): string {
  return LABELS[name] ?? name.replace(/_/g, ' ');
}

export function formatMetric(name: string, value: string): string {
  switch (KINDS[name]) {
    case 'percent':
      return percent(value);
    case 'money':
      return money(value);
    case 'count':
      return Number(value).toLocaleString();
    case 'days':
      return `${Math.round(Number(value)).toLocaleString()} d`;
    default:
      return decimal(value, 2);
  }
}

/** Whether a bigger number is better, for the one bit of colour a figure
    carries. `undefined` means the question does not apply - a trade count is
    neither good nor bad - and those render in the resting colour. */
export function betterWhenPositive(name: string): boolean | undefined {
  if (name === 'max_drawdown' || name === 'max_drawdown_days' || name === 'cost_sensitivity') {
    return false;
  }
  if (KINDS[name] === 'count' || name === 'exposure' || name === 'turnover') return undefined;
  return true;
}

export function percent(value: string | number, places = 2): string {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return String(value);
  return `${(parsed * 100).toFixed(places)}%`;
}

export function money(value: string | number): string {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return String(value);
  /* No currency symbol. The engine accounts in whatever the instruments are
     denominated in, and this app has no business asserting which that is. */
  return parsed.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function decimal(value: string | number, places = 2): string {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) return String(value);
  return parsed.toFixed(places);
}

/** A timestamp as a person reads it. Absent renders as an em dash rather than
    as "Invalid Date", which is what `new Date(null)` would otherwise print. */
export function when(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleString();
}

export function day(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? '—' : parsed.toLocaleDateString();
}

export function duration(ms: number | null): string {
  if (ms === null) return '—';
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`;
}

/** A parameter set as one line: `fast 5, slow 20`. */
export function describeParams(params: Record<string, unknown> | null): string {
  if (!params) return '—';
  const entries = Object.entries(params);
  if (entries.length === 0) return 'defaults';
  return entries.map(([key, value]) => `${key} ${String(value)}`).join(', ');
}
