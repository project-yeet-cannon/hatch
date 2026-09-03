import { useState } from 'react';
import { Button, Card, Field, PageHeader, Table } from '@aerie/ui';
import { createStatus, deleteStatus, getStatuses, patchStatus } from '../api/client';
import { message } from '../lib/errors';
import { useLoaded } from '../lib/useLoaded';
import type { Status } from '../types';

export function StatusesPage() {
  const { data: statuses, error, setError, reload } = useLoaded<Status[]>(getStatuses);
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
      await createStatus({ name });
      setName('');
    });
    setSaving(false);
  }

  /**
   * Reordering by swapping two rows' SortOrder values rather than by
   * renumbering the list. Two writes, no renumber, and a column that was
   * hand-placed between two others keeps whatever number it was given.
   */
  function swap(list: Status[], index: number, delta: number) {
    const a = list[index];
    const b = list[index + delta];
    if (!b) return;
    void act(async () => {
      await patchStatus(a.id, { sortOrder: b.sortOrder });
      await patchStatus(b.id, { sortOrder: a.sortOrder });
    });
  }

  return (
    <div className="hatch-page">
      <PageHeader title="Statuses" description="One status is one column on the board, left to right." />

      {error && <p className="text-danger">{error}</p>}

      <Card>
        <div className="hatch-inline-form">
          <Field label="Name">
            <input value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <Button variant="primary" loading={saving} disabled={!name} onClick={() => void create()}>
            Add column
          </Button>
        </div>
      </Card>

      {statuses && (
        <Card flush>
          <Table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Done column</th>
                <th>Order</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {statuses.map((status, index) => (
                <tr key={status.id}>
                  <td>
                    <NameCell status={status} onRename={(next) => void act(() => patchStatus(status.id, { name: next }))} />
                  </td>
                  <td>
                    <input
                      type="checkbox"
                      checked={status.isTerminal}
                      aria-label={`${status.name} means shipped`}
                      onChange={(e) => void act(() => patchStatus(status.id, { isTerminal: e.target.checked }))}
                    />
                  </td>
                  <td>
                    <div className="hatch-reorder">
                      <Button disabled={index === 0} aria-label={`Move ${status.name} left`} onClick={() => swap(statuses, index, -1)}>
                        ←
                      </Button>
                      <Button
                        disabled={index === statuses.length - 1}
                        aria-label={`Move ${status.name} right`}
                        onClick={() => swap(statuses, index, 1)}
                      >
                        →
                      </Button>
                    </div>
                  </td>
                  <td>
                    <Button variant="danger" onClick={() => void act(() => deleteStatus(status.id))}>
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

function NameCell({ status, onRename }: { status: Status; onRename: (name: string) => void }) {
  const [draft, setDraft] = useState(status.name);

  return (
    <input
      value={draft}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={() => {
        if (draft.trim() && draft !== status.name) onRename(draft.trim());
      }}
    />
  );
}
