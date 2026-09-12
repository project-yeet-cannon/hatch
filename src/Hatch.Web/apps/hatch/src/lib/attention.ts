/* What the nav strip's attention control says, and when it says it loudly.

   Everything decidable lives here rather than in the components, because this
   app has no component tests: the count, the tone, which of the two review
   emptinesses applies, the accessible sentence, and how long a question has
   been waiting are all pure functions of one answer from the server, and every
   one of them is pinned in attention.test.ts. */

import type { Attention } from '../types';

/** Resting or loud. Named for what it is doing rather than for how it looks, so
    a later change to the loud state's colour is not a rename. `isWaiting` is
    already taken by schedule.ts, for a ready date that has not arrived. */
export type AttentionTone = 'rest' | 'asking';

/**
 * How many rows the panel would draw.
 *
 * Reviews plus questions, and deliberately not `inReviewWithoutPullRequest`.
 * An issue standing in review with nowhere to review it is something a person
 * cannot act on from here, and a control that stayed lit for one would be a
 * control nobody reads after a week. It is said out loud in the section's empty
 * state instead - see `reviewEmptyWords`.
 */
export function attentionCount(attention: Attention | null): number {
  if (attention === null) return 0;
  return attention.reviews.length + attention.questions.length;
}

/** Loud only when there is something a person can act on. Nothing read yet is
    resting, not a third state: "we have not asked yet" and "nothing is waiting"
    are the same thing to draw. */
export const attentionTone = (attention: Attention | null): AttentionTone =>
  attentionCount(attention) > 0 ? 'asking' : 'rest';

/**
 * What the control is called, in words - which is the whole of what a screen
 * reader gets, and half of what makes the loud state legible without colour.
 *
 * `2 pull requests to review, 1 question to answer`, either half dropped when
 * it is empty, and one sentence at rest. Pluralised on both halves, because
 * `1 pull requests` read out loud is the kind of thing that makes somebody stop
 * trusting the rest of the sentence.
 */
export function attentionLabel(attention: Attention | null): string {
  if (attention === null) return 'Nothing is waiting on you';

  const parts: string[] = [];
  if (attention.reviews.length > 0) parts.push(`${count(attention.reviews.length, 'pull request')} to review`);
  if (attention.questions.length > 0) parts.push(`${count(attention.questions.length, 'question')} to answer`);

  return parts.length > 0 ? parts.join(', ') : 'Nothing is waiting on you';
}

/**
 * Which emptiness the pull request section is in.
 *
 * The two are different facts and the difference is the point. Nothing in
 * review at all is a quiet board. Things in review with no pull request
 * recorded is a ticket whose agent forgot `hatch pr` - invisible everywhere
 * else, and the reason this wording exists rather than one flat "nothing here".
 */
export function reviewEmptyWords(attention: Attention | null): string {
  const without = attention?.inReviewWithoutPullRequest ?? 0;
  if (without === 0) return 'Nothing is up for review.';

  return without === 1
    ? '1 issue is in review with no pull request recorded.'
    : `${without} issues are in review with no pull request recorded.`;
}

/** The other section's empty state. One wording: a question either exists or it
    does not, and there is no second kind of nothing to tell apart. */
export const questionEmptyWords = (): string => 'Nothing is waiting on an answer.';

/**
 * How long a question has been waiting: `just asked`, `4 minutes`, `2 hours`,
 * `3 days`.
 *
 * Its own rather than `agePhrase` from utilization.ts, which says `read 3 hours
 * ago` - that is the battery's sentence about a reading it took, and this is a
 * duration sitting beside a question in a list. It also has to reach days: a
 * reading is never more than a few hours old and a question can sit over a
 * weekend, which is exactly when somebody most needs to see how long.
 */
export function waitedWords(askedAt: string, now: Date): string {
  const at = Date.parse(askedAt);
  if (Number.isNaN(at)) return 'waiting';

  const minutes = Math.floor(Math.max(0, now.getTime() - at) / 60_000);
  if (minutes < 1) return 'just asked';
  if (minutes < 60) return count(minutes, 'minute');

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return count(hours, 'hour');

  return count(Math.floor(hours / 24), 'day');
}

/** `1 question`, `2 questions`. Every noun here takes a plain -s. */
const count = (n: number, noun: string): string => `${n} ${noun}${n === 1 ? '' : 's'}`;
