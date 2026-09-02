import { useEffect, useState } from 'react';
import { Button, Card, EmptyState, Field, Grid, PageHeader, Table, Text } from '@aerie/ui';
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
        // A camera imported this way arrives already knowing where it lives, so
        // the only thing left to fill in is the credential. Ignored for every
        // other kind.
        discoveredHost: device.discoveredHost,
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
      <PageHeader
        title="Discovery"
        actions={<Button onClick={load}>Refresh</Button>}
      />

      {error && <Text tone="danger" className="mb-2">{error}</Text>}
      {importedMessage && <Text tone="success" className="mb-2">{importedMessage}</Text>}
      {loading && <Text tone="muted">Loading…</Text>}

      {!loading && unmapped.length === 0 && (
        <EmptyState message="No unmapped Home Assistant devices found." />
      )}

      {unmapped.map((device) => {
        const draft = drafts[device.haDeviceId] ?? { name: device.suggestedName, kind: device.suggestedKind ?? '' };
        return (
          <Card className="mb-2" key={device.haDeviceId}>
            <Text tone="muted" className="mb-1">HA device: {device.haDeviceId}</Text>
            <Grid cols={2} className="mb-2">
              <Field label="Name">
                <input
                  type="text"
                  value={draft.name}
                  onChange={(e) => updateDraft(device.haDeviceId, { name: e.target.value })}
                />
              </Field>
              <Field label="Kind">
                <select
                  value={draft.kind}
                  onChange={(e) => updateDraft(device.haDeviceId, { kind: e.target.value as DeviceKind | "" })}
                >
                  <option value="">Unset</option>
                  <option value="Thermostat">Thermostat</option>
                  <option value="Hygrometer">Hygrometer</option>
                  <option value="SmartSwitch">Smart switch</option>
                  <option value="Light">Light</option>
                  <option value="Speaker">Speaker</option>
                  <option value="Camera">Camera</option>
                </select>
              </Field>
            </Grid>

            <Field as="div" label="HA entities" className="mb-2">
              <Text tone="muted">{device.entityIds.join(', ')}</Text>
            </Field>

            <Field as="div" label="Suggested channels" className="mb-2">
              <Table>
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
              </Table>
            </Field>

            <Button variant="primary"
              disabled={!draft.name.trim() || importingId === device.haDeviceId}
              onClick={() => importDevice(device)}
            >
              {importingId === device.haDeviceId ? 'Importing…' : 'Import as Device'}
            </Button>
          </Card>
        );
      })}
    </div>
  );
}
