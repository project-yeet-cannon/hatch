import { asJson, createClient } from '../../lib/http';
import type { Note, NoteWriteRequest } from './types';

/**
 * Quill's routes. The client underneath - the sign-in redirect, abort signals,
 * the server's own error message - is the shell's createClient (lib/http.ts).
 *
 * Every one of these answers 404 for a device with no person behind it, which
 * is the same answer it gives for someone else's note id. The module never sees
 * that case in practice: the shell does not show Quill to a session with no
 * person at all (modules/registry.ts). Belt and braces, in that order - the
 * braces are the ones holding.
 */
const { fetchJson } = createClient('/api/quill');

/** Everything this person has, newest edit first - one request, whole notes, which is what fills the mirror. */
export const getNotes = (signal?: AbortSignal) => fetchJson<Note[]>('/notes', { signal });

export const getNote = (id: string, signal?: AbortSignal) => fetchJson<Note>(`/notes/${id}`, { signal });

export const createNote = (request: NoteWriteRequest) =>
  fetchJson<Note>('/notes', { method: 'POST', ...asJson(request) });

export const updateNote = (id: string, request: NoteWriteRequest) =>
  fetchJson<Note>(`/notes/${id}`, { method: 'PUT', ...asJson(request) });

export const deleteNote = (id: string) => fetchJson<void>(`/notes/${id}`, { method: 'DELETE' });
