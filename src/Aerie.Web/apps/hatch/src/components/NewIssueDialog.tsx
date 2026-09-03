import { useState } from 'react';
import { Button, Field, Modal } from '@aerie/ui';
import { createIssue } from '../api/client';
import { message } from '../lib/errors';
import { ISSUE_TYPES } from '../types';
import type { IssueType, Project } from '../types';

/**
 * Filing an issue: a project, a type, a title, and somewhere to start writing.
 * No status and no position - the server puts a new issue in the leftmost
 * column at the bottom, so there is nothing here to get wrong.
 */
export function NewIssueDialog({
  open,
  projects,
  onClose,
  onCreated,
}: {
  open: boolean;
  projects: Project[];
  onClose: () => void;
  onCreated: () => void;
}) {
  const [projectId, setProjectId] = useState<number | null>(null);
  const [type, setType] = useState<IssueType>('task');
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // The first project, until somebody picks another. One project is the
  // ordinary case and choosing it for them is one fewer thing to do.
  const chosen = projectId ?? projects[0]?.id ?? null;

  async function submit() {
    if (chosen === null) {
      setError('there are no projects yet - make one on the Projects page');
      return;
    }

    setSaving(true);
    try {
      await createIssue({ projectId: chosen, type, title, description });
      setTitle('');
      setDescription('');
      setError(null);
      onCreated();
      onClose();
    } catch (err) {
      setError(message(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="New issue">
      <div className="hatch-form">
        <Field label="Project">
          <select value={chosen ?? ''} onChange={(e) => setProjectId(Number(e.target.value))}>
            {projects.map((p) => (
              <option key={p.id} value={p.id}>
                {p.key} — {p.name}
              </option>
            ))}
          </select>
        </Field>

        <Field label="Type">
          <select value={type} onChange={(e) => setType(e.target.value as IssueType)}>
            {ISSUE_TYPES.map((t) => (
              <option key={t} value={t}>
                {t}
              </option>
            ))}
          </select>
        </Field>

        <Field label="Title">
          <input value={title} onChange={(e) => setTitle(e.target.value)} autoFocus />
        </Field>

        <Field label="Description" hint="Markdown, rendered on the issue page.">
          <textarea rows={6} value={description} onChange={(e) => setDescription(e.target.value)} />
        </Field>

        {error && <p className="text-danger">{error}</p>}

        <div className="hatch-form-actions">
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={saving} onClick={() => void submit()}>
            File it
          </Button>
        </div>
      </div>
    </Modal>
  );
}
