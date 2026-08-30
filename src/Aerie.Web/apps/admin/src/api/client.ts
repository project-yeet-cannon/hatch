import { handledUnauthorized } from '../lib/signIn';
import type {
  AerieRevisionInfo,
  AppsConfig,
  AuthGrant,
  AuthInvite,
  BackfillRequest,
  CameraConnection,
  CameraConnectionWriteRequest,
  Calendar,
  CalendarAccount,
  CalendarDiscovery,
  CalendarSync,
  CalendarVisibilityRequest,
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
  HazardAlert,
  Panel,
  PanelWriteRequest,
  Person,
  PersonSession,
  PersonWriteRequest,
  PhotoAlbum,
  PhotoAlbumSelectionRequest,
  PhotoAlbumSync,
  PhotoCarousel,
  PhotosStatus,
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
  if (handledUnauthorized(res)) {
    // Navigating away; this promise is abandoned with the document.
    return await new Promise<T>(() => {});
  }
  if (!res.ok) {
    throw new Error(await failureMessage(res, init?.method ?? 'GET', path));
  }
  // 202/204 responses (e.g. POST .../backfill) have no body - res.json() throws on empty input.
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/**
 * What a failed request says out loud.
 *
 * Prefers the server's own sentence when there is one: a 400 from
 * PeopleController is "A name can be at most 60 characters", written to be read
 * by whoever is standing at the form. Reporting "PUT /api/people/… failed: 400"
 * instead throws that away and leaves them to guess.
 *
 * Falls back to the status line for the cases where the body is not a sentence
 * - empty, HTML, a ProblemDetails blob, or long enough to be a stack trace.
 * Showing one of those in a red line under a text input is worse than showing
 * nothing, which is why this has a length bound rather than just a null check.
 */
async function failureMessage(res: Response, method: string, path: string): Promise<string> {
  const fallback = `${method} ${path} failed: ${res.status} ${res.statusText}`;

  try {
    const body = (await res.text()).trim();
    if (body === '' || body.length > 300) return fallback;

    // A bare string body arrives JSON-quoted; anything structured is left to
    // the fallback rather than guessed at.
    const parsed: unknown = body.startsWith('"') ? JSON.parse(body) : body;
    return typeof parsed === 'string' && parsed !== '' && !parsed.startsWith('<') ? parsed : fallback;
  } catch {
    return fallback;
  }
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

export const getCameraConnection = (deviceId: string) =>
  fetchJson<CameraConnection>(`/api/devices/${deviceId}/camera-connection`);
export const saveCameraConnection = (deviceId: string, request: CameraConnectionWriteRequest) =>
  fetchJson<CameraConnection>(`/api/devices/${deviceId}/camera-connection`, { method: 'PUT', ...asJson(request) });

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
export const turnOffRoutine = (id: string) => fetchJson<void>(`/api/routines/${id}/turn-off`, { method: 'POST' });

// ---- Panels ----
//
// The kiosk's own panel endpoints (/state, /power, /setpoint) are deliberately
// absent: admin configures panels, the wall tablet drives them.

export const getPanels = () => fetchJson<Panel[]>('/api/panels');
export const getPanel = (id: string) => fetchJson<Panel>(`/api/panels/${id}`);
export const createPanel = (request: PanelWriteRequest) =>
  fetchJson<Panel>('/api/panels', { method: 'POST', ...asJson(request) });
export const updatePanel = (id: string, request: PanelWriteRequest) =>
  fetchJson<Panel>(`/api/panels/${id}`, { method: 'PUT', ...asJson(request) });
export const deletePanel = (id: string) => fetchJson<void>(`/api/panels/${id}`, { method: 'DELETE' });

// ---- Calendar ----
//
// The connect flow itself is not here: /api/calendar/oauth/start is navigated
// to, not fetched, so the page links to it rather than calling it.

export const getCalendarAccounts = () => fetchJson<CalendarAccount[]>('/api/calendar/accounts');
/** Re-lists the account's calendars from the provider, preserving the admin-owned fields on the ones that survive. */
export const refreshCalendars = (accountId: string) =>
  fetchJson<CalendarDiscovery>(`/api/calendar/accounts/${accountId}/refresh-calendars`, { method: 'POST' });
export const updateCalendar = (id: string, request: CalendarVisibilityRequest) =>
  fetchJson<Calendar>(`/api/calendar/calendars/${id}`, { method: 'PUT', ...asJson(request) });
/** Fetches events for every included calendar now, rather than waiting for the SyncCalendarEvents job's next firing. */
export const syncCalendarEvents = () => fetchJson<CalendarSync>('/api/calendar/sync', { method: 'POST' });
/** Revokes the grant with the provider, then deletes the account and its calendars and cached events. */
export const deleteCalendarAccount = (id: string) =>
  fetchJson<void>(`/api/calendar/accounts/${id}`, { method: 'DELETE' });

// ---- Photos ----
//
// The Immich host and key are not here: they are ordinary site settings, so the
// Photos page writes them with putSetting like every other credential in the
// app (docs/secrets-architecture.md).

/** Whether Immich is configured and whether it currently answers - a live check, not a reading of the settings. */
export const getPhotosStatus = () => fetchJson<PhotosStatus>('/api/photos/status');
export const getPhotoAlbums = () => fetchJson<PhotoAlbum[]>('/api/photos/albums');
/** Re-lists albums from Immich, preserving the choices on the ones that survive. */
export const refreshPhotoAlbums = () => fetchJson<PhotoAlbumSync>('/api/photos/albums/refresh', { method: 'POST' });
export const updatePhotoAlbum = (id: string, request: PhotoAlbumSelectionRequest) =>
  fetchJson<PhotoAlbum>(`/api/photos/albums/${id}`, { method: 'PUT', ...asJson(request) });
/** What the kiosk carousel would draw right now - the admin page uses it as the proof that a selection reached the wall. */
export const getPhotoCarousel = (count?: number) => fetchJson<PhotoCarousel>(`/api/photos/carousel${qs({ count })}`);

/** Both image sources are URLs rather than fetches: they go straight into an <img src>, on this origin, with the session cookie the page already has. */
export const photoAlbumCoverUrl = (albumId: string) => `/api/photos/albums/${albumId}/cover`;
export const photoAssetUrl = (assetId: string, size?: 'preview' | 'thumbnail') =>
  `/api/photos/assets/${encodeURIComponent(assetId)}/image${qs({ size })}`;

// ---- Outdoor hazards ----

/** What the kiosk would show right now. Empty is the normal answer - see the Active alerts card on the settings page. */
export const getAlerts = () => fetchJson<HazardAlert[]>('/api/alerts');

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

// ---- People ----

export const getPeople = () => fetchJson<Person[]>('/api/people');
export const createPerson = (request: PersonWriteRequest) =>
  fetchJson<Person>('/api/people', { method: 'POST', ...asJson(request) });
export const updatePerson = (id: string, request: PersonWriteRequest) =>
  fetchJson<Person>(`/api/people/${id}`, { method: 'PUT', ...asJson(request) });
/** Their photo goes with them; their sessions do not - those keep working, just unclaimed. */
export const deletePerson = (id: string) => fetchJson<void>(`/api/people/${id}`, { method: 'DELETE' });

/** Read-only here. The link is written on the Sessions page, where the device is. */
export const getPersonSessions = (id: string) => fetchJson<PersonSession[]>(`/api/people/${id}/sessions`);

/**
 * The photo goes up as a raw body rather than a multipart form: there is one
 * file and no fields beside it, and a Blob is already exactly that. The
 * server ignores the Content-Type this sets and sniffs the bytes instead.
 */
export const uploadPersonPhoto = (id: string, image: Blob) =>
  fetchJson<Person>(`/api/people/${id}/photo`, {
    method: 'PUT',
    body: image,
    // Spelled out because fetchJson's default would otherwise label these bytes
    // application/json. The server does not read it either way - it sniffs -
    // but a request that describes itself wrongly is a request that will
    // mislead whoever is reading it in a network tab at 1am.
    headers: { Accept: 'application/json', 'Content-Type': image.type || 'application/octet-stream' },
  });
export const deletePersonPhoto = (id: string) => fetchJson<void>(`/api/people/${id}/photo`, { method: 'DELETE' });

/**
 * A URL rather than a fetch - it goes straight into an <img src>, same-origin,
 * with the cookie the page already has (the pattern photoAssetUrl uses).
 *
 * The `v` is the upload time, and it is load-bearing: the path is deliberately
 * stable across uploads so nothing has to rebuild it, which means the browser
 * would otherwise happily show the cached previous photo forever.
 */
export const personPhotoUrl = (person: Person) =>
  `/api/people/${person.id}/photo${qs({ v: person.photoUpdatedAt ?? undefined })}`;

// ---- Sessions ----

export const getGrants = () => fetchJson<AuthGrant[]>('/api/auth/grants');
/** Revocation is deletion; the server refuses the caller's own grant, which signOutDevice is for. */
export const deleteGrant = (id: string) => fetchJson<void>(`/api/auth/grants/${id}`, { method: 'DELETE' });
/** The only response in the app that carries a live credential, and it carries it once - it cannot be fetched again. */
export const createInvite = (label: string | null, personId: string | null) =>
  fetchJson<AuthInvite>('/api/auth/invites', { method: 'POST', ...asJson({ label, personId }) });
/** Claims a device for a person, or unclaims it with a null. The only write path for the link. */
export const linkGrantPerson = (id: string, personId: string | null) =>
  fetchJson<void>(`/api/auth/grants/${id}/person`, { method: 'PUT', ...asJson({ personId }) });
export const signOutDevice = () => fetchJson<void>('/api/auth/sign-out', { method: 'POST' });

// ---- Platform config ----

export const getAppsConfig = () => fetchJson<AppsConfig>('/api/apps/config');

// ---- Aerie revision ----

/**
 * What this API replica is running, what this browser is running, and what Flux
 * has reconciled. Deliberately no-store on the server side: the whole question
 * is "right now", and this is the one call where a cached answer is a wrong one.
 */
export const getAerieRevision = () => fetchJson<AerieRevisionInfo>('/api/aerie-revision');
