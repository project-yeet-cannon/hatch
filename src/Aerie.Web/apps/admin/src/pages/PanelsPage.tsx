import { useEffect, useState } from 'react';
import { Badge, Button, Card, EmptyState, Field, Grid, PageHeader, Text } from '@aerie/ui';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import type {
  ChannelDirection,
  ControlKind,
  ControlRole,
  Device,
  DeviceChannelMetric,
  Panel,
  PanelItem,
  PanelItemKind,
  PanelItemWriteRequest,
  PanelWriteRequest,
  Routine,
} from '../types';
import { createPanel, deletePanel, getDevices, getPanels, getRoutines, updatePanel } from '../api/client';
import { ChannelSelect, type ChannelChoice } from '../components/ChannelSelect';
import { IconPicker } from '../components/IconPicker';
import { iconFor } from '../lib/icons';

// The four constants below mirror Services/Panels/PanelBindingRules.cs and
// PanelDefaults.cs. They are duplicated here rather than fetched because the
// form has to shape itself around them before it can ask anything - which
// channel selects to render at all is the question, not just whether the
// answer is valid. The server still runs the real rules on every write; these
// exist so the page says what's missing before the POST does.

/** Mirrors PanelDefaults - shown as input placeholders, never sent. A blank field means "use the default", which is what null already means server-side. */
const PANEL_DEFAULTS = { minF: 60, maxF: 85, stepF: 1 };

/** Mirrors PanelBindingRules.RequiredMetric. */
const ROLE_METRIC: Record<ControlRole, DeviceChannelMetric> = {
  Power: 'PowerState',
  Setpoint: 'SetpointTemperature',
  Mode: 'HvacMode',
  Ambient: 'Temperature',
};

/** Mirrors PanelBindingRules.IsWritten - Ambient is the one role a Read-only channel can fill, because a room's temperature is reported and never commanded. */
const roleIsWritten = (role: ControlRole) => role !== 'Ambient';

/** Mirrors PanelBindingRules.RolesFor. A Thermostat with neither Power nor Mode is setpoint-only, which is legal - it just has no on/off for the kiosk to render. */
const ROLES_FOR: Record<ControlKind, { role: ControlRole; required: boolean }[]> = {
  Switch: [{ role: 'Power', required: true }],
  Thermostat: [
    { role: 'Setpoint', required: true },
    { role: 'Power', required: false },
    { role: 'Mode', required: false },
    { role: 'Ambient', required: false },
  ],
};

const CONTROL_KINDS: ControlKind[] = ['Switch', 'Thermostat'];

const ROLE_HELP: Record<ControlRole, string> = {
  Power: 'On/off. Preferred over Mode when the device has one.',
  Setpoint: 'The target temperature this control writes.',
  Mode: 'HVAC mode. Needs an On mode below to be switchable.',
  Ambient: 'Measured temperature, shown under the setpoint. Read-only channels are fine here.',
};

const DEFAULT_PANEL_COLOR = '#4b7bec';

interface PanelChannelOption extends ChannelChoice {
  direction: ChannelDirection;
}

function channelOptions(devices: Device[]): PanelChannelOption[] {
  return devices.flatMap((device) =>
    device.channels.map((c) => ({
      channelId: c.id,
      deviceName: device.name,
      metric: c.metric,
      haEntityId: c.haEntityId,
      availableOptions: c.availableOptions,
      direction: c.direction,
    })),
  );
}

/** The channels legal for one role: the metric the role demands, and ReadWrite unless the role is only read. */
function optionsForRole(options: PanelChannelOption[], role: ControlRole): PanelChannelOption[] {
  return options.filter((o) => o.metric === ROLE_METRIC[role] && (!roleIsWritten(role) || o.direction === 'ReadWrite'));
}

type BindingMap = Partial<Record<ControlRole, string>>;

interface ItemFormRow {
  kind: PanelItemKind;
  /** Routine items only; '' until one is picked. */
  routineId: string;
  /** Control items only. Held even on a routine row so switching kinds back doesn't lose the choice. */
  controlKind: ControlKind;
  label: string;
  icon: string;
  color: string;
  onMode: string;
  minF: string;
  maxF: string;
  stepF: string;
  bindings: BindingMap;
}

