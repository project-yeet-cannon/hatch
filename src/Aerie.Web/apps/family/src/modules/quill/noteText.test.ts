import { describe, expect, it } from 'vitest';
import { UNTITLED, heading, inDisplayOrder, matches, preview } from './noteText';
import type { Note } from './types';

const note = (over: Partial<Note> = {}): Note => ({
  id: 'a',
  title: '',
  body: '',
  createdAt: '2026-08-30T09:00:00Z',
  updatedAt: '2026-08-30T09:00:00Z',
  ...over,
});

describe('heading', () => {
  it('is the note’s own title when it has one', () => {
    expect(heading(note({ title: 'Fence' }))).toEqual({ text: 'Fence', untitled: false });
  });

  it('reports untitled rather than borrowing the first line of the body', () => {
    // The whole point: a note that shows its first line as a title looks named,
    // so whoever meant to name it never finds out they didn't.
    expect(heading(note({ body: 'two posts and the gate latch' }))).toEqual({ text: UNTITLED, untitled: true });
  });

  it('treats a title of only whitespace as no title', () => {
    expect(heading(note({ title: '   ' })).untitled).toBe(true);
  });
});

describe('preview', () => {
  it('collapses whitespace, so a note that opens with blank lines still previews', () => {
    expect(preview('\n\n   two    posts\nand a gate  ')).toBe('two posts and a gate');
  });

  it('leaves a short body whole and unellipsised', () => {
    expect(preview('milk')).toBe('milk');
  });

  it('cuts at a word boundary when there is one near the limit', () => {
    const cut = preview('alpha bravo charlie delta echo foxtrot', 20);

    expect(cut).toBe('alpha bravo charlie…');
    expect(cut).not.toContain('delta');
  });

  it('cuts mid-word rather than losing most of the preview to one long word', () => {
    // A word boundary at character 2 is not worth an 18-character preview.
    expect(preview('a bcdefghijklmnopqrstuvwxyz', 20)).toBe('a bcdefghijklmnopqrs…');
  });

  it('is empty for an empty body, so the list can leave the line out', () => {
    expect(preview('   \n  ')).toBe('');
  });
});

describe('matches', () => {
  it('matches nothing in particular when the query is empty', () => {
    expect(matches(note({ title: 'Fence' }), '   ')).toBe(true);
  });

  it('searches the title and the body alike, ignoring case', () => {
    expect(matches(note({ title: 'Fence' }), 'FENCE')).toBe(true);
    expect(matches(note({ body: 'the gate LATCH' }), 'latch')).toBe(true);
  });

  it('needs every term, in either field and in any order', () => {
    const n = note({ title: 'Fence', body: 'the gate latch' });

    expect(matches(n, 'latch fence')).toBe(true);
    expect(matches(n, 'fence hinge')).toBe(false);
  });
});

describe('inDisplayOrder', () => {
  it('puts the most recently edited first, matching the server', () => {
    const older = note({ id: 'older', updatedAt: '2026-08-30T09:00:00Z' });
    const newer = note({ id: 'newer', updatedAt: '2026-08-30T10:00:00Z' });

    expect(inDisplayOrder([older, newer]).map((n) => n.id)).toEqual(['newer', 'older']);
  });

  it('breaks a tie by id, so two notes saved in the same tick hold still', () => {
    const a = note({ id: 'a' });
    const b = note({ id: 'b' });

    expect(inDisplayOrder([b, a]).map((n) => n.id)).toEqual(['a', 'b']);
  });

  it('does not reorder the array it was given', () => {
    const older = note({ id: 'older', updatedAt: '2026-08-30T09:00:00Z' });
    const newer = note({ id: 'newer', updatedAt: '2026-08-30T10:00:00Z' });
    const input = [older, newer];

    inDisplayOrder(input);

    expect(input.map((n) => n.id)).toEqual(['older', 'newer']);
  });
});
