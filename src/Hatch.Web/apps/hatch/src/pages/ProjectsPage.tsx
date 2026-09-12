import { useState } from 'react';
import { Button, Card, EmptyState, Field, Modal, PageHeader, Table } from '@hatch/ui';
import { createProject, deleteProject, getProjects, patchProject } from '../api/client';
import { message } from '../lib/errors';
import { normalizeProjectKey, rekeyObjection } from '../lib/projectKey';
import { useLoaded } from '../lib/useLoaded';
import type { Project } from '../types';

export function ProjectsPage() {
  const { data: projects, error, setError, reload } = useLoaded<Project[]>(getProjects);
  const [key, setKey] = useState('');
  const [name, setName] = useState('');
  const [saving, setSaving] = useState(false);
  const [rekeying, setRekeying] = useState<Project | null>(null);

  async function act(action: () => Promise<unknown>) {
    try {
      await action();
      await reload();
      setError(null);
    } catch (err) {
      setError(message(err));
    }
  }

  async function create() {
    setSaving(true);
    await act(async () => {
      await createProject({ key, name });
      setKey('');
      setName('');
    });
    setSaving(false);
  }

  return (
    <div className="hatch-page">
      <PageHeader
        title="Projects"
        description="A project is a key namespace. Its key can be changed, at a price this page states before it lets you."
      />

      {error && <p className="text-danger">{error}</p>}

      <Card>
        <h2 className="hatch-section-title">New project</h2>
        <div className="hatch-field-grid">
          <Field label="Key" hint="Two to six characters, e.g. AER.">
            <input value={key} onChange={(e) => setKey(e.target.value)} />
          </Field>
          <Field label="Name" hint="What it is called out loud.">
            <input value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
        </div>
        <div className="hatch-form-actions">
          <Button variant="primary" loading={saving} disabled={!key || !name} onClick={() => void create()}>
            Add project
          </Button>
        </div>
      </Card>

      {projects?.length === 0 && <EmptyState message="No projects yet." />}

      {projects && projects.length > 0 && (
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Key</th>
                <th>Name</th>
                <th>Issues</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {projects.map((project) => (
                <tr key={project.id}>
                  <td>
                    <code>{project.key}</code>
                  </td>
                  <td>
                    <NameCell project={project} onRename={(next) => void act(() => patchProject(project.id, { name: next }))} />
                  </td>
                  <td>{project.issueCount}</td>
                  <td>
                    <div className="hatch-row-actions">
                      <Button onClick={() => setRekeying(project)}>Change key</Button>
                      {/* Greyed rather than offered and refused: the server
                          returns the same 409 either way, and a button that
                          cannot work is better disabled than apologetic. */}
                      <Button
                        variant="danger"
                        disabled={project.issueCount > 0}
                        onClick={() => void act(() => deleteProject(project.id))}
                      >
                        Delete
                      </Button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </Table>
        </Card>
      )}

      <RekeyDialog
        project={rekeying}
        onClose={() => setRekeying(null)}
        onRekey={async (next) => {
          await act(() => patchProject(rekeying!.id, { key: next }));
          setRekeying(null);
        }}
      />
    </div>
  );
}

function NameCell({ project, onRename }: { project: Project; onRename: (name: string) => void }) {
  const [draft, setDraft] = useState(project.name);

  return (
    <input
      value={draft}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => {
        if (draft.trim() && draft !== project.name) onRename(draft.trim());
      }}
    />
  );
}

/**
 * The speed bump.
 *
 * A rekey is a real edit with a real cost, and the two halves of that cost are
 * worth separating because only one of them is scary: the tree survives - every
 * story stays under its epic, every task under its story, because parentage is
 * a foreign key and the number is the issue's own - while every AER-12 written
 * into a commit message, a branch name or a chat log stops resolving, because
 * those are references nothing here has ever seen.
 *
 * So the dialog states both, names how many issues are about to be renumbered,
 * and asks for the old key to be typed out. Typing it is the moment somebody
 * reads what they are about to break; the rules themselves live in
 * lib/projectKey.ts.
 */
function RekeyDialog({
  project,
  onClose,
  onRekey,
}: {
  project: Project | null;
  onClose: () => void;
  onRekey: (key: string) => Promise<void>;
}) {
  const [next, setNext] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [saving, setSaving] = useState(false);

  // Reset when a different project is picked, so a half-typed confirmation
  // cannot be carried from one row to another.
  const [opened, setOpened] = useState<number | null>(null);
  if (project && project.id !== opened) {
    setOpened(project.id);
    setNext('');
    setConfirmation('');
  }

  if (!project) return <Modal open={false} onClose={onClose} title="" />;

  const objection = rekeyObjection(project.key, next, confirmation);
  const example = normalizeProjectKey(next) || 'NEW';

  async function submit() {
    setSaving(true);
    try {
      await onRekey(normalizeProjectKey(next));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal open onClose={onClose} title={`Change ${project.key}'s key`}>
      <div className="hatch-form">
        <p className="text-danger">
          {project.issueCount} issue{project.issueCount === 1 ? '' : 's'} will be renumbered: {project.key}-12 becomes{' '}
          {example}-12. Every {project.key}-12 written into a commit message, a branch name or a chat log stops
          resolving, and nothing here can rewrite those.
        </p>
        <p className="text-muted">
          The structure comes through intact - stories stay under their epics and tasks under their stories, because
          those links are not the key.
        </p>

        <Field label="New key" hint="Two to six characters, e.g. OPS.">
          <input value={next} autoFocus onChange={(e) => setNext(e.target.value.toUpperCase())} />
        </Field>

        <Field label={`Type ${project.key} to confirm`}>
          <input value={confirmation} onChange={(e) => setConfirmation(e.target.value.toUpperCase())} />
        </Field>

        <div className="hatch-form-actions">
          {objection && <span className="text-muted">{objection}</span>}
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="danger" loading={saving} disabled={objection !== null} onClick={() => void submit()}>
            Change the key
          </Button>
        </div>
      </div>
    </Modal>
  );
}
