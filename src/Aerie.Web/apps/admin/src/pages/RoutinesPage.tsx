import { useEffect, useState } from 'react';
import type { Device, DeviceChannel, DeviceChannelMetric, Routine, RoutineActionKind, RoutineWriteRequest } from '../types';
import { createRoutine, deleteRoutine, getDevices, getRoutines, triggerRoutine, updateRoutine } from '../api/client';

/** Which RoutineActionKind a channel supports is fully determined by its metric - only these five ReadWrite metrics are valid Routine targets. */
const METRIC_TO_KIND: Partial<Record<DeviceChannelMetric, RoutineActionKind>> = {
  PowerState: 'SetPower',
  SetpointTemperature: 'SetTemperature',
  HvacMode: 'SetHvacMode',
  FanMode: 'SetFanMode',
  Scene: 'TriggerScene',
};

interface ChannelOption {
  channelId: string;
  deviceName: string;
  metric: DeviceChannelMetric;
  kind: RoutineActionKind;
  haEntityId: string;
  availableOptions: string[] | null;
}

function eligibleChannelOptions(devices: Device[]): ChannelOption[] {
  return devices.flatMap((device) =>
    device.channels
      .filter((c): c is DeviceChannel & { direction: 'ReadWrite' } => c.direction === 'ReadWrite' && c.metric in METRIC_TO_KIND)
      .map((c) => ({
        channelId: c.id,
        deviceName: device.name,
        metric: c.metric,
        kind: METRIC_TO_KIND[c.metric]!,
        haEntityId: c.haEntityId,
        availableOptions: c.availableOptions,
      })),
  );
}

interface ActionFormRow {
  channelId: string;
  value: string;
}

interface RoutineFormState {
  name: string;
  description: string;
  sortOrder: string;
  included: boolean;
  actions: ActionFormRow[];
}

const emptyForm = (nextSortOrder: number): RoutineFormState => ({
  name: '',
  description: '',
  sortOrder: String(nextSortOrder),
  included: true,
  actions: [],
});

const toFormState = (routine: Routine): RoutineFormState => ({
  name: routine.name,
  description: routine.description ?? '',
  sortOrder: String(routine.sortOrder),
  included: routine.included,
  actions: [...routine.actions].sort((a, b) => a.sortOrder - b.sortOrder).map((a) => ({ channelId: a.channelId, value: a.value ?? '' })),
});

function toRequest(form: RoutineFormState, channelOptions: ChannelOption[]): RoutineWriteRequest {
  const actions = form.actions
    .filter((row) => row.channelId !== '')
    .map((row, index) => {
      const option = channelOptions.find((o) => o.channelId === row.channelId)!;
      return {
        channelId: row.channelId,
        kind: option.kind,
        value: option.kind === 'TriggerScene' ? null : row.value,
        sortOrder: index,
      };
    });
  return {
    name: form.name.trim(),
    description: form.description.trim() === '' ? null : form.description.trim(),
    sortOrder: Number(form.sortOrder) || 0,
    included: form.included,
    actions,
  };
}