interface PanelFormState {
  name: string;
  description: string;
  icon: string;
  color: string;
  sortOrder: string;
  included: boolean;
  items: ItemFormRow[];
}

const emptyItem = (kind: PanelItemKind): ItemFormRow => ({
  kind,
  routineId: '',
  controlKind: 'Switch',
  label: '',
  icon: '',
  color: DEFAULT_PANEL_COLOR,
  onMode: '',
  minF: '',
  maxF: '',
  stepF: '',
  bindings: {},
});

const emptyForm = (nextSortOrder: number): PanelFormState => ({
  name: '',
  description: '',
  icon: '',
  color: DEFAULT_PANEL_COLOR,
  sortOrder: String(nextSortOrder),
  included: true,
  items: [],
});

const numText = (value: number | null) => (value === null ? '' : String(value));

function toItemFormRow(item: PanelItem): ItemFormRow {
  const bindings: BindingMap = {};
  for (const binding of item.bindings) bindings[binding.role] = binding.channelId;
  return {
    kind: item.kind,
    routineId: item.routineId ?? '',
    controlKind: item.controlKind ?? 'Switch',
    label: item.label ?? '',
    icon: item.icon ?? '',
    color: item.color ?? DEFAULT_PANEL_COLOR,
    onMode: item.onMode ?? '',
    minF: numText(item.minF),
    maxF: numText(item.maxF),
    stepF: numText(item.stepF),
    bindings,
  };
}

const toFormState = (panel: Panel): PanelFormState => ({
  name: panel.name,
  description: panel.description ?? '',
  icon: panel.icon ?? '',
  color: panel.color ?? DEFAULT_PANEL_COLOR,
  sortOrder: String(panel.sortOrder),
  included: panel.included,
  items: [...panel.items].sort((a, b) => a.sortOrder - b.sortOrder).map(toItemFormRow),
});

const trimOrNull = (value: string) => (value.trim() === '' ? null : value.trim());

/** A blank or unparseable box is null, which the server reads as "use the default" - not as zero. */
function numOrNull(value: string): number | null {
  const trimmed = value.trim();
  if (trimmed === '') return null;
  const parsed = Number(trimmed);
  return Number.isNaN(parsed) ? null : parsed;
}

function toItemRequest(row: ItemFormRow, index: number): PanelItemWriteRequest {
  const isControl = row.kind === 'Control';
  const isThermostat = isControl && row.controlKind === 'Thermostat';
  // Only the roles this kind understands are sent - a role left over from a
  // kind the row used to be would be rejected as "has no {role} role".
  const bindings = isControl
    ? ROLES_FOR[row.controlKind]
        .filter((spec) => (row.bindings[spec.role] ?? '') !== '')
        .map((spec) => ({ role: spec.role, channelId: row.bindings[spec.role]! }))
    : [];
  return {
    sortOrder: index,
    kind: row.kind,
    routineId: isControl ? null : trimOrNull(row.routineId),
    controlKind: isControl ? row.controlKind : null,
    label: isControl ? trimOrNull(row.label) : null,
    icon: isControl ? trimOrNull(row.icon) : null,
    color: isControl ? row.color : null,
    onMode: isThermostat ? trimOrNull(row.onMode) : null,
    minF: isThermostat ? numOrNull(row.minF) : null,
    maxF: isThermostat ? numOrNull(row.maxF) : null,
    stepF: isThermostat ? numOrNull(row.stepF) : null,
    bindings,
  };
}

function toRequest(form: PanelFormState): PanelWriteRequest {
  return {
    name: form.name.trim(),
    description: trimOrNull(form.description),
    icon: trimOrNull(form.icon),
    color: form.color,
    sortOrder: Number(form.sortOrder) || 0,
    included: form.included,
    items: form.items.map(toItemRequest),
  };
}

