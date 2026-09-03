/* The bulk edit form, and the request it becomes.

   Every field on the form has three possible meanings and the API reads two of
   them from one JSON value: absent is "leave this alone", the empty string is
   "clear this", and anything else is the new value (IssuePatchRequest in
   Dtos.cs). A form control has no absent - a <select> always has a value - so
   the third state is spelled here, once, rather than being reinvented per
   field.

   This is the module the whole page hangs on, and it is separate from the page
   for one reason: getting it slightly wrong is silent. A form that sends
   `parentKey: ''` where it meant to send nothing does not fail - it quietly
   outdents fifty issues. */

import type { IssueBulkEditRequest, IssueBulkResult, IssueType } from '../types';

/** The value a picker carries when it is not being used. Never a real key: no issue key is empty and none is this. */
export const KEEP = '__keep__';

export interface BulkForm {
  /** '' leaves the type alone. */
  type: IssueType | '';
  /** '' leaves the column alone. */
  statusId: number | '';
  /** KEEP leaves the parent alone, '' clears it, anything else is the new parent's key. */
  parent: string;
  /** Whether the ready date is being touched at all. */
  setReady: boolean;
  /** The wire form, or '' for the clear. Only read when setReady. */
  readyAt: string;
  setDue: boolean;
  dueAt: string;
}

export const EMPTY_FORM: BulkForm = {
  type: '',
  statusId: '',
  parent: KEEP,
  setReady: false,
  readyAt: '',
  setDue: false,
  dueAt: '',
};

/** Whether the form would change anything, which is what enables the button. */
export const isEmptyForm = (form: BulkForm): boolean =>
  form.type === '' && form.statusId === '' && form.parent === KEEP && !form.setReady && !form.setDue;

/**
 * The request, or null when there is nothing to send - no issues chosen, or a
 * form that names no change. Null rather than an empty request: the API refuses
 * both, and a refusal the browser could have avoided is a round trip spent
 * telling somebody what they already knew.
 */
export function buildBulkEdit(keys: string[], form: BulkForm): IssueBulkEditRequest | null {
  if (keys.length === 0 || isEmptyForm(form)) return null;

  const request: IssueBulkEditRequest = { keys };

  if (form.type !== '') request.type = form.type;
  if (form.statusId !== '') request.statusId = form.statusId;
  if (form.parent !== KEEP) request.parentKey = form.parent;
  if (form.setReady) request.readyAt = form.readyAt;
  if (form.setDue) request.dueAt = form.dueAt;

  return request;
}

/**
 * What happened, in one line. Says the boring parts out loud on purpose:
 * "already like that" is the answer to "why did nothing happen", and it is the
 * ordinary result of pressing Apply twice.
 */
export function summarize(result: IssueBulkResult): string {
  const parts = [`${result.changed.length} changed`];
  if (result.unchanged.length > 0) parts.push(`${result.unchanged.length} already like that`);
  if (result.failures.length > 0) parts.push(`${result.failures.length} refused`);
  return parts.join(', ');
}
