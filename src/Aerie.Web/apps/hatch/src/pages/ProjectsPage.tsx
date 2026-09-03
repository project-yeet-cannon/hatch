import { useState } from 'react';
import { Button, Card, EmptyState, Field, PageHeader, Table } from '@aerie/ui';
import { createProject, deleteProject, getProjects, patchProject } from '../api/client';
import { message } from '../lib/errors';
import { useLoaded } from '../lib/useLoaded';
import type { Project } from '../types';

export function ProjectsPage() {
  const { data: projects, error, setError, reload } = useLoaded<Project[]>(getProjects);
  const [key, setKey] = useState('');
  const [name, setName] = useState('');
  const [saving, setSaving] = useState(false);

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
      <PageHeader title="Projects" description="A project is a key namespace. Its key never changes." />

      {error && <p className="text-danger">{error}</p>}

      <Card>
        <div className="hatch-inline-form">
          <Field label="Key" hint="Two to six characters, e.g. AER.">
            <input value={key} onChange={(e) => setKey(e.target.value)} />
          </Field>
          <Field label="Name">
            <input value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
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
                  </td>
                </tr>
              ))}
            </tbody>
          </Table>
        </Card>
      )}
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
