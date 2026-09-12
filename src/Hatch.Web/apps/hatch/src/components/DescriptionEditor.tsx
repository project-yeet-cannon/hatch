/* A description, read and written in one place.

   Two screens edit one now - the issue page and the board's peek - and one
   copy of the behaviour in each is two markdown renderers, two rules for when
   Save is offered and two ways a refusal is reported, which drift. So the
   toggle, the draft, the dirty rule and the empty state live here, and a
   caller is left with the request it owns and the chrome it draws.

   What is deliberately not here: whether the description has arrived. The
   editor is mounted only once there is a string to edit, so `value` is never
   nullable - which is what lets the issue page, where the description comes
   down with the page, use it with no loading branch at all. */

import { useState, type ReactNode } from 'react';
import { Button } from '@hatch/ui';
import { renderMarkdown } from '../lib/markdown';
import { useAutoGrow } from '../lib/useAutoGrow';

export function DescriptionEditor({
  title,
  value,
  onSave,
  error,
  editorClassName,
  previewClassName,
  rows = 16,
}: {
  /** The section's heading, drawn beside the controls. A node rather than a
      string because the level differs: <h2> on the page, <h4> inside a dialog
      whose own title is an <h3>. */
  title: ReactNode;
  /** The description as it is stored. */
  value: string;
  /** Writes the draft, and answers whether it went through. The caller owns
      the request and the error; this never sees a rejection, so `onSave` must
      catch and return false rather than throw. */
  onSave: (next: string) => Promise<boolean>;
  /** What the last save was refused with, in the server's words. Drawn inside
      the section. Left off by a caller that reports errors somewhere else. */
  error?: string | null;
  /** Appended to the textarea's classes: the ceiling differs by call site. */
  editorClassName?: string;
  /** Appended to the rendered markdown's, for a call site that has to cap the
      preview too. The issue page does not - the page scrolls, and a brief that
      runs long should run long there. A dialog does, because a dialog cannot
      be scrolled past its own actions. */
  previewClassName?: string;
  /** The height the box opens at, which differs for the same reason. */
  rows?: number;
}) {
  const [draft, setDraft] = useState(value);
  const [known, setKnown] = useState(value);
  const [preview, setPreview] = useState(true);
  const [saving, setSaving] = useState(false);
  const editor = useAutoGrow(draft);

  // Same reasoning as the title's - see InlineTitle on the issue page. A save
  // landing underneath the editor is the new stored text, and a draft written
  // against the old one must not be left sitting on top of it.
  if (value !== known) {
    setKnown(value);
    setDraft(value);
  }

  // Its own state rather than a `busy` prop: this is exactly the span of the
  // promise it is already awaiting, and two booleans for one await is two
  // things that can come to disagree.
  async function submit() {
    if (saving) return;
    setSaving(true);
    const ok = await onSave(draft);
    setSaving(false);
    // A refusal leaves the draft, the view and the scroll exactly as they
    // were: nothing typed is lost to a failed request.
    if (ok) setPreview(true);
  }

  const dirty = draft !== value;

  return (
    <>
      <div className="hatch-section-head">
        {title}
        <div className="hatch-section-actions">
          <Button onClick={() => setPreview(!preview)}>{preview ? 'Edit' : 'Preview'}</Button>
          {/* Offered on the draft differing alone, in both views - which is
              what makes Edit, Preview, Save work. */}
          {dirty && (
            <Button variant="primary" loading={saving} onClick={() => void submit()}>
              Save
            </Button>
          )}
        </div>
      </div>

      {error && <p className="text-danger">{error}</p>}

      {preview ? (
        // The draft, not the stored text: Preview shows what Save would write.
        draft.trim() ? (
          // Sanitized by renderMarkdown - nothing from the database is trusted markup.
          <div
            className={`hatch-markdown${previewClassName ? ` ${previewClassName}` : ''}`}
            dangerouslySetInnerHTML={{ __html: renderMarkdown(draft) }}
          />
        ) : (
          <p className="text-muted">No description yet.</p>
        )
      ) : (
        // `rows` is the height it opens at and the floor it never goes back
        // under; useAutoGrow measures it rather than being told it.
        <textarea
          ref={editor}
          className={`hatch-description-editor${editorClassName ? ` ${editorClassName}` : ''}`}
          rows={rows}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
        />
      )}
    </>
  );
}
