import { useEffect, useState } from 'react';
import type { Zone, ZoneKind, ZoneWriteRequest } from '../types';
import { createZone, deleteZone, getZones, updateZone } from '../api/client';

interface ZoneFormState {
  name: string;
  kind: ZoneKind;
  comfortLowF: string;
  comfortHighF: string;
  sortOrder: string;
  included: boolean;
}

const emptyForm = (nextSortOrder: number): ZoneFormState => ({
  name: '',
  kind: 'Interior',
  comfortLowF: '',
  comfortHighF: '',
  sortOrder: String(nextSortOrder),
  included: true,
});

const toFormState = (zone: Zone): ZoneFormState => ({
  name: zone.name,
  kind: zone.kind,
  comfortLowF: zone.comfortLowF?.toString() ?? '',
  comfortHighF: zone.comfortHighF?.toString() ?? '',
  sortOrder: String(zone.sortOrder),
  included: zone.included,
});

const toRequest = (form: ZoneFormState): ZoneWriteRequest => ({
  name: form.name.trim(),
  kind: form.kind,
  comfortLowF: form.comfortLowF === '' ? null : Number(form.comfortLowF),
  comfortHighF: form.comfortHighF === '' ? null : Number(form.comfortHighF),
  sortOrder: Number(form.sortOrder) || 0,
  included: form.included,
});

