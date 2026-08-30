import { useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ErrorNote, Loading } from '../../components/Notices';
import { useNotes, writable } from './useNotes';
import { heading, matches, preview } from './noteText';
import { newNotePath, notePath } from './routes';
import type { Note } from './types';

/**
 * Every note, newest edit first, with the one control that matters at the
 * bottom of the thumb's reach.
 *
 * Search is client-side over the notes already in hand. That is not a shortcut:
 * the note text is protected at rest, so a server-side predicate would run
 * against obfuscated bytes and match nothing (noteText.ts).
 */
export function NotesPage() {
  const { notes, source, loading, error, reload } = useNotes();
  const [query, setQuery] = useState('');
  const navigate = useNavigate();

  const shown = useMemo(() => notes.filter((note) => matches(note, query)), [notes, query]);

  if (loading && notes.length === 0) return <Loading />;
  if (error && notes.length === 0) return <ErrorNote message={error} onRetry={reload} />;

  return (
    <div className="quill-list-page">
      {source === 'mirror' && (
        <p className="quill-mirrored" role="status">
          Saved on this device — read only until Aerie is reachable
        </p>
      )}

      {notes.length > 0 && (
        <input
          type="search"
          className="quill-search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search notes"
          aria-label="Search notes"
        />
      )}

      {shown.length === 0 ? (
        <div className="note">
          {notes.length === 0 ? <p>Nothing written down yet.</p> : <p>No note matches “{query}”.</p>}
        </div>
      ) : (
        <div className="quill-list">
          {shown.map((note) => (
            <NoteCard key={note.id} note={note} />
          ))}
        </div>
      )}

      {/* Fixed rather than in the flow: the reason to open this app is usually
          to write something, and that should not require scrolling past
          everything already written. */}
      <button
        type="button"
        className="btn-primary quill-new"
        onClick={() => navigate(newNotePath)}
        disabled={!writable(source)}
        aria-label="New note"
      >
        <span aria-hidden="true">✎</span> New
      </button>
    </div>
  );
}

/**
 * The heading, then the start of the note.
 *
 * An untitled note shows both: the placeholder says it was never named, and the
 * preview under it says what it is anyway. Showing only the first line as a
 * pretend title would look named, which is how a note nobody titled becomes a
 * note nobody can find.
 */
function NoteCard({ note }: { note: Note }) {
  const { text, untitled } = heading(note);
  const body = preview(note.body);

  return (
    <Link to={notePath(note.id)} className="card quill-card">
      <span className={`quill-card-title${untitled ? ' quill-untitled' : ''}`}>{text}</span>
      {body && <span className="quill-card-preview">{body}</span>}
      <span className="quill-card-when">{when(note.updatedAt)}</span>
    </Link>
  );
}

/**
 * A time a person reads at a glance: the clock today, the weekday this week,
 * the date beyond that. Locale-formatted, because this runs on the phone of
 * whoever is reading it.
 */
function when(iso: string): string {
  const at = new Date(iso);
  if (Number.isNaN(at.getTime())) return '';

  const days = (Date.now() - at.getTime()) / 86_400_000;
  if (days < 1) return at.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  if (days < 7) return at.toLocaleDateString(undefined, { weekday: 'long' });
  return at.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}
