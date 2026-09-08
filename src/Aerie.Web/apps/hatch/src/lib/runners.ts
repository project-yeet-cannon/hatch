/* What a runner row says about itself, apart from the page that draws it - the
   way lib/claim.ts is apart from the badge.

   The server hands over four facts and no verdict: when the runner was last
   heard from, how long silence is allowed to last, what it holds, and what it
   was asked to do. Turning those into one word is this file's whole job, and it
   is here rather than in the page because the ordering between them is a
   judgement worth testing: a runner that has stopped answering is *gone*
   whatever it was asked to do, because "we told it to pause" says nothing about
   whether it is still there to have heard.

   Pure functions taking `now` rather than reading it, so a page judges every
   row against one instant. */

import type { Runner, RunnerPatchRequest } from '../types';

/** The five states a row can be in, in the order they are decided. */
export type RunnerActivity = 'gone' | 'paused' | 'stopping' | 'working' | 'idle';

/**
 * What this runner is doing, in one word.
 *
 * Silence beats intent, which is the one ordering decision here: a paused
 * runner that stopped heartbeating is `gone`, because the pause was a request
 * and the silence is a fact. Below that, what it was asked to do beats what it
 * is holding - a loop finishing its last increment is `stopping`, and that is
 * the thing somebody watching wants to see.
 */
export function runnerActivity(runner: Runner, now: Date): RunnerActivity {
  const beat = Date.parse(runner.lastSeenAt);

  // An unparseable heartbeat is the one case where "look at this" is the honest
  // answer: it is a row whose age cannot be told, not a row that is fine.
  if (Number.isNaN(beat) || now.getTime() - beat > runner.goneAfterSeconds * 1000) return 'gone';

  if (runner.state === 'paused') return 'paused';
  if (runner.state === 'stopping') return 'stopping';

  return runner.claimKey ? 'working' : 'idle';
}

/** The same, as the words on the row. */
export function activityWords(runner: Runner, now: Date): string {
  const activity = runnerActivity(runner, now);
  switch (activity) {
    case 'gone':
      return 'Gone';
    case 'paused':
      return 'Paused';
    case 'stopping':
      return 'Stopping';
    case 'working':
      return `Working ${runner.claimKey}`;
    default:
      return 'Idle';
  }
}

/**
 * Whether this row has controls at all.
 *
 * A `once` runner is one increment that has already read whatever it was going
 * to read; a Pause on it would be a button nothing will ever look at, which is
 * worse than no button.
 */
export const isControllable = (runner: Runner) => runner.kind === 'loop';

/** The bounds as the form holds them: strings, because that is what inputs are
    and what the wire takes. */
export interface RunnerBounds {
  under: string;
  maxRuns: string;
  maxSpend: string;
  /** The wire form, or `''` - what MomentField hands back. */
  untilAt: string;
}

/** What a row's bounds look like in the form that edits them. */
export function boundsOf(runner: Runner): RunnerBounds {
  return {
    under: runner.under ?? '',
    maxRuns: runner.maxRuns === null ? '' : String(runner.maxRuns),
    maxSpend: runner.maxSpend === null ? '' : String(runner.maxSpend),
    untilAt: runner.untilAt ?? '',
  };
}

/** Whether the form is holding anything the row is not. */
export function boundsChanged(draft: RunnerBounds, runner: Runner): boolean {
  const saved = boundsOf(runner);
  return (['under', 'maxRuns', 'maxSpend', 'untilAt'] as const).some((f) => draft[f].trim() !== saved[f].trim());
}

/**
 * Why the form cannot be saved yet, or null.
 *
 * The one rule worth stating on screen rather than discovering as a refusal:
 * `until` is an hour and not a day. The server says the same thing, and this is
 * only what stops somebody typing a date, pressing Save and being told off for
 * a control that offered them the empty time box in the first place.
 */
export function boundsProblem(draft: RunnerBounds): string | null {
  return draft.untilAt.trim() !== '' && !draft.untilAt.includes('T')
    ? 'An until is an hour, not just a day - set a time beside the date.'
    : null;
}

/**
 * The form as a request: every field, trimmed, including the empty ones.
 *
 * Sending all four rather than only what changed is the point of the tri-state:
 * `''` is how a cap comes off, and a field left out would mean "leave it", so a
 * cleared box that was not sent would silently keep the cap it was clearing.
 */
export function boundsRequest(draft: RunnerBounds): RunnerPatchRequest {
  return {
    under: draft.under.trim(),
    maxRuns: draft.maxRuns.trim(),
    maxSpend: draft.maxSpend.trim(),
    untilAt: draft.untilAt.trim(),
  };
}
