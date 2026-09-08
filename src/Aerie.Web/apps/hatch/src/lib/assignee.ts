/* Who owns a ticket, as the browser has to reason about it: a stable token to
   put in a <select>, an identity comparison, and an order.

   Its own module rather than three helpers inside a component, for the reason
   every other lib/ file here exists: there is no DOM in the test run, so
   anything with an argument in it lives here and the components hold state and
   events. */

import type { Assignee } from '../types';

/** The filter's value for "nobody owns this".

    Safe as a sentinel because every token below is `person:<guid>` or
    `key:<guid>`, and no guid is the word `unassigned`. That is why it can be a
    bare string in the same field a token goes in, rather than a second flag
    beside it. */
export const UNASSIGNED = 'unassigned';

/**
 * One identity, as a string a <select> can hold. Both halves, because two ids
 * from two tables can collide in principle and `person:…` and `key:…` are two
 * different things rather than one ambiguous guid.
 */
export const assigneeToken = (assignee: Assignee): string => `${assignee.kind}:${assignee.id}`;

/**
 * Whether two assignees are the same somebody. On the kind and the id and never
 * on the name: a person who has been renamed is still the person the ticket
 * belongs to, and the name is only what is drawn.
 */
export const sameAssignee = (a: Assignee | null, b: Assignee | null): boolean =>
  a === null || b === null ? a === b : a.kind === b.kind && a.id === b.id;

/**
 * People first and then keys, each A→Z.
 *
 * The order is the browser's rather than whatever the server happened to
 * return, so a picker does not reshuffle between reads - and people lead
 * because a picker is opened by a person looking for a person, with the keys
 * being the answer to a rarer question.
 */
export const compareAssignees = (a: Assignee, b: Assignee): number =>
  a.kind === b.kind ? a.name.localeCompare(b.name) : a.kind === 'person' ? -1 : 1;

/**
 * What the issue page says under the field, which depends on who owns the
 * ticket - and says the one thing an assignee changes besides the name on the
 * card.
 *
 * A key's ticket is still picked up, and that is worth saying out loud rather
 * than leaving somebody to notice: "people only" is the answer to a question
 * that had three plausible ones, and assigning something to Claude and having
 * Claude stop working on it would read backwards.
 */
export const assigneeHint = (assignee: Assignee | null): string =>
  assignee === null
    ? 'Nobody owns this, so an unattended pass may pick it up.'
    : assignee.kind === 'person'
      ? `${assignee.name} owns this, so an unattended pass leaves it alone.`
      : `${assignee.name} owns this. A key's ticket is still picked up by the loop.`;
