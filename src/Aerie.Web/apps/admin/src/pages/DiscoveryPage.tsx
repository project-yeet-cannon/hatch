import { useEffect, useState } from 'react';
import type { DeviceKind, UnmappedHaDevice } from '../types';
import { addChannel, createDevice, getUnmappedDevices } from '../api/client';

interface Draft {
  name: string;
  kind: DeviceKind | '';
}

export function DiscoveryPage() {
  const [unmapped, setUnmapped] = useState<UnmappedHaDevice[]>([]);
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [importingId, setImportingId] = useState<string | null>(null);
  const [importedMessage, setImportedMessage] = useState<string | null>(null);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const data = await getUnmappedDevices();
      setUnmapped(data);
      setDrafts(
        Object.fromEntries(
          data.map((d) => [d.haDeviceId, { name: d.suggestedName, kind: d.suggestedKind ?? '' }]),
        ),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  function updateDraft(haDeviceId: string, patch: Partial<Draft>) {
    setDrafts((prev) => ({ ...prev, [haDeviceId]: { ...prev[haDeviceId], ...patch } }));
  }

  async function importDevice(device: UnmappedHaDevice) {
    const draft = drafts[device.haDeviceId];
    if (!draft || !draft.name.trim()) return;

    setImportingId(device.haDeviceId);
    setError(null);
    setImportedMessage(null);
    try {
      const created = await createDevice({
        name: draft.name.trim(),
        kind: draft.kind === "" ? null : draft.kind,
        zoneId: null,
        haDeviceId: device.haDeviceId,
        enabled: true,
      });
      for (const channel of device.suggestedChannels) {
        await addChannel(created.id, channel);
      }
      setUnmapped((prev) => prev.filter((d) => d.haDeviceId !== device.haDeviceId));
      setImportedMessage(`Imported "${created.name}" — assign it to a zone on the Devices page.`);
    } catch (err) {
      setError(
        `${err instanceof Error ? err.message : String(err)} — check the Devices page, the device may have been partially created.`,
      );
    } finally {
      setImportingId(null);
    }
  }

  return (
    <div>
      <div className="admin-page-header">
        <h2>Discovery</h2>
        <button className="btn-secondary" onClick={load}>
          Refresh
        </button>
      </div>

      {error && <p className="text-danger mb-2">{error}</p>}
      {importedMessage && <p className="text-success mb-2">{importedMessage}</p>}
      {loading && <p className="text-muted">Loading…</p>}

      {!loading && unmapped.length === 0 && (
        <p className="text-muted">No unmapped Home Assistant devices found.</p>
      )}

      {unmapped.map((device) => {
        const draft = drafts[device.haDeviceId] ?? { name: device.suggestedName, kind: device.suggestedKind ?? '' };
        return (
          <div className="card mb-2" key={device.haDeviceId}>
            <p className="text-muted mb-1">HA device: {device.haDeviceId}</p>
            <div className="grid cols-2 mb-2">
              <div className="field">
                <label className="field-label">Name</label>
                <input
                  type="text"
                  value={draft.name}
                  onChange={(e) => updateDraft(device.haDeviceId, { name: e.target.value })}
                />
              </div>
              <div className="field">
                <label className="field-label">Kind</label>
                <select
                  value={draft.kind}
                  onChange={(e) => updateDraft(device.haDeviceId, { kind: e.target.value as DeviceKind | "" })}
                >
                  <option value="">Unset</option>
                  <option value="Thermostat">Thermostat</option>
                  <option value="Hygrometer">Hygrometer</option>
                  <option value="SmartSwitch">Smart switch</option>
                  <option value="Light">Light</option>
                </select>
              </div>
            </div>

            <p className="field-label">HA entities</p>
            <p className="text-muted mb-2">{device.entityIds.join(', ')}</p>

            <p className="field-label">Suggested channels</p>
            <table className="admin-table mb-2">
              <thead>
                <tr>
                  <th>Metric</th>
                  <th>HA entity</th>
                  <th>Attribute</th>
                  <th>Direction</th>
                </tr>
              </thead>
              <tbody>
                {device.suggestedChannels.map((channel, i) => (
                  <tr key={i}>
                    <td>{channel.metric}</td>
                    <td>{channel.haEntityId}</td>
                    <td>{channel.haAttribute ?? '—'}</td>
                    <td>{channel.direction}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            <button
              className="btn-primary"
              disabled={!draft.name.trim() || importingId === device.haDeviceId}
              onClick={() => importDevice(device)}
            >
              {importingId === device.haDeviceId ? 'Importing…' : 'Import as Device'}
            </button>
          </div>
        );
      })}
    </div>
  );
}
