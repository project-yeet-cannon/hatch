import { useState } from 'react';
import { Badge, Button, Card, EmptyState, Field, PageHeader, Table, Text } from '@hatch/ui';
import { patchRunner } from '../api/client';
import { MomentField } from '../components/MomentField';
import { agoPhrase } from '../lib/claim';
import { message } from '../lib/errors';
import {
  activityWords,
  boundsChanged,
  boundsOf,
  boundsProblem,
  boundsRequest,
  isControllable,
  runnerActivity,
  type RunnerActivity,
  type RunnerBounds,
} from '../lib/runners';
import { useRunners } from '../lib/useRunners';
import type { Runner, RunnerPatchRequest } from '../types';

/**
 * Every runner that has spoken to this Hatch lately, and the three things a
 * person can tell one: keep going, hold, or finish and stop.
 *
 * Nothing on this page starts a process, and that is the design rather than a
 * limitation. A press writes down what the board would like; the runner asks
 * for it between increments and obeys - which is what makes it work for a loop
 * in a container and for one on somebody's laptop behind a router nothing can
 * reach. The cost is that a press takes effect at the top of the next pass,
 * after whatever increment is in flight has finished, and the page says so
 * rather than pretending otherwise.
 */
export function RunnersPage() {
  const { runners, error, reload } = useRunners();
  const [failure, setFailure] = useState<string | null>(null);

  async function act(action: () => Promise<unknown>) {
    try {
      await action();
      setFailure(null);
    } catch (err) {
      setFailure(message(err));
    }

    // Whatever happened, ask again: a refused press must not leave the row
    // drawing what was asked for rather than what is true.
    await reload();
  }

  // One instant for the whole page, so two rows a second apart are not judged
  // against two different nows.
  const now = new Date();

  return (
    <div className="hatch-page">
      <PageHeader
        title="Runners"
        description="The loops that have spoken to this Hatch lately. A runner asks for its instructions between increments, so anything set here takes effect on its next pass - never in the middle of one."
      />

      {(failure ?? error) && <p className="text-danger">{failure ?? error}</p>}

      {runners?.length === 0 && (
        <EmptyState message="Nothing has run here lately. A runner appears within a minute of hatch go-to-work starting, and drops off a quarter of an hour after it stops." />
      )}

      {runners && runners.length > 0 && (
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Runner</th>
                <th>Doing</th>
                <th>Last said</th>
                <th>Heard from</th>
                <th>Bounds</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {runners.map((runner) => (
                <Row
                  key={runner.name}
                  runner={runner}
                  now={now}
                  onPatch={(request) => void act(() => patchRunner(runner.name, request))}
                />
              ))}
            </tbody>
          </Table>
        </Card>
      )}
    </div>
  );
}

/** What each activity is worth saying loudly. Working is the one worth a colour
    - it is the row that is spending money right now - and gone is the one worth
    a warning. */
const TONES: Record<RunnerActivity, 'muted' | 'success' | 'danger' | 'primary'> = {
  working: 'success',
  stopping: 'primary',
  paused: 'primary',
  gone: 'danger',
  idle: 'muted',
};

function Row({
  runner,
  now,
  onPatch,
}: {
  runner: Runner;
  now: Date;
  onPatch: (request: RunnerPatchRequest) => void;
}) {
  const [open, setOpen] = useState(false);
  const activity = runnerActivity(runner, now);
  const controllable = isControllable(runner);

  return (
    <>
      <tr>
        <td>
          <strong className="hatch-runner-name">{runner.name}</strong>
          {/* Said out loud rather than left to the absent controls, because a
              row with no buttons reads as a bug otherwise. */}
          <Text tone="muted">{runner.kind === 'loop' ? 'loop' : 'one increment'}</Text>
        </td>
        <td>
          <Badge tone={TONES[activity]}>{activityWords(runner, now)}</Badge>
        </td>
        <td>{runner.line ?? <Text tone="muted">nothing yet</Text>}</td>
        <td>{agoPhrase(runner.lastSeenAt, now)}</td>
        <td>
          <BoundsSummary runner={runner} />
        </td>
        <td>
          {controllable && (
            <div className="hatch-reorder">
              <Button
                onClick={() => onPatch({ state: runner.state === 'paused' ? 'running' : 'paused' })}
                disabled={runner.state === 'stopping'}
              >
                {runner.state === 'paused' ? 'Resume' : 'Pause'}
              </Button>
              <Button
                variant="danger"
                onClick={() => onPatch({ state: 'stopping' })}
                disabled={runner.state === 'stopping'}
              >
                Stop after this one
              </Button>
              <Button onClick={() => setOpen(!open)}>{open ? 'Hide bounds' : 'Bounds'}</Button>
            </div>
          )}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={6}>
            <BoundsForm runner={runner} onSave={onPatch} />
          </td>
        </tr>
      )}
    </>
  );
}

/** The bounds at a glance: only the ones that are set, because four "no cap"s
    on every row would be four things to read past to find the one that is
    holding. */
function BoundsSummary({ runner }: { runner: Runner }) {
  const said = [
    runner.under && `under ${runner.under}`,
    runner.maxRuns !== null && `${runner.maxRuns} run(s)`,
    runner.maxSpend !== null && `$${runner.maxSpend}`,
    runner.untilAt && `until ${new Date(runner.untilAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`,
  ].filter(Boolean);

  return said.length === 0 ? <Text tone="muted">no bounds</Text> : <>{said.join(', ')}</>;
}

/**
 * The four bounds, edited together.
 *
 * Saved on a button rather than on blur, for the reason the playbook prompt is:
 * these are numbers somebody is part way through typing, and a PATCH fired
 * because they tabbed out of the box would set a budget of "4" on the way to
 * "40".
 */
function BoundsForm({ runner, onSave }: { runner: Runner; onSave: (request: RunnerPatchRequest) => void }) {
  const [draft, setDraft] = useState<RunnerBounds>(boundsOf(runner));
  const dirty = boundsChanged(draft, runner);
  const problem = boundsProblem(draft);

  const set = (field: keyof RunnerBounds, value: string) => setDraft({ ...draft, [field]: value });

  return (
    <div className="hatch-runner-bounds">
      <div className="hatch-field-grid">
        <Field label="Under" hint="An epic to stay inside, like AER-930. Empty is the whole board.">
          <input value={draft.under} onChange={(e) => set('under', e.target.value)} />
        </Field>
        <Field label="Max runs" hint="Increments this night may spend, counting the ones already spent. Empty is no cap.">
          <input inputMode="numeric" value={draft.maxRuns} onChange={(e) => set('maxRuns', e.target.value)} />
        </Field>
        <Field label="Max spend" hint="Dollars, on the same terms.">
          <input inputMode="decimal" value={draft.maxSpend} onChange={(e) => set('maxSpend', e.target.value)} />
        </Field>
        <MomentField
          label="Until"
          hint="An hour to stop by, in this browser's timezone."
          value={draft.untilAt === '' ? null : draft.untilAt}
          onChange={(next) => set('untilAt', next)}
        />
      </div>

      {problem && <p className="text-danger">{problem}</p>}

      <div className="hatch-form-actions">
        <Button
          variant="primary"
          disabled={!dirty || problem !== null}
          onClick={() => onSave(boundsRequest(draft))}
        >
          Save bounds
        </Button>
        <Button disabled={!dirty} onClick={() => setDraft(boundsOf(runner))}>
          Revert
        </Button>
      </div>

      <Text tone="muted">
        Folded into the loop at its next heartbeat, and applied from its next ticket onward. A cap set below what
        this night has already spent stops it at the next pass.
      </Text>
    </div>
  );
}
