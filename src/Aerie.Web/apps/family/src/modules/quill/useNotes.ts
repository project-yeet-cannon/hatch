import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { errorMessage } from '../../lib/useResource';
import { getNotes } from './api';
import { mirror } from './store';
import { inDisplayOrder } from './noteText';
import type { Note } from './types';

/*
  Quill's data, held once for the whole module.

  Not the shell's useResource, which is right for every other module and wrong
  here in one specific way: it treats a failed read as a failure. A failed read
  in Quill is the ordinary case the app is built for - the plane, the house with
  no internet - and it has an answer, which is the mirror. So the loader here is
  two sources with a preference rather than one source with an error state, and
  what it reports is which one it is showing.

  Holding it in one place (a context under QuillApp) rather than per screen is
  what makes the list instant after an edit: the editor writes the note it just
  saved back into the same array the list reads.
*/

/** Where what is on screen came from. `mirror` means read-only: see writable(). */
export type NotesSource = 'live' | 'mirror';

export interface NotesState {
  notes: Note[];
  source: NotesSource | null;
  loading: boolean;
  /** Set only when there is nothing to show at all - no network *and* no mirror. */
  error: string | null;
  reload: () => void;
  /** Drops in a note the caller already has, so a save costs no round trip. */
  apply: (note: Note) => void;
  forget: (id: string) => void;
}

export const NotesContext = createContext<NotesState | null>(null);

export function useNotes(): NotesState {
  const state = useContext(NotesContext);
  if (!state) throw new Error('useNotes outside QuillApp');
  return state;
}

/** Whether writes should be offered. Mirrored notes are read-only, deliberately - see store.ts. */
export const writable = (source: NotesSource | null) => source === 'live';

/**
 * Loads this person's notes, preferring the API and falling back to the mirror.
 *
 * The order matters and is the whole hook: the mirror is read *first* and
 * rendered immediately, so a cold launch on a plane draws notes rather than a
 * spinner that resolves into an error. The live load then replaces it when one
 * arrives.
 */
export function useNotesState(personId: string): NotesState {
  const [state, setState] = useState<Omit<NotesState, 'reload' | 'apply' | 'forget'>>({
    notes: [],
    source: null,
    loading: true,
    error: null,
  });
  const [attempt, setAttempt] = useState(0);

  // What is on screen, for the write-through below - which must not put a stale
  // array back into the mirror just because a save landed during a reload.
  const current = useRef<Note[]>([]);
  current.current = state.notes;

  useEffect(() => {
    const controller = new AbortController();
    let mirrored: Note[] | null = null;

    void (async () => {
      try {
        mirrored = await mirror.read(personId);
      } catch {
        // A mirror that cannot be read is a mirror that is not there. The live
        // load below is still the answer, and reporting this would be reporting
        // a fallback that failed while the primary is still in flight.
      }

      if (mirrored && !controller.signal.aborted) {
        setState({ notes: inDisplayOrder(mirrored), source: 'mirror', loading: true, error: null });
      }

      try {
        const notes = inDisplayOrder(await getNotes(controller.signal));
        if (controller.signal.aborted) return;

        setState({ notes, source: 'live', loading: false, error: null });
        await mirror.write(personId, notes);
      } catch (err) {
        if (controller.signal.aborted) return;

        setState((prev) =>
          prev.notes.length > 0 || mirrored
            ? // Something to show, and it is honestly labelled. Not an error.
              { notes: prev.notes, source: 'mirror', loading: false, error: null }
            : { notes: [], source: null, loading: false, error: errorMessage(err) },
        );
      }
    })();

    return () => controller.abort();
  }, [personId, attempt]);

  const persist = useCallback(
    (notes: Note[]) => {
      current.current = notes;
      // Written through on every change rather than on unload: a phone loses an
      // app to the OS without warning, and a mirror that only saved on the way
      // out would be a mirror that is empty exactly when it matters.
      void mirror.write(personId, notes).catch(() => undefined);
      return notes;
    },
    [personId],
  );

  return {
    ...state,
    reload: useCallback(() => setAttempt((n) => n + 1), []),
    apply: useCallback(
      (note: Note) =>
        setState((prev) => ({
          ...prev,
          notes: persist(inDisplayOrder([...prev.notes.filter((n) => n.id !== note.id), note])),
        })),
      [persist],
    ),
    forget: useCallback(
      (id: string) => setState((prev) => ({ ...prev, notes: persist(prev.notes.filter((n) => n.id !== id)) })),
      [persist],
    ),
  };
}
