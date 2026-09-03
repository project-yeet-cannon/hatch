/**
 * Every request this app makes, in one file.
 *
 * Same-origin throughout: the bundle is served by the trading control plane
 * itself (`aerie_trading/control/spa.py`), so `/api/trading/...` is that
 * service and no base URL is configured anywhere. That is the point of the
 * seam - an app that had to be told its own API's address would be an app with
 * one installation's hostname compiled into it.
 *
 * **A refusal's body is the message.** The control plane answers a refused
 * launch with the same sentence its CLI prints - "this sweep is 20 runs and 19
 * were confirmed" - and reporting "POST /api/trading/sweeps failed: 409"
 * instead throws away the only text that says what to do about it.
 */

import type {
  Installation,
  Leaderboard,
  LaunchRequest,
  Plan,
  RunCounts,
  RunDetail,
  StrategyCard,
  StrategyDetail,
} from '../types';

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    headers: {
      Accept: 'application/json',
      ...(typeof init?.body === 'string' ? { 'Content-Type': 'application/json' } : {}),
    },
    ...init,
  });
  if (!response.ok) {
    throw new Error(await failureMessage(response, init?.method ?? 'GET', path));
  }
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/**
 * What a failed request says out loud.
 *
 * FastAPI puts a refusal's sentence in `detail`. Anything else - an HTML error
 * page from a proxy, a validation array, an empty body - falls back to the
 * status line, because one of those under a form control is worse than
 * nothing.
 */
async function failureMessage(response: Response, method: string, path: string): Promise<string> {
  const fallback = `${method} ${path} failed: ${response.status} ${response.statusText}`;
  try {
    const body = (await response.text()).trim();
    if (body === '' || body.length > 2000) return fallback;
    const parsed: unknown = JSON.parse(body);
    if (parsed && typeof parsed === 'object' && 'detail' in parsed) {
      const detail = (parsed as { detail: unknown }).detail;
      if (typeof detail === 'string' && detail !== '') return detail;
    }
    return fallback;
  } catch {
    return fallback;
  }
}

const base = '/api/trading';

export const api = {
  installation: () => fetchJson<Installation>(`${base}/installation`),

  strategies: () => fetchJson<StrategyCard[]>(`${base}/strategies`),

  strategy: (name: string) => fetchJson<StrategyDetail>(`${base}/strategies/${encodeURIComponent(name)}`),

  /** The board. Every filter is echoed back on the response, so the page can
      state what it is showing rather than remembering what it asked for. */
  leaderboard: (query: {
    sort?: string;
    sample?: string;
    since?: string;
    mode?: string;
    strategy?: string;
    limit?: number;
  }) => {
    const search = new URLSearchParams();
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== '') search.set(key, String(value));
    }
    return fetchJson<Leaderboard>(`${base}/leaderboard?${search.toString()}`);
  },

  run: (id: number) => fetchJson<RunDetail>(`${base}/runs/${id}`),

  sweep: (id: number) => fetchJson<StrategyDetail>(`${base}/sweeps/${id}`),

  queue: () => fetchJson<{ counts: RunCounts }>(`${base}/queue`).then((payload) => payload.counts),

  /** The estimate. Writes nothing, and is the only way to learn the number
      `launch` requires - see the launcher's own note on why that handshake is
      in front of a button as well as in front of the CLI. */
  plan: (request: LaunchRequest) =>
    fetchJson<Plan>(`${base}/sweeps/plan`, { method: 'POST', body: JSON.stringify(request) }),

  launch: (request: LaunchRequest, confirm: number) =>
    fetchJson<{ sweep_id: number }>(`${base}/sweeps`, {
      method: 'POST',
      body: JSON.stringify({ request, confirm }),
    }),
};
