import type { Note } from './types';

/*
  Everything the list screen derives from a note. Pure, and in its own file
  because it is the part of this module with answers that can be wrong: what an
  untitled note is called, how much of the body stands in for it, and what
  counts as a match when someone types in the search box.

  Search is client-side here, and that is a decision rather than a shortcut. The
  note text is protected at rest (Modules/Quill/QuillContext.cs), so a SQL
  predicate over it would be a predicate over obfuscated bytes: it would compile,
  run, and match nothing. The shell already holds every note this person has, so
  the search that can be correct is the one that runs here.
*/

/** What an untitled note is called. One string, one place, so the list and the editor agree. */
export const UNTITLED = 'Untitled';

/** How much of the body stands in for a note in the list - about two lines on a phone. */
export const PREVIEW_LENGTH = 120;

/**
 * The heading for a note, and whether it is the note's own.
 *
 * Untitled is shown as a state rather than filled in from the first line of the
 * body. Both are defensible and this one is honest: a note whose heading is
 * silently its own first line looks titled, so the person who meant to name it
 * has no way to see that they did not - and the body is right there underneath
 * either way.
 */
export function heading(note: Note): { text: string; untitled: boolean } {
  const title = note.title.trim();
  return title ? { text: title, untitled: false } : { text: UNTITLED, untitled: true };
}

/**
 * The start of the note, on one line. Runs of whitespace collapse to a single
 * space so a body that opens with three blank lines still previews as its first
 * words rather than as nothing.
 */
export function preview(body: string, max = PREVIEW_LENGTH): string {
  const flat = body.replace(/\s+/g, ' ').trim();
  if (flat.length <= max) return flat;

  // Back up to a word boundary, but only one that keeps most of the preview.
  // The threshold is a fraction of the budget rather than a number of
  // characters so it holds at any length: a body whose first word is longer
  // than the preview should be cut mid-word, not reduced to its first letter.
  const cut = flat.slice(0, max);
  const space = cut.lastIndexOf(' ');
  return `${(space >= max * 0.7 ? cut.slice(0, space) : cut).trimEnd()}…`;
}

/**
 * Whether a note answers a search. Case-insensitive over the title and the
 * body, every term having to appear somewhere in one of them - so "fence gate"
 * finds the note that mentions both, in either order and in either field.
 */
export function matches(note: Note, query: string): boolean {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return true;

  const haystack = `${note.title}\n${note.body}`.toLowerCase();
  return terms.every((term) => haystack.includes(term));
}

/**
 * Newest edit first, matching the server's order - so a note written offline
 * and a note read from the API sit in the same place in the list, and the list
 * does not reshuffle when a live load replaces the mirrored one.
 */
export function inDisplayOrder(notes: Note[]): Note[] {
  return [...notes].sort((a, b) => (a.updatedAt === b.updatedAt ? a.id.localeCompare(b.id) : b.updatedAt.localeCompare(a.updatedAt)));
}
