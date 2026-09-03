import { describe, expect, it } from 'vitest';
import { EMPTY_FORM, KEEP, buildBulkEdit, isEmptyForm, summarize } from './bulk';
import type { BulkForm } from './bulk';

const form = (over: Partial<BulkForm> = {}): BulkForm => ({ ...EMPTY_FORM, ...over });
const KEYS = ['AER-1', 'AER-2'];

describe('buildBulkEdit', () => {
  it('sends nothing when nothing was chosen', () => {
    expect(buildBulkEdit([], form({ statusId: 3 }))).toBeNull();
    expect(buildBulkEdit(KEYS, EMPTY_FORM)).toBeNull();
  });

  /* The property the whole module exists for. A field left alone must not
     appear in the body at all - an empty string there is the clear, and a form
     that sends one it did not mean outdents fifty issues without erroring. */
  it('names only the fields that were touched', () => {
    expect(buildBulkEdit(KEYS, form({ statusId: 3 }))).toEqual({ keys: KEYS, statusId: 3 });
  });

  it('carries a new type and a new column together', () => {
    expect(buildBulkEdit(KEYS, form({ type: 'bug', statusId: 3 }))).toEqual({
      keys: KEYS,
      type: 'bug',
      statusId: 3,
    });
  });

  it('sends a new parent as its key', () => {
    expect(buildBulkEdit(KEYS, form({ parent: 'AER-9' }))?.parentKey).toBe('AER-9');
  });

  it('sends the empty string when the parent is being cleared', () => {
    expect(buildBulkEdit(KEYS, form({ parent: '' }))?.parentKey).toBe('');
  });

  it('leaves parentKey off entirely when the parent is being left alone', () => {
    const request = buildBulkEdit(KEYS, form({ parent: KEEP, statusId: 3 }))!;
    expect('parentKey' in request).toBe(false);
  });

  it('sends a date only when its box is ticked, and the empty string clears it', () => {
    expect(buildBulkEdit(KEYS, form({ setDue: true, dueAt: '2027-09-01' }))?.dueAt).toBe('2027-09-01');
    expect(buildBulkEdit(KEYS, form({ setDue: true, dueAt: '' }))?.dueAt).toBe('');
  });

  /* A date typed and then unticked is a date nobody meant to send. The value is
     kept in the form so unticking is undoable, and dropped here. */
  it('ignores a date that was typed and then unticked', () => {
    const request = buildBulkEdit(KEYS, form({ setDue: false, dueAt: '2027-09-01', statusId: 3 }))!;
    expect('dueAt' in request).toBe(false);
  });

  it('carries both dates when both are being set', () => {
    const request = buildBulkEdit(KEYS, form({ setReady: true, readyAt: '2027-08-15', setDue: true, dueAt: '' }))!;
    expect(request.readyAt).toBe('2027-08-15');
    expect(request.dueAt).toBe('');
  });
});

describe('isEmptyForm', () => {
  it('is true only for a form that would change nothing', () => {
    expect(isEmptyForm(EMPTY_FORM)).toBe(true);
    expect(isEmptyForm(form({ type: 'task' }))).toBe(false);
    expect(isEmptyForm(form({ parent: '' }))).toBe(false);
    expect(isEmptyForm(form({ setReady: true }))).toBe(false);
  });
});

describe('summarize', () => {
  it('says only what happened', () => {
    expect(summarize({ changed: ['AER-1'], unchanged: [], failures: [] })).toBe('1 changed');
  });

  it('says the boring parts too, because they are the answer to "why did nothing happen"', () => {
    expect(
      summarize({
        changed: [],
        unchanged: ['AER-1', 'AER-2'],
        failures: [{ key: 'AER-3', reason: 'a task hangs under a story' }],
      }),
    ).toBe('0 changed, 2 already like that, 1 refused');
  });
});
