import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { errorMessage } from '../../lib/useResource';
import { createNote, deleteNote, updateNote } from './api';
import { useNotes, writable } from './useNotes';
import { UNTITLED } from './noteText';
import { notePath, notesPath } from './routes';

/**
 * The writing surface, and as little else as will fit.
 *
 * Three things drive the whole screen:
 *
 * - **It opens on the body.** The title sits above it and is scrolled out of
 *   view on a phone, so the first keystroke goes into the note rather than into
 *   a field asking what the note is going to be about. Scrolling up finds it.
 * - **There is no save button.** A note saves itself as you stop typing, which
 *   is the only interaction that matches "I want to start writing" - a Save
 *   that can be missed is a note that can be lost.
 * - **Offline is read-only**, and says which. The mirror makes every note
 *   readable with no network (store.ts); writing them back would need conflict
 *   resolution that nothing here justifies yet.
 */

/** How long a pause counts as "stopped typing". Long enough not to save mid-word, short enough to beat a pocket. */
const SAVE_AFTER_MS = 800;

type SaveState = 'idle' | 'saving' | 'saved' | 'failed';

export function NotePage() {
  const { noteId } = useParams();
  const navigate = useNavigate();
  const { notes, source, loading, apply, forget } = useNotes();

  const existing = noteId ? notes.find((n) => n.id === noteId) : undefined;
  const readOnly = !writable(source);

  const [id, setId] = useState(noteId === 'new' ? null : (noteId ?? null));
  const [title, setTitle] = useState(existing?.title ?? '');
  const [body, setBody] = useState(existing?.body ?? '');
  const [save, setSave] = useState<SaveState>('idle');
  const [error, setError] = useState<string | null>(null);
  const [ready, setReady] = useState(existing !== undefined || noteId === 'new');

  // What the server last accepted. Everything else compares against this, so a
  // save that lands does not immediately schedule another one.
  const saved = useRef({ title: existing?.title ?? '', body: existing?.body ?? '' });
  const scrollRef = useRef<HTMLDivElement>(null);
  const bodyRef = useRef<HTMLTextAreaElement>(null);

  // A note that is not in the list yet: the list arrived after this screen did,
  // which is the ordinary cold-open case for a link into a note.
  useEffect(() => {
    if (ready || !existing) return;
    setTitle(existing.title);
    setBody(existing.body);
    saved.current = { title: existing.title, body: existing.body };
    setReady(true);
  }, [existing, ready]);

  const persist = useCallback(
    async (next: { title: string; body: string }) => {
      if (next.title === saved.current.title && next.body === saved.current.body) return;
      // An untouched new note is not a note. Backing out of one leaves nothing
      // behind, which is what makes tapping New free.
      if (id === null && !next.title.trim() && !next.body.trim()) return;

      setSave('saving');
      setError(null);
      try {
        const note = id === null ? await createNote(next) : await updateNote(id, next);
        saved.current = { title: note.title, body: note.body };
        apply(note);
        if (id === null) {
          setId(note.id);
          // Replace, not push: Back should leave the editor, not return to the
          // blank note this one just stopped being.
          navigate(notePath(note.id), { replace: true });
        }
        setSave('saved');
      } catch (err) {
        setSave('failed');
        setError(errorMessage(err));
      }
    },
    [apply, id, navigate],
  );

  // Autosave. The timer restarts on every keystroke, so this fires once per
  // pause rather than once per character.
  useEffect(() => {
    if (readOnly || !ready) return;
    const timer = setTimeout(() => void persist({ title, body }), SAVE_AFTER_MS);
    return () => clearTimeout(timer);
  }, [title, body, persist, readOnly, ready]);

  // Leaving the screen inside the debounce window - tapping back, or the OS
  // taking the app - must not cost the last few seconds of typing. Guarded on
  // "differs from what was saved", so React's development double-mount does not
  // turn an untouched open into a write.
  const pending = useRef({ title, body });
  pending.current = { title, body };
  useEffect(() => {
    if (readOnly) return;
    return () => void persist(pending.current);
  }, [persist, readOnly]);

  // Open on the body: the title is above it and scrolled past. Only where the
  // screen is small enough for that to be the trade - on a laptop there is room
  // for both, and hiding a field that fits would be a puzzle rather than a
  // focus.
  useLayoutEffect(() => {
    const scroller = scrollRef.current;
    if (!scroller || !window.matchMedia('(max-width: 899px)').matches) return;

    const body = bodyRef.current;
    if (body) scroller.scrollTop = body.offsetTop - scroller.offsetTop;
    if (!readOnly && !existing) body?.focus();
  }, [readOnly, existing]);

  // Grow with the text, so the container scrolls rather than the textarea -
  // which is what keeps the title reachable by scrolling up.
  useLayoutEffect(() => {
    const el = bodyRef.current;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = `${el.scrollHeight}px`;
  }, [body]);

  const remove = async () => {
    if (id === null) {
      navigate(notesPath, { replace: true });
      return;
    }
    try {
      await deleteNote(id);
      forget(id);
      navigate(notesPath, { replace: true });
    } catch (err) {
      setError(errorMessage(err));
    }
  };

  // A note this device has never seen, with no way to ask for it. The one
  // honest sentence: it exists, and not here.
  if (!ready && !loading && readOnly) {
    return (
      <div className="note">
        <p>That note isn’t on this device, and Aerie is unreachable.</p>
      </div>
    );
  }
  if (!ready) return <div className="note">Loading…</div>;

  return (
    <div className="quill-editor">
      <div className="quill-scroll" ref={scrollRef}>
        <input
          type="text"
          className="quill-title"
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          placeholder={UNTITLED}
          aria-label="Title"
          readOnly={readOnly}
          maxLength={120}
        />
        <textarea
          className="quill-body"
          ref={bodyRef}
          value={body}
          onChange={(e) => setBody(e.target.value)}
          placeholder="Start writing…"
          aria-label="Note"
          readOnly={readOnly}
          spellCheck
        />
      </div>

      <div className="quill-bar">
        <span className={`quill-status${save === 'failed' ? ' quill-status-failed' : ''}`} role="status">
          {readOnly ? 'Read only — saved on this device' : statusText(save, error)}
        </span>
        {!readOnly && (
          <button type="button" className="quill-delete" onClick={() => void remove()}>
            Delete
          </button>
        )}
      </div>
    </div>
  );
}

function statusText(save: SaveState, error: string | null): string {
  switch (save) {
    case 'saving':
      return 'Saving…';
    case 'saved':
      return 'Saved';
    case 'failed':
      // The server's own sentence, which is the actual explanation - "couldn't
      // save" is not.
      return error ?? "Couldn't save";
    default:
      return '';
  }
}
