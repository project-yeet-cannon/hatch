import type {
  BackfillRequest,
  Device,
  DeviceChannel,
  DeviceChannelWriteRequest,
  DeviceWriteRequest,
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

// ---- Settings ----

export const getSettings = () => fetchJson<SiteSetting[]>('/api/settings');
export const putSetting = (key: string, value: string) =>
  fetchJson<SiteSetting>(`/api/settings/${encodeURIComponent(key)}`, { method: 'PUT', ...asJson({ value }) });
export const deleteSetting = (key: string) =>
  fetchJson<void>(`/api/settings/${encodeURIComponent(key)}`, { method: 'DELETE' });

// ---- Discovery ----

export const getUnmappedDevices = () => fetchJson<UnmappedHaDevice[]>('/api/discovery/unmapped');
