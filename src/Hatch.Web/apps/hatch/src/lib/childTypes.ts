/* What may be filed under an issue.

   The inverse of LEGAL_PARENT_TYPES, which answers "what may this hang under".
   Derived rather than written out a second time: a table saying a story hangs
   under an epic and a picker saying an epic takes stories are two statements of
   one fact, and the second one is the one that goes stale.

   Ordered by ISSUE_TYPES, so the offer reads epic, story, task, bug wherever it
   is drawn - and so the first entry, which is what a picker starts on, is the
   ordinary child rather than whichever type happened to sort first. */

import { ISSUE_TYPES, LEGAL_PARENT_TYPES } from '../types';
import type { IssueType } from '../types';

/**
 * The types the server would accept under an issue of this type, in the order
 * a picker offers them. Empty for a task - nothing hangs under one.
 */
export const childTypes = (parent: IssueType): IssueType[] =>
  ISSUE_TYPES.filter((type) => LEGAL_PARENT_TYPES[type].includes(parent));