export function RoutinesPage() {
  const [routines, setRoutines] = useState<Routine[]>([]);
  const [devices, setDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<RoutineFormState | null>(null);
  const [creating, setCreating] = useState(false);
  const [createForm, setCreateForm] = useState<RoutineFormState>(emptyForm(0));
  const [saving, setSaving] = useState(false);
  const [triggeringId, setTriggeringId] = useState<string | null>(null);
  const [triggerStatus, setTriggerStatus] = useState<{ id: string; message: string; isError: boolean } | null>(null);

  const channelOptions = eligibleChannelOptions(devices);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const [routineList, deviceList] = await Promise.all([getRoutines(), getDevices()]);
      setRoutines(routineList);
      setDevices(deviceList);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  function startCreate() {
    setCreateForm(emptyForm(routines.length ? Math.max(...routines.map((r) => r.sortOrder)) + 1 : 0));
    setCreating(true);
  }

  async function submitCreate() {
    setSaving(true);
    setError(null);
    try {
      const routine = await createRoutine(toRequest(createForm, channelOptions));
      setRoutines((prev) => [...prev, routine].sort((a, b) => a.sortOrder - b.sortOrder));
      setCreating(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  function startEdit(routine: Routine) {
    setEditingId(routine.id);
    setEditForm(toFormState(routine));
  }

  async function submitEdit(id: string) {
    if (!editForm) return;
    setSaving(true);
    setError(null);
    try {
      const routine = await updateRoutine(id, toRequest(editForm, channelOptions));
      setRoutines((prev) => prev.map((r) => (r.id === id ? routine : r)).sort((a, b) => a.sortOrder - b.sortOrder));
      setEditingId(null);
      setEditForm(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete(routine: Routine) {
    if (!confirm(`Delete routine "${routine.name}"?`)) return;
    setError(null);
    try {
      await deleteRoutine(routine.id);
      setRoutines((prev) => prev.filter((r) => r.id !== routine.id));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function move(routine: Routine, direction: -1 | 1) {
    const sorted = [...routines].sort((a, b) => a.sortOrder - b.sortOrder);
    const index = sorted.findIndex((r) => r.id === routine.id);
    const swapWith = sorted[index + direction];
    if (!swapWith) return;

    setError(null);
    try {
      const [updated, updatedSwap] = await Promise.all([
        updateRoutine(routine.id, toRequest({ ...toFormState(routine), sortOrder: String(swapWith.sortOrder) }, channelOptions)),
        updateRoutine(swapWith.id, toRequest({ ...toFormState(swapWith), sortOrder: String(routine.sortOrder) }, channelOptions)),
      ]);
      setRoutines((prev) =>
        prev.map((r) => (r.id === updated.id ? updated : r.id === updatedSwap.id ? updatedSwap : r)).sort((a, b) => a.sortOrder - b.sortOrder),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleTrigger(routine: Routine) {
    setTriggeringId(routine.id);
    setTriggerStatus(null);
    try {
      await triggerRoutine(routine.id);
      setTriggerStatus({ id: routine.id, message: 'Triggered.', isError: false });
    } catch (err) {
      setTriggerStatus({ id: routine.id, isError: true, message: err instanceof Error ? err.message : String(err) });
    } finally {
      setTriggeringId(null);
    }
  }

  const sortedRoutines = [...routines].sort((a, b) => a.sortOrder - b.sortOrder);

  return (
    <div>
      <div className="admin-page-header">
        <h2>Routines</h2>
        {!creating && (
          <button className="btn-primary" onClick={startCreate}>
            Add routine
          </button>
        )}
      </div>

      {error && <p className="text-danger mb-2">{error}</p>}
      {loading && <p className="text-muted">Loading…</p>}

      {creating && (
        <div className="card mb-2">
          <h3 className="mb-2">New routine</h3>
          <RoutineForm form={createForm} onChange={setCreateForm} channelOptions={channelOptions} />
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

      {!loading && sortedRoutines.length === 0 && !creating && <p className="text-muted">No routines yet.</p>}

      {sortedRoutines.map((routine, index) => (
        <div className="card mb-2" key={routine.id}>
          {editingId === routine.id && editForm ? (
            <>
              <RoutineForm form={editForm} onChange={setEditForm} channelOptions={channelOptions} />
              <div className="flex gap-1 mt-2">
                <button className="btn-primary" disabled={saving} onClick={() => submitEdit(routine.id)}>
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
            </>
          ) : (
            <div className="flex between" style={{ alignItems: 'flex-start' }}>
              <div>
                <div className="flex gap-1" style={{ alignItems: 'center' }}>
                  <h3>{routine.name}</h3>
                  <span className={`badge ${routine.included ? 'badge-success' : 'badge-muted'}`}>
                    {routine.included ? 'On kiosk' : 'Hidden'}
                  </span>
                </div>
                {routine.description && <p className="text-muted">{routine.description}</p>}
                <p className="text-muted">
                  {routine.actions.length} action{routine.actions.length === 1 ? '' : 's'}
                </p>
                {triggerStatus?.id === routine.id && (
                  <p className={triggerStatus.isError ? 'text-danger' : 'text-success'}>{triggerStatus.message}</p>
                )}
              </div>
              <div className="flex gap-1">
                <button className="btn-secondary" disabled={index === 0} onClick={() => move(routine, -1)}>
                  ↑
                </button>
                <button className="btn-secondary" disabled={index === sortedRoutines.length - 1} onClick={() => move(routine, 1)}>
                  ↓
                </button>
                <button className="btn-secondary" disabled={triggeringId === routine.id} onClick={() => handleTrigger(routine)}>
                  {triggeringId === routine.id ? 'Triggering…' : 'Trigger now'}
                </button>
                <button className="btn-secondary" onClick={() => startEdit(routine)}>
                  Edit
                </button>
                <button className="btn-danger" onClick={() => handleDelete(routine)}>
                  Delete
                </button>
              </div>
            </div>
          )}
        </div>
      ))}
    </div>
  );
}

function RoutineForm({
  form,
  onChange,
  channelOptions,
}: {
  form: RoutineFormState;
  onChange: (form: RoutineFormState) => void;
  channelOptions: ChannelOption[];
}) {
  const optionsByDevice = new Map<string, ChannelOption[]>();
  for (const option of channelOptions) {
    optionsByDevice.set(option.deviceName, [...(optionsByDevice.get(option.deviceName) ?? []), option]);
  }

  function updateAction(index: number, row: ActionFormRow) {
    onChange({ ...form, actions: form.actions.map((a, i) => (i === index ? row : a)) });
  }

  function removeAction(index: number) {
    onChange({ ...form, actions: form.actions.filter((_, i) => i !== index) });
  }

  function moveAction(index: number, direction: -1 | 1) {
    const target = index + direction;
    if (target < 0 || target >= form.actions.length) return;
    const actions = [...form.actions];
    [actions[index], actions[target]] = [actions[target], actions[index]];
    onChange({ ...form, actions });
  }

  return (
    <div>
      <div className="grid cols-3">
        <div className="field">
          <label className="field-label">Name</label>
          <input type="text" value={form.name} onChange={(e) => onChange({ ...form, name: e.target.value })} />
        </div>
        <div className="field">
          <label className="field-label">Description (optional)</label>
          <input type="text" value={form.description} onChange={(e) => onChange({ ...form, description: e.target.value })} />
        </div>
        <div className="field">
          <label className="field-label">Sort order</label>
          <input type="number" value={form.sortOrder} onChange={(e) => onChange({ ...form, sortOrder: e.target.value })} />
        </div>
        <div className="field">
          <label className="field-label">Included</label>
          <label className="flex gap-1" style={{ alignItems: 'center' }}>
            <input type="checkbox" checked={form.included} onChange={(e) => onChange({ ...form, included: e.target.checked })} />
            Show on kiosk
          </label>
        </div>
      </div>

      <h4 className="mt-2 mb-1">Actions</h4>
      {form.actions.length === 0 && <p className="text-muted">No actions yet — add at least one below.</p>}
      {form.actions.map((row, index) => {
        const option = channelOptions.find((o) => o.channelId === row.channelId) ?? null;
        return (
          <div className="flex gap-1 mb-1" key={index} style={{ alignItems: 'center' }}>
            <select
              value={row.channelId}
              onChange={(e) => updateAction(index, { channelId: e.target.value, value: '' })}
              style={{ minWidth: '16rem' }}
            >
              <option value="">Select a device channel…</option>
              {[...optionsByDevice.entries()].map(([deviceName, options]) => (
                <optgroup key={deviceName} label={deviceName}>
                  {options.map((o) => (
                    <option key={o.channelId} value={o.channelId}>
                      {o.metric} ({o.haEntityId})
                    </option>
                  ))}
                </optgroup>
              ))}
            </select>
            {option && <ActionValueInput option={option} value={row.value} onChange={(value) => updateAction(index, { ...row, value })} />}
            <button className="btn-secondary" disabled={index === 0} onClick={() => moveAction(index, -1)}>
              ↑
            </button>
            <button className="btn-secondary" disabled={index === form.actions.length - 1} onClick={() => moveAction(index, 1)}>
              ↓
            </button>
            <button className="btn-danger" onClick={() => removeAction(index)}>
              Remove
            </button>
          </div>
        );
      })}
      <button className="btn-secondary mt-1" onClick={() => onChange({ ...form, actions: [...form.actions, { channelId: '', value: '' }] })}>
        Add action
      </button>
    </div>
  );
}

/** Value input for one action row - shape depends entirely on the selected channel's derived RoutineActionKind, same visual language as DevicesPage's live channel controls. */
function ActionValueInput({ option, value, onChange }: { option: ChannelOption; value: string; onChange: (value: string) => void }) {
  switch (option.kind) {
    case 'SetPower':
      return (
        <select value={value || 'true'} onChange={(e) => onChange(e.target.value)}>
          <option value="true">Turn on</option>
          <option value="false">Turn off</option>
        </select>
      );
    case 'SetTemperature':
      return (
        <input type="number" placeholder="°F" style={{ width: '5rem' }} value={value} onChange={(e) => onChange(e.target.value)} />
      );
    case 'SetHvacMode':
    case 'SetFanMode':
      return option.availableOptions && option.availableOptions.length > 0 ? (
        <select value={value} onChange={(e) => onChange(e.target.value)}>
          <option value="" disabled>
            Select mode…
          </option>
          {option.availableOptions.map((o) => (
            <option key={o} value={o}>
              {o}
            </option>
          ))}
        </select>
      ) : (
        <input type="text" placeholder="mode" style={{ width: '6rem' }} value={value} onChange={(e) => onChange(e.target.value)} />
      );
    case 'TriggerScene':
      return <span className="text-muted">Activates scene</span>;
  }
}
