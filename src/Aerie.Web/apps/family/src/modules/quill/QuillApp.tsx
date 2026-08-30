import { Navigate, Route, Routes } from 'react-router-dom';
import { useSession } from '../../lib/session';
import { NotesContext, useNotesState } from './useNotes';
import { NotesPage } from './NotesPage';
import { NotePage } from './NotePage';
import { notesPath } from './routes';
import './quill.css';

/**
 * Quill's slice of the shell. Everything under /apps/family/quill/ is routed
 * here and nowhere else.
 *
 * The session guard below is belt to the API's braces. The shell does not put
 * Quill on the home screen or in the tab bar without a person
 * (modules/registry.ts), and the API answers 404 to a caller without one - so
 * this branch is reachable only in the seam between them: a person unlinked
 * while the app is open. It renders nothing rather than an explanation, for the
 * same reason the API's refusal is blank.
 */
export default function QuillApp() {
  const { session } = useSession();

  return session?.personId ? <Notes key={session.personId} personId={session.personId} /> : null;
}

/**
 * The notes themselves, held once for the module so the list and the editor are
 * the same array - an edit is on the list the moment you back out of it, with
 * no refetch and no flicker.
 *
 * Keyed by person: re-linking the device to someone else has to build a new
 * state rather than merge into the old one, and a key is the version of that
 * which cannot be forgotten in an effect.
 */
function Notes({ personId }: { personId: string }) {
  const notes = useNotesState(personId);

  return (
    <NotesContext.Provider value={notes}>
      <Routes>
        <Route index element={<NotesPage />} />
        <Route path="new" element={<NotePage />} />
        <Route path=":noteId" element={<NotePage />} />
        <Route path="*" element={<Navigate to={notesPath} replace />} />
      </Routes>
    </NotesContext.Provider>
  );
}
