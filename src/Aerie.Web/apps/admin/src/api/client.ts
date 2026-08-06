import type {
  BackfillRequest,
  ChannelHistory,
  ChannelModeRequest,
  ChannelPlayMediaRequest,
  ChannelPowerRequest,
  ChannelSetpointRequest,
  Device,
  DeviceChannel,
  DeviceChannelWriteRequest,
  DeviceHistory,
  DeviceWriteRequest,
  ProvisioningInfo,
  Routine,
  RoutineWriteRequest,
  SiteSetting,
  UnmappedHaDevice,
  Zone,
  ZoneWriteRequest,
} from '../types';

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    headers: { Accept: 'application/json', ...(init?.body ? { 'Content-Type': 'application/json' } : {}) },
    ...init,
  });
  if (!res.ok) {
    throw new Error(`${init?.method ?? 'GET'} ${path} failed: ${res.status} ${res.statusText}`);
  }
  // 202/204 responses (e.g. POST .../backfill) have no body - res.json() throws on empty input.
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const asJson = (body: unknown): RequestInit => ({ body: JSON.stringify(body) });

/** Builds a "?key=value&..." query string, dropping undefined values. */
function qs(params: Record<string, string | number | undefined>): string {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined) as [string, string | number][];
  if (entries.length === 0) return '';
  return `?${new URLSearchParams(entries.map(([k, v]) => [k, String(v)])).toString()}`;
}

// ---- Zones ----

export const getZones = () => fetchJson<Zone[]>('/api/zones');
export const getZone = (id: string) => fetchJson<Zone>(`/api/zones/${id}`);
export const createZone = (request: ZoneWriteRequest) =>
  fetchJson<Zone>('/api/zones', { method: 'POST', ...asJson(request) });
export const updateZone = (id: string, request: ZoneWriteRequest) =>
  fetchJson<Zone>(`/api/zones/${id}`, { method: 'PUT', ...asJson(request) });
export const deleteZone = (id: string) => fetchJson<void>(`/api/zones/${id}`, { method: 'DELETE' });

// ---- Devices ----

export const getDevices = () => fetchJson<Device[]>('/api/devices');
export const getDevice = (id: string) => fetchJson<Device>(`/api/devices/${id}`);
export const createDevice = (request: DeviceWriteRequest) =>
  fetchJson<Device>('/api/devices', { method: 'POST', ...asJson(request) });
export const updateDevice = (id: string, request: DeviceWriteRequest) =>
  fetchJson<Device>(`/api/devices/${id}`, { method: 'PUT', ...asJson(request) });
export const deleteDevice = (id: string) => fetchJson<void>(`/api/devices/${id}`, { method: 'DELETE' });

export const addChannel = (deviceId: string, request: DeviceChannelWriteRequest) =>
  fetchJson<DeviceChannel>(`/api/devices/${deviceId}/channels`, { method: 'POST', ...asJson(request) });
export const updateChannel = (deviceId: string, channelId: string, request: DeviceChannelWriteRequest) =>
  fetchJson<DeviceChannel>(`/api/devices/${deviceId}/channels/${channelId}`, { method: 'PUT', ...asJson(request) });
export const deleteChannel = (deviceId: string, channelId: string) =>
  fetchJson<void>(`/api/devices/${deviceId}/channels/${channelId}`, { method: 'DELETE' });

export const triggerBackfill = (deviceId: string, request: BackfillRequest) =>
  fetchJson<void>(`/api/devices/${deviceId}/backfill`, { method: 'POST', ...asJson(request) });

export const setChannelPower = (deviceId: string, channelId: string, request: ChannelPowerRequest) =>
  fetchJson<void>(`/api/devices/${deviceId}/channels/${channelId}/power`, { method: 'POST', ...asJson(request) });
export const setChannelSetpoint = (deviceId: string, channelId: string, request: ChannelSetpointRequest) =>
  fetchJson<void>(`/api/devices/${deviceId}/channels/${channelId}/setpoint`, { method: 'POST', ...asJson(request) });
export const setChannelMode = (deviceId: string, channelId: string, request: ChannelModeRequest) =>
  fetchJson<void>(`/api/devices/${deviceId}/channels/${channelId}/mode`, { method: 'POST', ...asJson(request) });
export const refreshChannelOptions = (deviceId: string, channelId: string) =>
  fetchJson<DeviceChannel>(`/api/devices/${deviceId}/channels/${channelId}/refresh-options`, { method: 'POST' });
export const playChannelMedia = (deviceId: string, channelId: string, request: ChannelPlayMediaRequest) =>
  fetchJson<void>(`/api/devices/${deviceId}/channels/${channelId}/play-media`, { method: 'POST', ...asJson(request) });
export const triggerScene = (deviceId: string, channelId: string) =>
  fetchJson<void>(`/api/devices/${deviceId}/channels/${channelId}/trigger-scene`, { method: 'POST' });

export const getDeviceHistory = (deviceId: string, from?: string, to?: string, bucketMinutes?: number) =>
  fetchJson<DeviceHistory>(`/api/devices/${deviceId}/history${qs({ from, to, bucketMinutes })}`);
export const getChannelHistory = (deviceId: string, channelId: string, from?: string, to?: string, bucketMinutes?: number) =>
  fetchJson<ChannelHistory>(`/api/devices/${deviceId}/channels/${channelId}/history${qs({ from, to, bucketMinutes })}`);

// ---- Routines ----

export const getRoutines = () => fetchJson<Routine[]>('/api/routines');
export const getRoutine = (id: string) => fetchJson<Routine>(`/api/routines/${id}`);
export const createRoutine = (request: RoutineWriteRequest) =>
  fetchJson<Routine>('/api/routines', { method: 'POST', ...asJson(request) });
export const updateRoutine = (id: string, request: RoutineWriteRequest) =>
  fetchJson<Routine>(`/api/routines/${id}`, { method: 'PUT', ...asJson(request) });
export const deleteRoutine = (id: string) => fetchJson<void>(`/api/routines/${id}`, { method: 'DELETE' });
export const triggerRoutine = (id: string) => fetchJson<void>(`/api/routines/${id}/trigger`, { method: 'POST' });

// ---- Settings ----

export const getSettings = () => fetchJson<SiteSetting[]>('/api/settings');
export const putSetting = (key: string, value: string) =>
  fetchJson<SiteSetting>(`/api/settings/${encodeURIComponent(key)}`, { method: 'PUT', ...asJson({ value }) });
export const deleteSetting = (key: string) =>
  fetchJson<void>(`/api/settings/${encodeURIComponent(key)}`, { method: 'DELETE' });

// ---- Discovery ----

export const getUnmappedDevices = () => fetchJson<UnmappedHaDevice[]>('/api/discovery/unmapped');

// ---- Kiosk provisioning ----

export const getKioskProvisioningInfo = () => fetchJson<ProvisioningInfo>('/api/kiosk/provisioning-info');
