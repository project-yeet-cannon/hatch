import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Button, Card, Field, Text } from '@aerie/ui';
import { api } from '../api/client';
import { useAsync } from '../lib/useAsync';
import type { LaunchRequest, Plan, StrategyCard } from '../types';

/**
 * *"A sweep launcher with an estimated run count before you commit."*
 *
 * **The estimate is a handshake, not a hint, and this form is built around
 * that.** The server refuses a launch unless the caller passes back the number
 * the estimate reported (`runs/sweep.py`), so there is deliberately no way to
 * press Launch without having asked for an estimate first: the button does not
 * exist until a plan is on screen, and changing any field throws the plan away.
 * A form that launched with one click would be a form that satisfied the
 * handshake on the operator's behalf, which is the version of this control the
 * plan warns about.
 *
 * **Nothing about the installation is compiled in.** The universe, the
 * intervals and the ceiling come from `GET /api/trading/installation`, because
 * docs/ethos.md's one rule is that nothing in this repository may be true of
 * exactly one installation - and a symbol list in a form is exactly that.
 */
export function SweepLauncher({ strategy, onLaunched }: { strategy: StrategyCard; onLaunched: () => void }) {
  const navigate = useNavigate();
  const { data: installation } = useAsync(() => api.installation(), []);

  const swept = strategy.parameters.filter((parameter) => parameter.swept);
  const [walk, setWalk] = useState<string[]>(() => swept.map((parameter) => parameter.name));
  const [name, setName] = useState('');
  const [start, setStart] = useState('2021-01-01');
  const [end, setEnd] = useState('2026-01-01');
  const [cash, setCash] = useState('100000');
  /* The estimate, and the form it was an estimate *of*. Held together in one
     piece of state and compared during render rather than cleared by an
     effect: a plan and the fields it describes cannot drift apart if they are
     one value. Without that, a person could estimate a 20-run grid, widen it,
     and launch the wider one against the number they read - which is exactly
     the drift the confirmation exists to catch, made invisible by a form that
     kept a stale figure on screen. */
  const [estimated, setEstimated] = useState<{ plan: Plan; of: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const request = (): LaunchRequest => ({
    strategy: strategy.name,
    name: name.trim() === '' ? null : name.trim(),
    swept: walk,
    window_start: `${start}T00:00:00+00:00`,
    window_end: `${end}T00:00:00+00:00`,
    starting_cash: cash,
  });

  const signature = JSON.stringify(request());
  const plan = estimated && estimated.of === signature ? estimated.plan : null;

  const estimate = async () => {
    setBusy(true);
    setFailure(null);
    try {
      setEstimated({ plan: await api.plan(request()), of: signature });
    } catch (error) {
      setFailure(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  };

  const launch = async () => {
    if (!plan) return;
    setBusy(true);
    setFailure(null);
    try {
      const { sweep_id } = await api.launch(request(), plan.total);
      setEstimated(null);
      onLaunched();
      navigate(`/sweeps/${sweep_id}`);
    } catch (error) {
      setFailure(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <h2 className="tr-section-title">Launch a sweep</h2>
      <Text tone="muted">
        Every run in a sweep shares one window, one universe and one cost model — that is what makes the
        sweep a comparison. One extra run is enqueued beside the grid to evaluate it out of sample, and it
        is the only one whose figures can be called performance.
      </Text>

      <div className="tr-filters">
        <Field label="Name" hint="Optional. Generated from the strategy and the clock when blank.">
          <input value={name} onChange={(event) => setName(event.target.value)} placeholder="auto" />
        </Field>
        <Field label="From">
          <input type="date" value={start} onChange={(event) => setStart(event.target.value)} />
        </Field>
        <Field label="To" hint="Exclusive.">
          <input type="date" value={end} onChange={(event) => setEnd(event.target.value)} />
        </Field>
        <Field label="Starting cash">
          <input value={cash} onChange={(event) => setCash(event.target.value)} inputMode="decimal" />
        </Field>
      </div>

      <fieldset className="tr-fieldset">
        <legend>Parameters to walk</legend>
        {swept.length === 0 ? (
          <Text tone="muted">
            {strategy.name} declares no swept parameters, so a sweep of it is one run at every default.
          </Text>
        ) : (
          swept.map((parameter) => (
            <label key={parameter.name} className="tr-check">
              <input
                type="checkbox"
                checked={walk.includes(parameter.name)}
                onChange={(event) =>
                  setWalk((current) =>
                    event.target.checked
                      ? [...current, parameter.name]
                      : current.filter((entry) => entry !== parameter.name),
                  )
                }
              />
              <span>
                {parameter.name} — {parameter.low} to {parameter.high} step {parameter.step} (
                {parameter.count} values)
              </span>
            </label>
          ))
        )}
      </fieldset>

      <Text tone="muted">
        Universe: {installation ? installation.symbols.join(', ') : '…'} — what this installation collects
        bars for. Ceiling on one launch: {installation ? installation.max_sweep_runs.toLocaleString() : '…'}{' '}
        runs.
      </Text>

      <div className="tr-actions">
        <Button onClick={estimate} disabled={busy}>
          Estimate
        </Button>
        {/* Only reachable once an estimate is on screen, and it sends that
            estimate's own number back. */}
        {plan && (
          <Button variant="primary" onClick={launch} disabled={busy}>
            Launch {plan.total.toLocaleString()} run{plan.total === 1 ? '' : 's'}
          </Button>
        )}
      </div>

      {plan && (
        <div className="tr-plan">
          <Text>{plan.describe}</Text>
          <Text tone="muted">
            {plan.rows.toLocaleString()} rows on the queue: {plan.total.toLocaleString()} of the grid plus the
            walk-forward evaluation.
            {plan.rejected > 0 &&
              ` ${plan.rejected.toLocaleString()} combination(s) pruned by the strategy itself: ${plan.rejection}`}
          </Text>
        </div>
      )}

      {failure && <Text tone="danger">{failure}</Text>}
    </Card>
  );
}
