import { useCallback } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Button, Card, EmptyState, Field, PageHeader, Text } from '@aerie/ui';
import { getPlan, getProjects, getStatuses } from '../api/client';
import { StatusMeter } from '../components/StatusMeter';
import { StatusPill } from '../components/StatusPill';
import { orderPlan } from '../lib/plan';
import { useLoaded } from '../lib/useLoaded';
import type { Plan, PlanEntry, Project, Status } from '../types';

/** The URL's whole vocabulary here: which project, by key. */
const PROJECT = 'project';

/** The three reads this page is made of, arriving together so a half-drawn
    page is never a state - a meter without its statuses is a bar with no
    colours, and a picker without its projects is an empty select. */
interface PlanView {
  plan: Plan;
  statuses: Status[];
  projects: Project[];
}

/**
 * The screen the epic is named for: every epic in the house with a meter under
 * it, so the operator can pick the one worth furthering today.
 *
 * It does not fetch the board. The board is seven hundred cards this page has
 * no use for, and the whole reason `/api/hatch/plan` exists as its own read is
 * that the arithmetic is done on the server once rather than re-derived here
 * from every card in the tracker.
 */
export function PlanPage() {
  const [params, setParams] = useSearchParams();
  const chosen = params.get(PROJECT) ?? '';

  /* Wrapped so useLoaded's effect has a stable dependency, and keyed on the
     chosen project so picking one is a refetch rather than a filter applied
     here - the server scopes both halves of the plan, including the loose
     bucket, and a client-side filter would get the second half wrong. */
  const load = useCallback(async (): Promise<PlanView> => {
    const [projects, statuses] = await Promise.all([getProjects(), getStatuses()]);

    /* The plan waits on the project list rather than going out beside it,
       because the URL holds a key - the thing a person would type or paste -
       and the API takes an id. A key naming no project falls back to the whole
       tracker, which is what the picker will be showing anyway. */
    const project = projects.find((p) => p.key === chosen);

    return { plan: await getPlan(project?.id), statuses, projects };
  }, [chosen]);

  const { data, error } = useLoaded<PlanView>(load);

  function choose(key: string) {
    const next = new URLSearchParams(params);
    if (key) next.set(PROJECT, key);
    else next.delete(PROJECT);
    // Replaced rather than pushed: flipping a picker is not a place somebody
    // wants six presses of Back to walk them through.
    setParams(next, { replace: true });
  }

  if (error && !data) return <p className="text-danger">{error}</p>;
  if (!data) return <p className="text-muted">Loading…</p>;

  const { plan, statuses, projects } = data;
  const { live, finished } = orderPlan(plan.epics);
  const empty = plan.epics.length === 0 && plan.loose.leaves === 0;

  return (
    <div className="hatch-page">
      <PageHeader
        title="Plan"
        description="Every epic, and how far along it is. The nearest the line is at the top."
        /* Only when there is a choice to make. One project makes this a
           control with a single option, and Aerie ships to operators who will
           have several - so it is written for both and shown for one. */
        actions={
          projects.length > 1 && (
            <Field label="Project" className="hatch-plan-picker">
              <select value={chosen} onChange={(e) => choose(e.target.value)}>
                <option value="">All projects</option>
                {projects.map((project) => (
                  <option key={project.id} value={project.key}>
                    {project.key} — {project.name}
                  </option>
                ))}
              </select>
            </Field>
          )
        }
      />

      {error && <p className="text-danger">{error}</p>}

      {empty ? (
        <EmptyState
          message="Nothing is filed yet. The plan is a picture of the board, and the board is empty."
          action={
            <Button as={Link} to="/" variant="primary">
              Go to the board
            </Button>
          }
        />
      ) : (
        <>
          {live.map((entry) => (
            <EpicCard key={entry.issue.key} entry={entry} statuses={statuses} />
          ))}

          {/* Not a card, and collapsed: finished work is context, not the
              page. <details> because the browser already knows how to open
              and close one, from the keyboard and to a screen reader. */}
          {finished.length > 0 && (
            <details className="hatch-plan-finished">
              <summary>
                Finished
                <span className="hatch-plan-finished-count">{finished.length}</span>
              </summary>
              {finished.map((entry) => (
                <EpicCard key={entry.issue.key} entry={entry} statuses={statuses} />
              ))}
            </details>
          )}

          {/* Last, and only when it holds something. Its job is to stop this
              view quietly hiding half the tracker from an operator who filed a
              story without a parent - so it is one line and a bar, not an epic
              pretending to be one. */}
          {plan.loose.leaves > 0 && (
            <Card>
              <div className="hatch-plan-head">
                <span className="hatch-plan-title">Under no epic</span>
              </div>
              <Text tone="muted">Work filed without an epic above it. It is on the board, but not on this page.</Text>
              <StatusMeter rollup={plan.loose} statuses={statuses} size="lg" counts />
            </Card>
          )}
        </>
      )}
    </div>
  );
}

/** One root epic and everything drawn inside it. */
function EpicCard({ entry, statuses }: { entry: PlanEntry; statuses: Status[] }) {
  return (
    <Card>
      <EpicRow entry={entry} statuses={statuses} top />
    </Card>
  );
}

/**
 * An epic's line: what it is, where it is, and how far along it is - with the
 * epics beneath it drawn the same way, one indent per level.
 *
 * The indent comes from the recursion rather than from a depth counter: each
 * level wraps its children in one nested box with one step of padding, so
 * "one indent per level" is a fact about the markup instead of an arithmetic
 * anybody has to keep right.
 *
 * The root's meter is `lg` and a nested one is `sm`. A card is about its root
 * epic; the ones inside it are what it is made of, and a page of equal bars
 * says nothing about which is which.
 */
function EpicRow({ entry, statuses, top = false }: { entry: PlanEntry; statuses: Status[]; top?: boolean }) {
  const status = statuses.find((s) => s.id === entry.issue.statusId);

  return (
    <div className={`hatch-plan-row${top ? ' top' : ''}`}>
      <div className="hatch-plan-head">
        <Link to={`/issues/${entry.issue.key}`} className="hatch-plan-key">
          {entry.issue.key}
        </Link>
        <span className="hatch-plan-title">{entry.issue.title}</span>
        {/* Its own column, which is not what the meter says. An epic sitting in
            the inbox with half its stories done is a thing worth seeing, and
            the bar below cannot say it. */}
        {status && <StatusPill status={status} />}
      </div>

      {/* Renders nothing when there is nothing filed under it - see
          StatusMeter. An epic with no stories is at the start of its life, and
          an empty trough would read as stalled at zero. */}
      <StatusMeter rollup={entry.rollup} statuses={statuses} size={top ? 'lg' : 'sm'} counts />

      {entry.children.length > 0 && (
        <div className="hatch-plan-nested">
          {entry.children.map((child) => (
            <EpicRow key={child.issue.key} entry={child} statuses={statuses} />
          ))}
        </div>
      )}
    </div>
  );
}
