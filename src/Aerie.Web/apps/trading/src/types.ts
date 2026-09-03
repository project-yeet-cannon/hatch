/**
 * What the trading control plane serves, as this app reads it.
 *
 * These mirror `aerie_trading/control/panel/views.py` field for field, in
 * snake_case, because that service's own shape is snake_case and renaming it
 * on the way in would mean this file carrying a second spelling of the honesty
 * layer's own field names.
 *
 * **Every number arrives as a string, and that is deliberate on the server's
 * side.** JSON has one numeric type and it is a double; money and returns are
 * computed as decimals and stored as NUMERIC, so rendering them as JSON
 * numbers would round exactly the figures somebody is checking by hand. This
 * app parses them for charts and formatting (`lib/format.ts`) and prints the
 * string itself wherever the exact value is the point.
 */

/** Where a result's data came from. Required on every row, never optional. */
export interface Source {
  id: number;
  name: string;
  description: string | null;
}

/**
 * A run's numbers, split by what they may be called.
 *
 * `headline` is only ever populated for a run whose window was held out; an
 * ordinary sweep run's figures arrive under `in_sample`, and the server
 * refuses to serve them any other way. The UI's job is to keep those two
 * labelled differently, not to decide which is which.
 */
export interface Figures {
  sample: 'in_sample' | 'out_of_sample';
  out_of_sample_start: string | null;
  out_of_sample_end: string | null;
  headline: Record<string, string>;
  in_sample: Record<string, string>;
  descriptive: Record<string, string>;
}

export interface RunRow {
  id: number;
  kind: 'backtest' | 'walk_forward';
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
  strategy: string;
  params: Record<string, unknown> | null;
  param_set_id: number | null;
  sweep_id: number | null;
  sweep_name: string | null;
  trials: number | null;
  seeded: boolean;
  source: Source;
  symbols: string[];
  interval: string;
  window_start: string;
  window_end: string;
  starting_cash: string;
  costs: Record<string, unknown>;
  enqueued_at: string;
  started_at: string | null;
  finished_at: string | null;
  duration_ms: number | null;
  bars: number | null;
  attempts: number;
  aerie_revision: string | null;
  data_fingerprint: string | null;
  result_fingerprint: string | null;
  error: string | null;
  figures: Figures;
}

export interface SweepProgress {
  total_runs: number;
  finished: number;
  by_status: Record<string, number>;
  complete: boolean;
  cancelled: boolean;
}

export interface SweepRow {
  id: number;
  name: string;
  strategy: string;
  seeded: boolean;
  created_at: string;
  cancelled_at: string | null;
  trials: number;
  spec: Record<string, unknown>;
  aerie_revision: string | null;
  progress: SweepProgress;
}

export interface Trade {
  sequence: number;
  symbol: string;
  filled_at: string;
  quantity: number;
  price: string;
  reference_price: string;
  commission: string;
  realized_pnl: string;
  tag: string;
}

/** `recorded: false` means the curve was never written, which is a different
    thing from a curve with no points in it. */
export interface Curve {
  recorded: boolean;
  points: [string, string][];
  points_total: number;
  sampled: boolean;
}

export interface Fold {
  fold: number;
  train_start: string;
  train_end: string;
  test_start: string;
  test_end: string;
  params: Record<string, unknown>;
  candidates: number;
  train_objective: string;
  starting_cash: string;
  ending_equity: string;
}

export interface RunDetail {
  run: RunRow;
  sweep: SweepRow | null;
  trades: Trade[];
  curve: Curve;
  folds: Fold[];
}

export interface Parameter {
  name: string;
  description: string;
  default: string | null;
  swept: boolean;
  low: string | null;
  high: string | null;
  step: string | null;
  count: number | null;
}

export interface RunCounts {
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
  cancelled: number;
}

export interface StrategyCard {
  name: string;
  description: string;
  shipped: boolean;
  parameters: Parameter[];
  sweeps: number;
  runs: RunCounts;
  last_finished_at: string | null;
  best: RunRow | null;
  live: string;
}

export interface StrategyDetail {
  strategy: StrategyCard;
  sweeps: SweepRow[];
  runs: RunRow[];
}

export interface Leaderboard {
  rows: RunRow[];
  sort: string;
  sample: string;
  since: string;
  mode: string;
  note: string | null;
}

export interface Installation {
  symbols: string[];
  intervals: string[];
  max_sweep_runs: number;
  walk_forward_folds: number | null;
}

export interface Plan {
  name: string;
  strategy: string;
  total: number;
  rows: number;
  combinations: number;
  rejected: number;
  rejection: string;
  describe: string;
  ceiling: number;
}

export interface LaunchRequest {
  strategy: string;
  name?: string | null;
  swept?: string[];
  grid?: Record<string, string[]>;
  symbols?: string[];
  interval?: string;
  window_start: string;
  window_end: string;
  starting_cash?: string;
  priority?: number;
  source?: string | null;
}
