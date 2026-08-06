import { useEffect, useState } from "react";
import type {
  ChannelDirection,
  Device,
  DeviceChannel,
  DeviceChannelMetric,
  DeviceChannelWriteRequest,
  DeviceKind,
  DeviceWriteRequest,
  Zone,
} from "../types";
import {
  addChannel,
  createDevice,
  deleteChannel,
  deleteDevice,
  getDevices,
  getSettings,
  getZones,
  playChannelMedia,
  refreshChannelOptions,
  setChannelMode,
  setChannelPower,
  setChannelSetpoint,
  triggerBackfill,
  triggerScene,
  updateChannel,
  updateDevice,
} from "../api/client";
import { formatLastValue, homeAssistantDeviceUrl } from "../lib/format";
import {
  CheckIcon,
  CopyIcon,
  ExternalLinkIcon,
  HomeAssistantIcon,
} from "../components/icons";
import { HistoryModal } from "../components/HistoryModal";

const BACKFILL_PRESETS: { label: string; days: number }[] = [
  { label: "Past day", days: 1 },
  { label: "Past 3 days", days: 3 },
  { label: "Past week", days: 7 },
];

interface BackfillFormState {
  from: string;
  to: string;
}

function toDatetimeLocalValue(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function backfillPresetForm(days: number): BackfillFormState {
  const to = new Date();
  const from = new Date(to.getTime() - days * 24 * 60 * 60 * 1000);
  return { from: toDatetimeLocalValue(from), to: toDatetimeLocalValue(to) };
}

const METRICS: DeviceChannelMetric[] = [
  "Temperature",
  "Humidity",
  "Battery",
  "SetpointTemperature",
  "HvacAction",
  "HeatingMode",
  "PowerState",
  "HvacMode",
  "FanMode",
  "Scene",
  "MediaPlayback",
];
const DIRECTIONS: ChannelDirection[] = ["Read", "ReadWrite"];

function isPowerOn(channel: DeviceChannel): boolean {
  return (channel.lastState ?? "").toLowerCase() === "on";
}

function isPowerChannel(channel: DeviceChannel): boolean {
  return channel.metric === "PowerState" && channel.direction === "ReadWrite";
}

function isModeChannel(channel: DeviceChannel): boolean {
  return (channel.metric === "HvacMode" || channel.metric === "FanMode") && channel.direction === "ReadWrite";
}

function isSetpointChannel(channel: DeviceChannel): boolean {
  return channel.metric === "SetpointTemperature" && channel.direction === "ReadWrite";
}

function isSceneChannel(channel: DeviceChannel): boolean {
  return channel.metric === "Scene" && channel.direction === "ReadWrite";
}

function isMediaPlaybackChannel(channel: DeviceChannel): boolean {
  return channel.metric === "MediaPlayback" && channel.direction === "ReadWrite";
}

/** "scene.basement_hallway_rolling_hills" -> "basement hallway rolling hills" - readable enough without a dedicated display-name field. */
function sceneLabel(channel: DeviceChannel): string {
  return channel.haEntityId.replace(/^scene\./, "").replace(/_/g, " ");
}

interface DeviceFormState {
  name: string;
  kind: DeviceKind | "";
  zoneId: string;
  haDeviceId: string;
  enabled: boolean;
}

const emptyDeviceForm = (): DeviceFormState => ({
  name: "",
  kind: "",
  zoneId: "",
  haDeviceId: "",
  enabled: true,
});

const toDeviceForm = (device: Device): DeviceFormState => ({
  name: device.name,
  kind: device.kind ?? "",
  zoneId: device.zoneId ?? "",
  haDeviceId: device.haDeviceId ?? "",
  enabled: device.enabled,
});

const toDeviceRequest = (form: DeviceFormState): DeviceWriteRequest => ({
  name: form.name.trim(),
  kind: form.kind === "" ? null : form.kind,
  zoneId: form.zoneId === "" ? null : form.zoneId,
  haDeviceId: form.haDeviceId.trim() === "" ? null : form.haDeviceId.trim(),
  enabled: form.enabled,
});

interface ChannelFormState {
  metric: DeviceChannelMetric;
  haEntityId: string;
  haAttribute: string;
  direction: ChannelDirection;
  // Not editable through this generic form (see ModeControl's "Refresh options"
  // for how it's populated) - carried through untouched so editing a mode
  // channel's entity/attribute/direction here doesn't wipe its known options.
  availableOptions: string[] | null;
}

const emptyChannelForm = (): ChannelFormState => ({
  metric: "Temperature",
  haEntityId: "",
  haAttribute: "",
  direction: "Read",
  availableOptions: null,
});

const toChannelForm = (channel: DeviceChannel): ChannelFormState => ({
  metric: channel.metric,
  haEntityId: channel.haEntityId,
  haAttribute: channel.haAttribute ?? "",
  direction: channel.direction,
  availableOptions: channel.availableOptions,
});

const toChannelRequest = (
  form: ChannelFormState,
): DeviceChannelWriteRequest => ({
  metric: form.metric,
  haEntityId: form.haEntityId.trim(),
  haAttribute: form.haAttribute.trim() === "" ? null : form.haAttribute.trim(),
  direction: form.direction,
  availableOptions: form.availableOptions,
});

export function DevicesPage() {
  const [devices, setDevices] = useState<Device[]>([]);
  const [zones, setZones] = useState<Zone[]>([]);
  const [haHost, setHaHost] = useState<string | null>(null);
  const [haPort, setHaPort] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedId, setExpandedId] = useState<string | null>(null);

  const [creatingDevice, setCreatingDevice] = useState(false);
  const [createDeviceForm, setCreateDeviceForm] =
    useState<DeviceFormState>(emptyDeviceForm());

  const [editingDeviceId, setEditingDeviceId] = useState<string | null>(null);
  const [editDeviceForm, setEditDeviceForm] = useState<DeviceFormState | null>(
    null,
  );

  const [addingChannelFor, setAddingChannelFor] = useState<string | null>(null);
  const [newChannelForm, setNewChannelForm] =
    useState<ChannelFormState>(emptyChannelForm());

  const [editingChannel, setEditingChannel] = useState<{
    deviceId: string;
    channelId: string;
  } | null>(null);
  const [editChannelForm, setEditChannelForm] =
    useState<ChannelFormState | null>(null);

  const [backfillingFor, setBackfillingFor] = useState<string | null>(null);
  const [backfillForm, setBackfillForm] = useState<BackfillFormState>(
    backfillPresetForm(1),
  );
  const [backfillStatus, setBackfillStatus] = useState<{
    deviceId: string;
    message: string;
    isError: boolean;
  } | null>(null);

  const [historyDeviceId, setHistoryDeviceId] = useState<string | null>(null);
  const [historyChannel, setHistoryChannel] = useState<{
    deviceId: string;
    channelId: string;
  } | null>(null);

  const [copiedHaDeviceId, setCopiedHaDeviceId] = useState<string | null>(null);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const [deviceList, zoneList, settings] = await Promise.all([
        getDevices(),
        getZones(),
        getSettings(),
      ]);
      setDevices(deviceList);
      setZones(zoneList);
      setHaHost(
        settings.find((s) => s.key === "HomeAssistantHost")?.value ?? null,
      );
      setHaPort(
        settings.find((s) => s.key === "HomeAssistantPort")?.value ?? null,
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  function zoneName(zoneId: string | null) {
    if (!zoneId) return "Unassigned";
    return zones.find((z) => z.id === zoneId)?.name ?? "Unknown zone";
  }

  async function submitCreateDevice() {
    setError(null);
    try {
      const device = await createDevice(toDeviceRequest(createDeviceForm));
      setDevices((prev) => [...prev, device]);
      setCreatingDevice(false);
      setCreateDeviceForm(emptyDeviceForm());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  function startEditDevice(device: Device) {
    setEditingDeviceId(device.id);
    setEditDeviceForm(toDeviceForm(device));
  }

  async function submitEditDevice(id: string) {
    if (!editDeviceForm) return;
    setError(null);
    try {
      const device = await updateDevice(id, toDeviceRequest(editDeviceForm));
      setDevices((prev) => prev.map((d) => (d.id === id ? device : d)));
      setEditingDeviceId(null);
      setEditDeviceForm(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function toggleEnabled(device: Device) {
    setError(null);
    try {
      const updated = await updateDevice(
        device.id,
        toDeviceRequest({ ...toDeviceForm(device), enabled: !device.enabled }),
      );
      setDevices((prev) => prev.map((d) => (d.id === device.id ? updated : d)));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleDeleteDevice(device: Device) {
    if (!confirm(`Delete device "${device.name}" and its channels?`)) return;
    setError(null);
    try {
      await deleteDevice(device.id);
      setDevices((prev) => prev.filter((d) => d.id !== device.id));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function submitAddChannel(deviceId: string) {
    setError(null);
    try {
      const channel = await addChannel(
        deviceId,
        toChannelRequest(newChannelForm),
      );
      setDevices((prev) =>
        prev.map((d) =>
          d.id === deviceId ? { ...d, channels: [...d.channels, channel] } : d,
        ),
      );
      setAddingChannelFor(null);
      setNewChannelForm(emptyChannelForm());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  function startEditChannel(deviceId: string, channel: DeviceChannel) {
    setEditingChannel({ deviceId, channelId: channel.id });
    setEditChannelForm(toChannelForm(channel));
  }

  async function submitEditChannel() {
    if (!editingChannel || !editChannelForm) return;
    setError(null);
    try {
      const { deviceId, channelId } = editingChannel;
      const channel = await updateChannel(
        deviceId,
        channelId,
        toChannelRequest(editChannelForm),
      );
      setDevices((prev) =>
        prev.map((d) =>
          d.id === deviceId
            ? {
                ...d,
                channels: d.channels.map((c) =>
                  c.id === channelId ? channel : c,
                ),
              }
            : d,
        ),
      );
      setEditingChannel(null);
      setEditChannelForm(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleDeleteChannel(deviceId: string, channel: DeviceChannel) {
    if (!confirm(`Delete channel ${channel.metric} (${channel.haEntityId})?`))
      return;
    setError(null);
    try {
      await deleteChannel(deviceId, channel.id);
      setDevices((prev) =>
        prev.map((d) =>
          d.id === deviceId
            ? { ...d, channels: d.channels.filter((c) => c.id !== channel.id) }
            : d,
        ),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleTogglePower(deviceId: string, channel: DeviceChannel) {
    setError(null);
    const nextOn = !isPowerOn(channel);
    try {
      await setChannelPower(deviceId, channel.id, { on: nextOn });
      // The channel's own row updates on SampleChannels' next poll (~1min);
      // reflect the commanded state locally in the meantime.
      setDevices((prev) =>
        prev.map((d) =>
          d.id === deviceId
            ? {
                ...d,
                channels: d.channels.map((c) =>
                  c.id === channel.id
                    ? {
                        ...c,
                        lastState: nextOn ? "on" : "off",
                        lastValueAt: new Date().toISOString(),
                      }
                    : c,
                ),
              }
            : d,
        ),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleSetMode(deviceId: string, channel: DeviceChannel, mode: string) {
    setError(null);
    try {
      await setChannelMode(deviceId, channel.id, { mode });
      setDevices((prev) =>
        prev.map((d) =>
          d.id === deviceId
            ? {
                ...d,
                channels: d.channels.map((c) =>
                  c.id === channel.id
                    ? { ...c, lastState: mode, lastValueAt: new Date().toISOString() }
                    : c,
                ),
              }
            : d,
        ),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleSetSetpoint(
    deviceId: string,
    channel: DeviceChannel,
    temperature: number,
  ) {
    setError(null);
    try {
      await setChannelSetpoint(deviceId, channel.id, { temperature });
      setDevices((prev) =>
        prev.map((d) =>
          d.id === deviceId
            ? {
                ...d,
                channels: d.channels.map((c) =>
                  c.id === channel.id
                    ? { ...c, lastValue: temperature, lastValueAt: new Date().toISOString() }
                    : c,
                ),
              }
            : d,
        ),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleTriggerScene(deviceId: string, channel: DeviceChannel) {
    setError(null);
    try {
      await triggerScene(deviceId, channel.id);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handlePlayMedia(
    deviceId: string,
    channel: DeviceChannel,
    mediaContentId: string,
  ) {
    setError(null);
    try {
      await playChannelMedia(deviceId, channel.id, {
        mediaContentId,
        mediaContentType: null,
      });
      // Same optimism as handleTogglePower - the real state lands on
      // SampleChannels' next poll (~1min).
      setDevices((prev) =>
        prev.map((d) =>
          d.id === deviceId
            ? {
                ...d,
                channels: d.channels.map((c) =>
                  c.id === channel.id
                    ? { ...c, lastState: "playing", lastValueAt: new Date().toISOString() }
                    : c,
                ),
              }
            : d,
        ),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function handleRefreshOptions(deviceId: string, channel: DeviceChannel) {
    setError(null);
    try {
      const updated = await refreshChannelOptions(deviceId, channel.id);
      setDevices((prev) =>
        prev.map((d) =>
          d.id === deviceId
            ? { ...d, channels: d.channels.map((c) => (c.id === channel.id ? updated : c)) }
            : d,
        ),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  function startBackfill(deviceId: string) {
    setBackfillingFor(deviceId);
    setBackfillForm(backfillPresetForm(1));
    setBackfillStatus(null);
  }

  async function copyHaDeviceId(haDeviceId: string) {
    try {
      await navigator.clipboard.writeText(haDeviceId);
      setCopiedHaDeviceId(haDeviceId);
      setTimeout(
        () =>
          setCopiedHaDeviceId((prev) => (prev === haDeviceId ? null : prev)),
        1500,
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }

  async function submitBackfill(deviceId: string) {
    setBackfillStatus(null);
    try {
      const from = new Date(backfillForm.from).toISOString();
      const to = new Date(backfillForm.to).toISOString();
      await triggerBackfill(deviceId, { from, to });
      setBackfillStatus({
        deviceId,
        message: "Backfill started — check server logs for progress.",
        isError: false,
      });
    } catch (err) {
      setBackfillStatus({
        deviceId,
        isError: true,
        message: err instanceof Error ? err.message : String(err),
      });
    }
  }

  return (
    <div>
      <div className="admin-page-header">
        <h2>Devices</h2>
        {!creatingDevice && (
          <button
            className="btn-primary"
            onClick={() => setCreatingDevice(true)}
          >
            Add device
          </button>
        )}
      </div>

      {error && <p className="text-danger mb-2">{error}</p>}
      {loading && <p className="text-muted">Loading…</p>}

      {creatingDevice && (
        <div className="card mb-2">
          <h3 className="mb-2">New device</h3>
          <DeviceForm
            form={createDeviceForm}
            onChange={setCreateDeviceForm}
            zones={zones}
          />
          <div className="flex gap-1 mt-2">
            <button
              className="btn-primary"
              disabled={!createDeviceForm.name.trim()}
              onClick={submitCreateDevice}
            >
              Save
            </button>
            <button
              className="btn-secondary"
              onClick={() => setCreatingDevice(false)}
            >
              Cancel
            </button>
          </div>
        </div>
      )}

      {!loading && devices.length === 0 && !creatingDevice && (
        <p className="text-muted">No devices yet.</p>
      )}

      {devices.map((device) => (
        <div className="card mb-2" key={device.id}>
          {editingDeviceId === device.id && editDeviceForm ? (
            <>
              <DeviceForm
                form={editDeviceForm}
                onChange={setEditDeviceForm}
                zones={zones}
              />
              <div className="flex gap-1 mt-2">
                <button
                  className="btn-primary"
                  onClick={() => submitEditDevice(device.id)}
                >
                  Save
                </button>
                <button
                  className="btn-secondary"
                  onClick={() => {
                    setEditingDeviceId(null);
                    setEditDeviceForm(null);
                  }}
                >
                  Cancel
                </button>
              </div>
            </>
          ) : (
            <div className="flex between" style={{ alignItems: "flex-start" }}>
              <div>
                <div className="flex gap-1" style={{ alignItems: "center" }}>
                  <h3>{device.name}</h3>
                  <span
                    className={`badge ${device.enabled ? "badge-success" : "badge-muted"}`}
                  >
                    {device.enabled ? "Enabled" : "Disabled"}
                  </span>
                </div>
                <p className="text-muted">
                  {device.haDeviceId && (
                    <>
                      {" · HA Device ID "}
                      {device.haDeviceId}{" "}
                      <button
                        type="button"
                        className="icon-button"
                        onClick={() => copyHaDeviceId(device.haDeviceId!)}
                        title="Copy HA device id"
                        aria-label="Copy HA device id"
                      >
                        {copiedHaDeviceId === device.haDeviceId ? (
                          <CheckIcon />
                        ) : (
                          <CopyIcon />
                        )}
                      </button>{" "}
                      <a
                        className="icon-link"
                        href={homeAssistantDeviceUrl(
                          device.haDeviceId,
                          haHost,
                          haPort,
                        )}
                        target="_blank"
                        rel="noopener noreferrer"
                        title="Open in Home Assistant"
                        aria-label="Open in Home Assistant"
                      >
                        <HomeAssistantIcon />
                        <ExternalLinkIcon />
                      </a>
                    </>
                  )}
                </p>
                <p className="text-muted">
                  {[device.kind, zoneName(device.zoneId)].filter(Boolean).join(" · ")}
                </p>
                {device.channels.length > 0 && (
                  <div className="mt-1">
                    {device.channels.map((channel) => (
                      <p
                        className="text-muted flex gap-1"
                        key={channel.id}
                        style={{ margin: 0, alignItems: "center" }}
                      >
                        <span>
                          {channel.metric}: {formatLastValue(channel)}
                        </span>
                        {isPowerChannel(channel) && (
                          <button
                            className="btn-secondary"
                            onClick={() => handleTogglePower(device.id, channel)}
                          >
                            Turn {isPowerOn(channel) ? "off" : "on"}
                          </button>
                        )}
                        {isModeChannel(channel) && (
                          <ModeControl
                            deviceId={device.id}
                            channel={channel}
                            onSetMode={handleSetMode}
                            onRefreshOptions={handleRefreshOptions}
                          />
                        )}
                        {isSetpointChannel(channel) && (
                          <SetpointControl
                            deviceId={device.id}
                            channel={channel}
                            onSetSetpoint={handleSetSetpoint}
                          />
                        )}
                        {isSceneChannel(channel) && (
                          <button
                            className="btn-secondary"
                            onClick={() => handleTriggerScene(device.id, channel)}
                          >
                            Activate: {sceneLabel(channel)}
                          </button>
                        )}
                        {isMediaPlaybackChannel(channel) && (
                          <MediaControl
                            deviceId={device.id}
                            channel={channel}
                            onPlayMedia={handlePlayMedia}
                          />
                        )}
                      </p>
                    ))}
                  </div>
                )}
              </div>
              <div className="flex gap-1">
                <button
                  className="btn-secondary"
                  onClick={() => toggleEnabled(device)}
                >
                  {device.enabled ? "Disable" : "Enable"}
                </button>
                <button
                  className="btn-secondary"
                  onClick={() => startEditDevice(device)}
                >
                  Edit
                </button>
                <button
                  className="btn-danger"
                  onClick={() => handleDeleteDevice(device)}
                >
                  Delete
                </button>
              </div>
            </div>
          )}

          <div className="flex gap-1 mt-2">
            <button
              className="btn-secondary"
              onClick={() =>
                setExpandedId(expandedId === device.id ? null : device.id)
              }
            >
              {expandedId === device.id
                ? "Hide channels"
                : `Channels (${device.channels.length})`}
            </button>
            <button
              className="btn-secondary"
              onClick={() =>
                backfillingFor === device.id
                  ? setBackfillingFor(null)
                  : startBackfill(device.id)
              }
            >
              {backfillingFor === device.id
                ? "Hide backfill"
                : "Backfill history"}
            </button>
            <button
              className="btn-secondary"
              onClick={() => setHistoryDeviceId(device.id)}
            >
              History
            </button>
          </div>

          {backfillingFor === device.id && (
            <div className="card mt-2">
              <p className="text-muted mb-2">
                Pull historical channel samples from Home Assistant into Aerie
                for this device.
              </p>
              <div className="flex gap-1 mb-2">
                {BACKFILL_PRESETS.map((preset) => (
                  <button
                    key={preset.days}
                    className="btn-secondary"
                    onClick={() =>
                      setBackfillForm(backfillPresetForm(preset.days))
                    }
                  >
                    {preset.label}
                  </button>
                ))}
              </div>
              <div className="grid cols-3">
                <div className="field">
                  <label className="field-label">From</label>
                  <input
                    type="datetime-local"
                    value={backfillForm.from}
                    onChange={(e) =>
                      setBackfillForm({ ...backfillForm, from: e.target.value })
                    }
                  />
                </div>
                <div className="field">
                  <label className="field-label">To</label>
                  <input
                    type="datetime-local"
                    value={backfillForm.to}
                    onChange={(e) =>
                      setBackfillForm({ ...backfillForm, to: e.target.value })
                    }
                  />
                </div>
              </div>
              <div className="flex gap-1 mt-2">
                <button
                  className="btn-primary"
                  disabled={!backfillForm.from || !backfillForm.to}
                  onClick={() => submitBackfill(device.id)}
                >
                  Start backfill
                </button>
                <button
                  className="btn-secondary"
                  onClick={() => setBackfillingFor(null)}
                >
                  Cancel
                </button>
              </div>
              {backfillStatus && backfillStatus.deviceId === device.id && (
                <p
                  className={`mt-2 ${backfillStatus.isError ? "text-danger" : "text-success"}`}
                >
                  {backfillStatus.message}
                </p>
              )}
            </div>
          )}

          {expandedId === device.id && (
            <div className="mt-2">
              {device.channels.length === 0 && (
                <p className="text-muted">No channels.</p>
              )}
              {device.channels.length > 0 && (
                <table className="admin-table">
                  <thead>
                    <tr>
                      <th>Metric</th>
                      <th>HA entity</th>
                      <th>Attribute</th>
                      <th>Direction</th>
                      <th>Last value</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {device.channels.map((channel) =>
                      editingChannel?.deviceId === device.id &&
                      editingChannel.channelId === channel.id &&
                      editChannelForm ? (
                        <tr key={channel.id}>
                          <td colSpan={6}>
                            <ChannelForm
                              form={editChannelForm}
                              onChange={setEditChannelForm}
                            />
                            <div className="flex gap-1 mt-2">
                              <button
                                className="btn-primary"
                                onClick={submitEditChannel}
                              >
                                Save
                              </button>
                              <button
                                className="btn-secondary"
                                onClick={() => {
                                  setEditingChannel(null);
                                  setEditChannelForm(null);
                                }}
                              >
                                Cancel
                              </button>
                            </div>
                          </td>
                        </tr>
                      ) : (
                        <tr key={channel.id}>
                          <td>{channel.metric}</td>
                          <td>{channel.haEntityId}</td>
                          <td>{channel.haAttribute ?? "—"}</td>
                          <td>{channel.direction}</td>
                          <td>{formatLastValue(channel)}</td>
                          <td>
                            <div className="flex gap-1">
                              {isPowerChannel(channel) && (
                                <button
                                  className="btn-secondary"
                                  onClick={() =>
                                    handleTogglePower(device.id, channel)
                                  }
                                >
                                  Turn {isPowerOn(channel) ? "off" : "on"}
                                </button>
                              )}
                              {isModeChannel(channel) && (
                                <ModeControl
                                  deviceId={device.id}
                                  channel={channel}
                                  onSetMode={handleSetMode}
                                  onRefreshOptions={handleRefreshOptions}
                                />
                              )}
                              {isSetpointChannel(channel) && (
                                <SetpointControl
                                  deviceId={device.id}
                                  channel={channel}
                                  onSetSetpoint={handleSetSetpoint}
                                />
                              )}
                              {isSceneChannel(channel) && (
                                <button
                                  className="btn-secondary"
                                  onClick={() =>
                                    handleTriggerScene(device.id, channel)
                                  }
                                >
                                  Activate: {sceneLabel(channel)}
                                </button>
                              )}
                              {isMediaPlaybackChannel(channel) && (
                                <MediaControl
                                  deviceId={device.id}
                                  channel={channel}
                                  onPlayMedia={handlePlayMedia}
                                />
                              )}
                              <button
                                className="btn-secondary"
                                onClick={() =>
                                  setHistoryChannel({
                                    deviceId: device.id,
                                    channelId: channel.id,
                                  })
                                }
                              >
                                History
                              </button>
                              <button
                                className="btn-secondary"
                                onClick={() =>
                                  startEditChannel(device.id, channel)
                                }
                              >
                                Edit
                              </button>
                              <button
                                className="btn-danger"
                                onClick={() =>
                                  handleDeleteChannel(device.id, channel)
                                }
                              >
                                Delete
                              </button>
                            </div>
                          </td>
                        </tr>
                      ),
                    )}
                  </tbody>
                </table>
              )}

              {addingChannelFor === device.id ? (
                <div className="mt-2">
                  <ChannelForm
                    form={newChannelForm}
                    onChange={setNewChannelForm}
                  />
                  <div className="flex gap-1 mt-2">
                    <button
                      className="btn-primary"
                      disabled={!newChannelForm.haEntityId.trim()}
                      onClick={() => submitAddChannel(device.id)}
                    >
                      Add channel
                    </button>
                    <button
                      className="btn-secondary"
                      onClick={() => setAddingChannelFor(null)}
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              ) : (
                <button
                  className="btn-secondary mt-2"
                  onClick={() => {
                    setAddingChannelFor(device.id);
                    setNewChannelForm(emptyChannelForm());
                  }}
                >
                  Add channel
                </button>
              )}
            </div>
          )}

          {historyDeviceId === device.id && (
            <HistoryModal
              open
              deviceId={device.id}
              title={`${device.name} history`}
              onClose={() => setHistoryDeviceId(null)}
            />
          )}
          {historyChannel?.deviceId === device.id && (
            <HistoryModal
              open
              deviceId={device.id}
              channelId={historyChannel.channelId}
              title={`${device.name} · ${device.channels.find((c) => c.id === historyChannel.channelId)?.metric ?? "channel"} history`}
              onClose={() => setHistoryChannel(null)}
            />
          )}
        </div>
      ))}
    </div>
  );
}

function DeviceForm({
  form,
  onChange,
  zones,
}: {
  form: DeviceFormState;
  onChange: (form: DeviceFormState) => void;
  zones: Zone[];
}) {
  return (
    <div className="grid cols-3">
      <div className="field">
        <label className="field-label">Name</label>
        <input
          type="text"
          value={form.name}
          onChange={(e) => onChange({ ...form, name: e.target.value })}
        />
      </div>
      <div className="field">
        <label className="field-label">Kind</label>
        <select
          value={form.kind}
          onChange={(e) =>
            onChange({ ...form, kind: e.target.value as DeviceKind | "" })
          }
        >
          <option value="">Unset</option>
          <option value="Thermostat">Thermostat</option>
          <option value="Hygrometer">Hygrometer</option>
          <option value="SmartSwitch">Smart switch</option>
          <option value="Light">Light</option>
          <option value="Speaker">Speaker</option>
          <option value="Camera">Camera</option>
        </select>
      </div>
      <div className="field">
        <label className="field-label">Zone</label>
        <select
          value={form.zoneId}
          onChange={(e) => onChange({ ...form, zoneId: e.target.value })}
        >
          <option value="">Unassigned</option>
          {zones.map((zone) => (
            <option key={zone.id} value={zone.id}>
              {zone.name}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label className="field-label">HA device id</label>
        <input
          type="text"
          value={form.haDeviceId}
          onChange={(e) => onChange({ ...form, haDeviceId: e.target.value })}
        />
      </div>
      <div className="field">
        <label className="field-label">Enabled</label>
        <label className="flex gap-1" style={{ alignItems: "center" }}>
          <input
            type="checkbox"
            checked={form.enabled}
            onChange={(e) => onChange({ ...form, enabled: e.target.checked })}
          />
          Active
        </label>
      </div>
    </div>
  );
}

function ChannelForm({
  form,
  onChange,
}: {
  form: ChannelFormState;
  onChange: (form: ChannelFormState) => void;
}) {
  return (
    <div className="grid cols-3">
      <div className="field">
        <label className="field-label">Metric</label>
        <select
          value={form.metric}
          onChange={(e) =>
            onChange({ ...form, metric: e.target.value as DeviceChannelMetric })
          }
        >
          {METRICS.map((metric) => (
            <option key={metric} value={metric}>
              {metric}
            </option>
          ))}
        </select>
      </div>
      <div className="field">
        <label className="field-label">HA entity id</label>
        <input
          type="text"
          value={form.haEntityId}
          onChange={(e) => onChange({ ...form, haEntityId: e.target.value })}
        />
      </div>
      <div className="field">
        <label className="field-label">HA attribute (optional)</label>
        <input
          type="text"
          value={form.haAttribute}
          onChange={(e) => onChange({ ...form, haAttribute: e.target.value })}
        />
      </div>
      <div className="field">
        <label className="field-label">Direction</label>
        <select
          value={form.direction}
          onChange={(e) =>
            onChange({ ...form, direction: e.target.value as ChannelDirection })
          }
        >
          {DIRECTIONS.map((direction) => (
            <option key={direction} value={direction}>
              {direction}
            </option>
          ))}
        </select>
      </div>
    </div>
  );
}

/** A HvacMode/FanMode channel's live control: a constrained dropdown when AvailableOptions is known (the normal case, populated by Discovery), otherwise a free-text fallback for a hand-added channel plus a way to fetch its real options from HA. */
function ModeControl({
  deviceId,
  channel,
  onSetMode,
  onRefreshOptions,
}: {
  deviceId: string;
  channel: DeviceChannel;
  onSetMode: (deviceId: string, channel: DeviceChannel, mode: string) => void;
  onRefreshOptions: (deviceId: string, channel: DeviceChannel) => void;
}) {
  const [draft, setDraft] = useState(channel.lastState ?? "");

  if (channel.availableOptions && channel.availableOptions.length > 0) {
    return (
      <select
        value={channel.lastState ?? ""}
        onChange={(e) => onSetMode(deviceId, channel, e.target.value)}
      >
        <option value="" disabled>
          Select…
        </option>
        {channel.availableOptions.map((option) => (
          <option key={option} value={option}>
            {option}
          </option>
        ))}
      </select>
    );
  }

  return (
    <span className="flex gap-1" style={{ alignItems: "center" }}>
      <input
        type="text"
        value={draft}
        placeholder="mode"
        style={{ width: "6rem" }}
        onChange={(e) => setDraft(e.target.value)}
      />
      <button
        className="btn-secondary"
        disabled={!draft.trim()}
        onClick={() => onSetMode(deviceId, channel, draft.trim())}
      >
        Set
      </button>
      <button
        className="btn-secondary"
        onClick={() => onRefreshOptions(deviceId, channel)}
      >
        Refresh options
      </button>
    </span>
  );
}

/** A MediaPlayback channel's live control: a file location plus a Play button. Normally a path relative to the media library root, which the API expands against the MediaLibraryBaseUrl setting (see MediaLibraryUrlResolver); an http URL or media-source:// id also works and is passed through. */
function MediaControl({
  deviceId,
  channel,
  onPlayMedia,
}: {
  deviceId: string;
  channel: DeviceChannel;
  onPlayMedia: (deviceId: string, channel: DeviceChannel, mediaContentId: string) => void;
}) {
  const [draft, setDraft] = useState("");

  return (
    <span className="flex gap-1" style={{ alignItems: "center" }}>
      <input
        type="text"
        value={draft}
        placeholder="Miles Davis/Kind of Blue/01 So What.flac"
        title="Path relative to the media library root. A full http(s) URL or a media-source:// id also works."
        style={{ width: "22rem" }}
        onChange={(e) => setDraft(e.target.value)}
      />
      <button
        className="btn-secondary"
        disabled={!draft.trim()}
        onClick={() => onPlayMedia(deviceId, channel, draft.trim())}
      >
        Play
      </button>
    </span>
  );
}

/** A SetpointTemperature channel's live control: a number field plus an explicit Set button, unlike ModeControl's auto-submitting dropdown, since a stray keystroke here shouldn't immediately push a new setpoint to HA. */
function SetpointControl({
  deviceId,
  channel,
  onSetSetpoint,
}: {
  deviceId: string;
  channel: DeviceChannel;
  onSetSetpoint: (deviceId: string, channel: DeviceChannel, temperature: number) => void;
}) {
  const [draft, setDraft] = useState(
    channel.lastValue !== null ? String(channel.lastValue) : "",
  );
  const parsed = Number(draft);
  const valid = draft.trim() !== "" && !Number.isNaN(parsed);

  return (
    <span className="flex gap-1" style={{ alignItems: "center" }}>
      <input
        type="number"
        value={draft}
        style={{ width: "5rem" }}
        onChange={(e) => setDraft(e.target.value)}
      />
      <button
        className="btn-secondary"
        disabled={!valid}
        onClick={() => onSetSetpoint(deviceId, channel, parsed)}
      >
        Set
      </button>
    </span>
  );
}