/** The half of PanelBindingRules.Validate a form can answer on its own. Wording tracks the server's so the same problem reads the same either way it is caught. */
function itemError(row: ItemFormRow): string | null {
  if (row.kind === 'Routine') return row.routineId === '' ? 'Pick a routine.' : null;

  for (const spec of ROLES_FOR[row.controlKind]) {
    if (spec.required && (row.bindings[spec.role] ?? '') === '') {
      return `A ${row.controlKind} control requires a ${spec.role} channel.`;
    }
  }

  if (row.controlKind !== 'Thermostat') return null;

  if ((row.bindings.Mode ?? '') !== '' && row.onMode.trim() === '') {
    return 'A thermostat with a Mode channel needs an On mode (e.g. "cool").';
  }
  // Compared as effective values, not stored ones: a Min of 90 against a blank
  // Max is a Min of 90 against the default 85, which is nonsense worth saying.
  const min = numOrNull(row.minF) ?? PANEL_DEFAULTS.minF;
  const max = numOrNull(row.maxF) ?? PANEL_DEFAULTS.maxF;
  if (min >= max) return `Min (${min}°) must be below max (${max}°).`;
  const step = numOrNull(row.stepF);
  if (step !== null && step <= 0) return `Step must be positive, got ${step}.`;
  return null;
}

function formError(form: PanelFormState): string | null {
  if (form.name.trim() === '') return 'A panel needs a name.';
  if (form.items.length === 0) return 'A panel needs at least one item.';
  for (const [index, row] of form.items.entries()) {
    const error = itemError(row);
    if (error) return `Item ${index + 1}: ${error}`;
  }
  return null;
}