export function ZonesPage() {
  const [zones, setZones] = useState<Zone[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<ZoneFormState | null>(null);
  const [creating, setCreating] = useState(false);
  const [createForm, setCreateForm] = useState<ZoneFormState>(emptyForm(0));
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const data = await getZones();
      setZones(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  function startCreate() {
    setCreateForm(emptyForm(zones.length ? Math.max(...zones.map((z) => z.sortOrder)) + 1 : 0));
    setCreating(true);
  }

  async function submitCreate() {
    setSaving(true);
    setError(null);
    try {
      const zone = await createZone(toRequest(createForm));
      setZones((prev) => [...prev, zone].sort((a, b) => a.sortOrder - b.sortOrder));
      setCreating(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  function startEdit(zone: Zone) {
    setEditingId(zone.id);
    setEditForm(toFormState(zone));
  }

  async function submitEdit(id: string) {
    if (!editForm) return;
    setSaving(true);
    setError(null);
    try {
      const zone = await updateZone(id, toRequest(editForm));
      setZones((prev) => prev.map((z) => (z.id === id ? zone : z)).sort((a, b) => a.sortOrder - b.sortOrder));
      setEditingId(null);
      setEditForm(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete(zone: Zone) {
    if (!confirm(`Delete zone "${zone.name}"?`)) return;
    setError(null);
    try {
      await deleteZone(zone.id);
      setZones((prev) => prev.filter((z) => z.id !== zone.id));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function move(zone: Zone, direction: -1 | 1) {
    const sorted = [...zones].sort((a, b) => a.sortOrder - b.sortOrder);
    const index = sorted.findIndex((z) => z.id === zone.id);
    const swapWith = sorted[index + direction];
    if (!swapWith) return;

    setError(null);
    try {
      const [updatedZone, updatedSwap] = await Promise.all([
        updateZone(zone.id, toRequest({ ...toFormState(zone), sortOrder: String(swapWith.sortOrder) })),
        updateZone(swapWith.id, toRequest({ ...toFormState(swapWith), sortOrder: String(zone.sortOrder) })),
      ]);
      setZones((prev) =>
        prev
          .map((z) => (z.id === updatedZone.id ? updatedZone : z.id === updatedSwap.id ? updatedSwap : z))
          .sort((a, b) => a.sortOrder - b.sortOrder),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  const sortedZones = [...zones].sort((a, b) => a.sortOrder - b.sortOrder);

  return (
    <div>
      <div className="admin-page-header">
        <h2>Zones</h2>
        {!creating && (
          <button className="btn-primary" onClick={startCreate}>
            Add zone
          </button>
        )}
      </div>

      {error && <p className="text-danger mb-2">{error}</p>}
      {loading && <p className="text-muted">Loading…</p>}

      {creating && (
        <div className="card mb-2">
          <h3 className="mb-2">New zone</h3>
          <ZoneForm form={createForm} onChange={setCreateForm} />
          <div className="flex gap-1 mt-2">
            <button className="btn-primary" disabled={saving || !createForm.name.trim()} onClick={submitCreate}>
              Save
            </button>
            <button className="btn-secondary" onClick={() => setCreating(false)}>
              Cancel
            </button>
          </div>
        </div>
      )}

      {!loading && sortedZones.length === 0 && !creating && (
        <p className="text-muted">No zones yet.</p>
      )}

      {sortedZones.length > 0 && (
        <div className="card">
          <table className="admin-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Kind</th>
                <th>Comfort range (°F)</th>
                <th>Sort</th>
                <th>Included</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {sortedZones.map((zone, index) =>
                editingId === zone.id && editForm ? (
                  <tr key={zone.id}>
                    <td colSpan={6}>
                      <ZoneForm form={editForm} onChange={setEditForm} />
                      <div className="flex gap-1 mt-2">
                        <button className="btn-primary" disabled={saving} onClick={() => submitEdit(zone.id)}>
                          Save
                        </button>
                        <button
                          className="btn-secondary"
                          onClick={() => {
                            setEditingId(null);
                            setEditForm(null);
                          }}
                        >
                          Cancel
                        </button>
                      </div>
                    </td>
                  </tr>
                ) : (
                  <tr key={zone.id}>
                    <td>{zone.name}</td>
                    <td>{zone.kind}</td>
                    <td>
                      {zone.comfortLowF ?? '—'} – {zone.comfortHighF ?? '—'}
                    </td>
                    <td>
                      <div className="flex gap-1" style={{ alignItems: 'center' }}>
                        <button className="btn-secondary" disabled={index === 0} onClick={() => move(zone, -1)}>
                          ↑
                        </button>
                        <button
                          className="btn-secondary"
                          disabled={index === sortedZones.length - 1}
                          onClick={() => move(zone, 1)}
                        >
                          ↓
                        </button>
                      </div>
                    </td>
                    <td>
                      <span className={`badge ${zone.included ? 'badge-success' : 'badge-muted'}`}>
                        {zone.included ? 'Yes' : 'No'}
                      </span>
                    </td>
                    <td>
                      <div className="flex gap-1">
                        <button className="btn-secondary" onClick={() => startEdit(zone)}>
                          Edit
                        </button>
                        <button className="btn-danger" onClick={() => handleDelete(zone)}>
                          Delete
                        </button>
                      </div>
                    </td>
                  </tr>
                ),
              )}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function ZoneForm({ form, onChange }: { form: ZoneFormState; onChange: (form: ZoneFormState) => void }) {
  return (
    <div className="grid cols-3">
      <div className="field">
        <label className="field-label">Name</label>
        <input type="text" value={form.name} onChange={(e) => onChange({ ...form, name: e.target.value })} />
      </div>
      <div className="field">
        <label className="field-label">Kind</label>
        <select value={form.kind} onChange={(e) => onChange({ ...form, kind: e.target.value as ZoneKind })}>
          <option value="Interior">Interior</option>
          <option value="Outside">Outside</option>
        </select>
      </div>
      <div className="field">
        <label className="field-label">Sort order</label>
        <input
          type="number"
          value={form.sortOrder}
          onChange={(e) => onChange({ ...form, sortOrder: e.target.value })}
        />
      </div>
      <div className="field">
        <label className="field-label">Comfort low (°F)</label>
        <input
          type="number"
          value={form.comfortLowF}
          onChange={(e) => onChange({ ...form, comfortLowF: e.target.value })}
        />
      </div>
      <div className="field">
        <label className="field-label">Comfort high (°F)</label>
        <input
          type="number"
          value={form.comfortHighF}
          onChange={(e) => onChange({ ...form, comfortHighF: e.target.value })}
        />
      </div>
      <div className="field">
        <label className="field-label">Included</label>
        <label className="flex gap-1" style={{ alignItems: 'center' }}>
          <input
            type="checkbox"
            checked={form.included}
            onChange={(e) => onChange({ ...form, included: e.target.checked })}
          />
          Show on dashboard
        </label>
      </div>
    </div>
  );
}