export function PanelsPage() {
  const [panels, setPanels] = useState<Panel[]>([]);
  const [routines, setRoutines] = useState<Routine[]>([]);
  const [devices, setDevices] = useState<Device[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editForm, setEditForm] = useState<PanelFormState | null>(null);
  const [creating, setCreating] = useState(false);
  const [createForm, setCreateForm] = useState<PanelFormState>(emptyForm(0));
  const [saving, setSaving] = useState(false);

  const options = channelOptions(devices);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const [panelList, routineList, deviceList] = await Promise.all([getPanels(), getRoutines(), getDevices()]);
      setPanels(panelList);
      setRoutines(routineList);
      setDevices(deviceList);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  function startCreate() {
    setCreateForm(emptyForm(panels.length ? Math.max(...panels.map((p) => p.sortOrder)) + 1 : 0));
    setCreating(true);
  }

  async function submitCreate() {
    setSaving(true);
    setError(null);
    try {
      const panel = await createPanel(toRequest(createForm));
      setPanels((prev) => [...prev, panel].sort((a, b) => a.sortOrder - b.sortOrder));
      setCreating(false);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function submitEdit(id: string) {
    if (!editForm) return;
    setSaving(true);
    setError(null);
    try {
      const panel = await updatePanel(id, toRequest(editForm));
      setPanels((prev) => prev.map((p) => (p.id === id ? panel : p)).sort((a, b) => a.sortOrder - b.sortOrder));
      setEditingId(null);
      setEditForm(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  }

  async function handleDelete(panel: Panel) {
    if (!confirm(`Delete panel "${panel.name}"?`)) return;
    setError(null);
    try {
      await deletePanel(panel.id);
      setPanels((prev) => prev.filter((p) => p.id !== panel.id));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function move(panel: Panel, direction: -1 | 1) {
    const sorted = [...panels].sort((a, b) => a.sortOrder - b.sortOrder);
    const index = sorted.findIndex((p) => p.id === panel.id);
    const swapWith = sorted[index + direction];
    if (!swapWith) return;

    setError(null);
    try {
      const [updated, updatedSwap] = await Promise.all([
        updatePanel(panel.id, toRequest({ ...toFormState(panel), sortOrder: String(swapWith.sortOrder) })),
        updatePanel(swapWith.id, toRequest({ ...toFormState(swapWith), sortOrder: String(panel.sortOrder) })),
      ]);
      setPanels((prev) =>
        prev.map((p) => (p.id === updated.id ? updated : p.id === updatedSwap.id ? updatedSwap : p)).sort((a, b) => a.sortOrder - b.sortOrder),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  const sortedPanels = [...panels].sort((a, b) => a.sortOrder - b.sortOrder);
  const createError = formError(createForm);
  const editError = editForm ? formError(editForm) : null;

  return (
    <div>
      <PageHeader
        title="Panels"
        actions={!creating && (
          <Button variant="primary" onClick={startCreate}>
            Add panel
          </Button>
        )}
      />

      <Text tone="muted" className="mb-2">
        A panel is a kiosk tile that opens a sub-screen holding several things to touch — existing routines, and controls bound straight to a
        device's channels.
      </Text>

      {error && <Text tone="danger" className="mb-2">{error}</Text>}
      {loading && <Text tone="muted">Loading…</Text>}

      {creating && (
        <Card className="mb-2">
          <h3 className="mb-2">New panel</h3>
          <PanelForm form={createForm} onChange={setCreateForm} routines={routines} options={options} />
          {createError && <Text tone="danger" className="mt-1">{createError}</Text>}
          <div className="flex gap-1 mt-2">
            <Button variant="primary" disabled={saving || createError !== null} onClick={submitCreate}>
              Save
            </Button>
            <Button onClick={() => setCreating(false)}>
              Cancel
            </Button>
          </div>
        </Card>
      )}

      {!loading && sortedPanels.length === 0 && !creating && <EmptyState message="No panels yet." />}

      {sortedPanels.map((panel, index) => (
        <Card className="mb-2" key={panel.id}>
          {editingId === panel.id && editForm ? (
            <>
              <PanelForm form={editForm} onChange={setEditForm} routines={routines} options={options} />
              {editError && <Text tone="danger" className="mt-1">{editError}</Text>}
              <div className="flex gap-1 mt-2">
                <Button variant="primary" disabled={saving || editError !== null} onClick={() => submitEdit(panel.id)}>
                  Save
                </Button>
                <Button
                  onClick={() => {
                    setEditingId(null);
                    setEditForm(null);
                  }}
                >
                  Cancel
                </Button>
              </div>
            </>
          ) : (
            <div className="flex between" style={{ alignItems: 'flex-start' }}>
              <div>
                <div className="flex gap-1" style={{ alignItems: 'center' }}>
                  <FontAwesomeIcon icon={iconFor(panel.icon)} color={panel.color ?? undefined} />
                  <h3>{panel.name}</h3>
                  <Badge tone={panel.included ? 'success' : 'muted'}>
                    {panel.included ? 'On kiosk' : 'Hidden'}
                  </Badge>
                </div>
                {panel.description && <Text tone="muted">{panel.description}</Text>}
                <Text tone="muted">{itemSummary(panel, routines)}</Text>
              </div>
              <div className="flex gap-1">
                <Button disabled={index === 0} onClick={() => move(panel, -1)}>
                  ↑
                </Button>
                <Button disabled={index === sortedPanels.length - 1} onClick={() => move(panel, 1)}>
                  ↓
                </Button>
                <Button
                  onClick={() => {
                    setEditingId(panel.id);
                    setEditForm(toFormState(panel));
                  }}
                >
                  Edit
                </Button>
                <Button variant="danger" onClick={() => handleDelete(panel)}>
                  Delete
                </Button>
              </div>
            </div>
          )}
        </Card>
      ))}
    </div>
  );
}

/** "3 items — Air conditioner, Fan 1, Good night" - the collapsed row says what is on the panel, not just how much. */
function itemSummary(panel: Panel, routines: Routine[]): string {
  if (panel.items.length === 0) return 'No items yet.';
  const names = [...panel.items]
    .sort((a, b) => a.sortOrder - b.sortOrder)
    .map((item) =>
      item.kind === 'Routine'
        ? (routines.find((r) => r.id === item.routineId)?.name ?? 'Unknown routine')
        : (item.label?.trim() || `Unnamed ${item.controlKind ?? 'control'}`),
    );
  return `${names.length} item${names.length === 1 ? '' : 's'} — ${names.join(', ')}`;
}

function PanelForm({
  form,
  onChange,
  routines,
  options,
}: {
  form: PanelFormState;
  onChange: (form: PanelFormState) => void;
  routines: Routine[];
  options: PanelChannelOption[];
}) {
  function updateItem(index: number, row: ItemFormRow) {
    onChange({ ...form, items: form.items.map((item, i) => (i === index ? row : item)) });
  }

  function removeItem(index: number) {
    onChange({ ...form, items: form.items.filter((_, i) => i !== index) });
  }

  function moveItem(index: number, direction: -1 | 1) {
    const target = index + direction;
    if (target < 0 || target >= form.items.length) return;
    const items = [...form.items];
    [items[index], items[target]] = [items[target], items[index]];
    onChange({ ...form, items });
  }

  return (
    <div>
      <Grid cols={3}>
        <Field label="Name">
          <input type="text" value={form.name} onChange={(e) => onChange({ ...form, name: e.target.value })} />
        </Field>
        <Field label="Description (optional)">
          <input type="text" value={form.description} onChange={(e) => onChange({ ...form, description: e.target.value })} />
        </Field>
        <Field label="Sort order">
          <input type="number" value={form.sortOrder} onChange={(e) => onChange({ ...form, sortOrder: e.target.value })} />
        </Field>
        <Field label="Included" as="div">
          <label className="flex gap-1" style={{ alignItems: 'center' }}>
            <input type="checkbox" checked={form.included} onChange={(e) => onChange({ ...form, included: e.target.checked })} />
            Show on kiosk
          </label>
        </Field>
        <Field label="Icon">
          <IconPicker value={form.icon} onChange={(icon) => onChange({ ...form, icon })} />
        </Field>
        <Field label="Color">
          <input type="color" value={form.color} onChange={(e) => onChange({ ...form, color: e.target.value })} />
        </Field>
      </Grid>

      <h4 className="mt-2 mb-1">Items</h4>
      {form.items.length === 0 && <EmptyState message="Nothing on this panel yet — add a routine or a control below." />}

      {form.items.map((row, index) => (
        <ItemEditor
          key={index}
          row={row}
          index={index}
          total={form.items.length}
          routines={routines}
          options={options}
          onChange={(next) => updateItem(index, next)}
          onMove={(direction) => moveItem(index, direction)}
          onRemove={() => removeItem(index)}
        />
      ))}

      <div className="flex gap-1 mt-1">
        <Button onClick={() => onChange({ ...form, items: [...form.items, emptyItem('Routine')] })}>
          Add routine
        </Button>
        <Button onClick={() => onChange({ ...form, items: [...form.items, emptyItem('Control')] })}>
          Add control
        </Button>
      </div>
    </div>
  );
}

function ItemEditor({
  row,
  index,
  total,
  routines,
  options,
  onChange,
  onMove,
  onRemove,
}: {
  row: ItemFormRow;
  index: number;
  total: number;
  routines: Routine[];
  options: PanelChannelOption[];
  onChange: (row: ItemFormRow) => void;
  onMove: (direction: -1 | 1) => void;
  onRemove: () => void;
}) {
  const error = itemError(row);

  function setControlKind(controlKind: ControlKind) {
    // Bindings are kept keyed by role, and toItemRequest only sends the roles
    // the new kind understands - so switching Thermostat -> Switch -> Thermostat
    // does not silently discard the setpoint channel the admin already picked.
    onChange({ ...row, controlKind });
  }

  function bind(role: ControlRole, channelId: string) {
    onChange({ ...row, bindings: { ...row.bindings, [role]: channelId } });
  }

  return (
    <Card className="mb-1">
      <div className="flex between mb-1" style={{ alignItems: 'center' }}>
        <Badge>{row.kind === 'Routine' ? 'Routine' : `${row.controlKind} control`}</Badge>
        <div className="flex gap-1">
          <Button disabled={index === 0} onClick={() => onMove(-1)}>
            ↑
          </Button>
          <Button disabled={index === total - 1} onClick={() => onMove(1)}>
            ↓
          </Button>
          <Button variant="danger" onClick={onRemove}>
            Remove
          </Button>
        </div>
      </div>

      {row.kind === 'Routine' ? (
        <Field label="Routine">
          <select value={row.routineId} onChange={(e) => onChange({ ...row, routineId: e.target.value })}>
            <option value="">Select a routine…</option>
            {[...routines]
              .sort((a, b) => a.name.localeCompare(b.name))
              .map((routine) => (
                <option key={routine.id} value={routine.id}>
                  {routine.name}
                </option>
              ))}
          </select>
          <Text tone="muted">The routine's own name, icon and color are used on the panel.</Text>
        </Field>
      ) : (
        <ControlEditor row={row} options={options} onChange={onChange} onSetControlKind={setControlKind} onBind={bind} />
      )}

      {error && <Text tone="danger" className="mt-1">{error}</Text>}
    </Card>
  );
}

function ControlEditor({
  row,
  options,
  onChange,
  onSetControlKind,
  onBind,
}: {
  row: ItemFormRow;
  options: PanelChannelOption[];
  onChange: (row: ItemFormRow) => void;
  onSetControlKind: (kind: ControlKind) => void;
  onBind: (role: ControlRole, channelId: string) => void;
}) {
  const isThermostat = row.controlKind === 'Thermostat';
  const modeChannelId = row.bindings.Mode ?? '';
  // The device itself is the authority on what its modes are called, so the
  // On mode is picked from the bound HvacMode channel's own options rather
  // than from a list of guesses.
  const modeOptions = options.find((o) => o.channelId === modeChannelId)?.availableOptions ?? null;

  return (
    <>
      <Grid cols={3}>
        <Field label="Kind">
          <select value={row.controlKind} onChange={(e) => onSetControlKind(e.target.value as ControlKind)}>
            {CONTROL_KINDS.map((kind) => (
              <option key={kind} value={kind}>
                {kind}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Label">
          <input
            type="text"
            placeholder="Air conditioner"
            value={row.label}
            onChange={(e) => onChange({ ...row, label: e.target.value })}
          />
        </Field>
        <Field label="Icon">
          <IconPicker value={row.icon} onChange={(icon) => onChange({ ...row, icon })} />
        </Field>
        <Field label="Color">
          <input type="color" value={row.color} onChange={(e) => onChange({ ...row, color: e.target.value })} />
        </Field>
      </Grid>

      <h5 className="mt-1 mb-1">Channels</h5>
      {ROLES_FOR[row.controlKind].map((spec) => {
        const roleOptions = optionsForRole(options, spec.role);
        return (
          <Field label={`${spec.role}${spec.required ? '' : ' (optional)'}`} key={spec.role}>
            <div className="flex gap-1" style={{ alignItems: 'center' }}>
              <ChannelSelect
                value={row.bindings[spec.role] ?? ''}
                options={roleOptions}
                placeholder={`Search ${ROLE_METRIC[spec.role]} channels…`}
                onSelect={(channelId) => onBind(spec.role, channelId)}
              />
              {(row.bindings[spec.role] ?? '') !== '' && (
                <Button onClick={() => onBind(spec.role, '')}>
                  Clear
                </Button>
              )}
            </div>
            <Text tone="muted">
              {ROLE_HELP[spec.role]}
              {roleOptions.length === 0 && ` No ${ROLE_METRIC[spec.role]} channel is mapped yet.`}
            </Text>
          </Field>
        );
      })}

      {isThermostat && (
        <Grid cols={3} className="mt-1">
          <Field label="On mode">
            {modeOptions && modeOptions.length > 0 ? (
              <select value={row.onMode} onChange={(e) => onChange({ ...row, onMode: e.target.value })}>
                <option value="">Select a mode…</option>
                {modeOptions.map((option) => (
                  <option key={option} value={option}>
                    {option}
                  </option>
                ))}
              </select>
            ) : (
              <input
                type="text"
                placeholder="cool"
                value={row.onMode}
                onChange={(e) => onChange({ ...row, onMode: e.target.value })}
              />
            )}
            <Text tone="muted">
              Which HVAC mode means "on" for this device — "cool" for an air conditioner, "heat" for a radiator. Required once a Mode
              channel is bound.
            </Text>
          </Field>
          <Field label="Min °F">
            <input
              type="number"
              placeholder={String(PANEL_DEFAULTS.minF)}
              value={row.minF}
              onChange={(e) => onChange({ ...row, minF: e.target.value })}
            />
          </Field>
          <Field label="Max °F">
            <input
              type="number"
              placeholder={String(PANEL_DEFAULTS.maxF)}
              value={row.maxF}
              onChange={(e) => onChange({ ...row, maxF: e.target.value })}
            />
          </Field>
          <Field label="Step °F">
            <input
              type="number"
              placeholder={String(PANEL_DEFAULTS.stepF)}
              value={row.stepF}
              onChange={(e) => onChange({ ...row, stepF: e.target.value })}
            />
          </Field>
        </Grid>
      )}
    </>
  );
}
