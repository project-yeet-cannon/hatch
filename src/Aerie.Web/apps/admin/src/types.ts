/**
 * TS mirrors of Aerie.Api's DeviceMapping DTOs (Models/DeviceMapping/Dtos.cs).
 * Enums serialize as strings (Program.cs JsonStringEnumConverter), so they're
 * modeled here as string literal unions.
 */

export type ZoneKind = 'Interior' | 'Outside';

export interface Zone {
  id: string;
  name: string;
  kind: ZoneKind;
  comfortLowF: number | null;
  comfortHighF: number | null;
  sortOrder: number;
  included: boolean;
}

export interface ZoneWriteRequest {
  name: string;
  kind: ZoneKind;
  comfortLowF: number | null;
  comfortHighF: number | null;
  sortOrder: number;
  included: boolean;
}

export type DeviceKind = 'Thermostat' | 'Hygrometer' | 'SmartSwitch' | 'Light' | 'Speaker' | 'Camera';

export type DeviceChannelMetric =
  | 'Temperature'
  | 'Humidity'
  | 'Battery'
  | 'SetpointTemperature'
  | 'HvacAction'
  | 'HeatingMode'
  | 'PowerState'
  | 'HvacMode'
  | 'FanMode'
  | 'Scene'
  | 'MediaPlayback'
  | 'CameraFeed'
  | 'MotionState';

export type ChannelDirection = 'Read' | 'ReadWrite';

export interface DeviceChannel {
  id: string;
  metric: DeviceChannelMetric;
  haEntityId: string;
  haAttribute: string | null;
  direction: ChannelDirection;
  lastValue: number | null;
  lastState: string | null;
  lastValueAt: string | null;
  availableOptions: string[] | null;
}

export interface DeviceChannelWriteRequest {
  metric: DeviceChannelMetric;
  haEntityId: string;
  haAttribute: string | null;
  direction: ChannelDirection;
  availableOptions: string[] | null;
}

export interface Device {
  id: string;
  name: string;
  kind: DeviceKind | null;
  zoneId: string | null;
  haDeviceId: string | null;
  enabled: boolean;
  channels: DeviceChannel[];
}

export interface DeviceWriteRequest {
  name: string;
  kind: DeviceKind | null;
  zoneId: string | null;
  haDeviceId: string | null;
  enabled: boolean;
  /** Only Discovery sends this: the host HA reports for the device, which seeds a camera's connection so nobody has to type an address. */
  discoveredHost?: string | null;
}

/**
 * How to reach a camera's RTSP stream (docs/camera-devices-architecture.md). There
 * is no password field, on purpose - the API lets you set one, never read one
 * back, so `hasPassword` is what tells an empty box from an unset one.
 */
export interface CameraConnection {
  /** The operator's override. Null means "use whatever Home Assistant reports". */
  host: string | null;
  /** What Home Assistant last reported. Shown as a hint, never edited here. */
  discoveredHost: string | null;
  /** Which of the two is actually in use, resolved server-side. */
  effectiveHost: string | null;
  port: number;
  streamPath: string;
  username: string | null;
  hasPassword: boolean;
}

export interface CameraConnectionWriteRequest {
  host: string | null;
  port: number | null;
  streamPath: string | null;
  username: string | null;
  /** Omit (undefined) to leave the stored password alone; '' clears it; a value replaces it. */
  password?: string | null;
}

export interface BackfillRequest {
  from: string;
  to: string;
}

export interface ChannelPowerRequest {
  on: boolean;
}

export interface ChannelModeRequest {
  mode: string;
}

export interface ChannelSetpointRequest {
  temperature: number;
}

export interface ChannelPlayMediaRequest {
  mediaContentId: string;
  /** Defaults to "music" server-side when null. */
  mediaContentType: string | null;
}

export interface SiteSetting {
  key: string;
  value: string;
}

/**
 * Mirrors Aerie.Api's HazardAlert (Models/Dashboard/DashboardData.cs). Read
 * here only to verify configuration - the kiosk gets the same list on
 * GET /api/dashboard.
 */
export interface HazardAlert {
  id: string;
  kind: 'Weather' | 'AirQuality';
  severity: 'Unknown' | 'Minor' | 'Moderate' | 'Severe' | 'Extreme';
  title: string;
  detail: string | null;
  startsAt: string | null;
  endsAt: string | null;
}

export interface UnmappedHaDevice {
  haDeviceId: string;
  suggestedName: string;
  suggestedKind: DeviceKind | null;
  entityIds: string[];
  suggestedChannels: DeviceChannelWriteRequest[];
  /** Host from HA's device registry, when it has one. Only a camera uses it. */
  discoveredHost: string | null;
}

export interface ChannelHistoryPoint {
  time: string;
  value: number;
}

export interface ChannelStatePoint {
  time: string;
  state: string;
}

export interface ChannelHistory {
  channelId: string;
  metric: DeviceChannelMetric;
  points: ChannelHistoryPoint[];
  states: ChannelStatePoint[];
}

export interface DeviceHistory {
  deviceId: string;
  channels: ChannelHistory[];
}

export type RoutineActionKind = 'SetPower' | 'SetTemperature' | 'SetHvacMode' | 'SetFanMode' | 'TriggerScene' | 'PlayMedia';

export interface RoutineAction {
  id: string;
  channelId: string;
  kind: RoutineActionKind;
  value: string | null;
  sortOrder: number;
}

export interface RoutineActionWriteRequest {
  channelId: string;
  kind: RoutineActionKind;
  value: string | null;
  sortOrder: number;
}

export interface Routine {
  id: string;
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  sortOrder: number;
  included: boolean;
  /** Renders on the kiosk as an on/off switch instead of a momentary trigger - see RoutineWriteRequest.isToggle. */
  isToggle: boolean;
  actions: RoutineAction[];
}

export interface RoutineWriteRequest {
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  sortOrder: number;
  included: boolean;
  /** When true, only SetPower actions are meaningful: the kiosk shows active while every action's channel reads "on", and tapping while active turns those channels off instead of re-running Actions. */
  isToggle: boolean;
  actions: RoutineActionWriteRequest[];
}

export interface ProvisioningInfo {
  signatureChecksum: string;
  apkDownloadUrl: string;
  deviceAdminComponentName: string;
  wifiSsid: string;
  wifiPassword: string;
  wifiSecurityType: string;
  timeZone: string;
}

/**
 * Mirrors of Aerie.Api's auth DTOs (Models/Auth/Dtos.cs). A grant is one
 * enrolled device: the credential itself is never in here, because it exists
 * in plaintext exactly once, in the Set-Cookie that minted it.
 */
/** `GET /api/people` - one household member (Models/People/Dtos.cs). */
export interface Person {
  id: string;
  name: string;
  /**
   * Whether this person is served this app at all, and may take the operator
   * verbs behind it. Nothing on the client branches on it - by the time this
   * page renders, the server has already decided: a device linked to nobody, or
   * to somebody without this flag, is answered 404 for the whole bundle
   * (AdminAppMiddleware), so a non-admin never gets far enough to read this
   * field.
   *
   * Enforced only while the install sets ADMIN_MODE=enforced; off, everyone
   * behind the wall sees everything, which is how Aerie behaved before the flag
   * was read. Setting it is guarded by itself, which is why the checkbox below
   * cannot be used to promote the device you are sitting on. See
   * EfPerson.IsAdmin and docs/auth-architecture.md, "The admin flag".
   */
  isAdmin: boolean;
  createdAt: string;
  updatedAt: string;
  /**
   * When the photo was last uploaded, or null for someone who has none. Doubles
   * as the photo's version: the URL is stable, so this is what a cache-busting
   * query has to carry for a new upload to show up.
   */
  photoUpdatedAt: string | null;
  hasPhoto: boolean;
  /** How many enrolled devices are linked to this person. Zero is ordinary. */
  sessionCount: number;
}

export interface PersonWriteRequest {
  name: string;
  isAdmin: boolean;
}

/** One of a person's devices, as the People page lists them - read-only here; revoking lives on Sessions. */
export interface PersonSession {
  id: string;
  label: string;
  kind: AuthGrantKind;
  createdAt: string;
  lastSeenAt: string | null;
}

export type AuthGrantKind = 'Interactive' | 'Device';

export interface AuthGrant {
  id: string;
  label: string;
  kind: AuthGrantKind;
  createdAt: string;
  lastSeenAt: string | null;
  lastSeenIp: string | null;
  userAgent: string | null;
  /** The device asking. It gets a badge instead of a Delete button - see SessionsPage. */
  isCurrent: boolean;
  /** Whose device this is, or null for one nobody has claimed. Editable here; the People page only reads it. */
  personId: string | null;
  /** Their name, sent alongside the id so the table can draw the column without joining two lists. */
  personName: string | null;
}

export interface AuthInvite {
  /** The bare eight characters, as the redeem URL carries them. */
  code: string;
  /** The same code as a person says it: "AERIE-K3M9-P2QT". */
  formattedCode: string;
  /** Rooted path a scanned QR should open; the absolute URL is this against the install's public base. */
  redeemPath: string;
  expiresAt: string;
  label: string | null;
}

/** What a key may reach. One scope today - see ApiKeyScopes in Ef/ApiKeys.cs. */
export type ApiKeyScope = 'hatch';

/** The scopes the mint dialog offers, mirroring ApiKeyScopes.All. */
export const API_KEY_SCOPES: ApiKeyScope[] = ['hatch'];

/**
 * One API key as the list sees it. The secret is not here and never will be:
 * it exists in plaintext exactly once, in the response to the mint.
 */
export interface ApiKey {
  id: string;
  name: string;
  /** The leading characters of the secret - enough to match a row against a config file, useless to hold. */
  prefix: string;
  scopes: ApiKeyScope[];
  createdAt: string;
  lastUsedAt: string | null;
  /** When it stopped working, or null while it still does. */
  revokedAt: string | null;
}

/** A freshly minted key, on its way to a screen once. */
export interface ApiKeyMinted {
  key: ApiKey;
  secret: string;
}

/** `GET /api/apps/config` - the deploy-time values a client can't derive (Modules/AppsController.cs). */
export interface AppsConfig {
  /** Absolute base URL of this install, no trailing slash, or null when unset. */
  publicBaseUrl: string | null;
}

/**
 * Mirrors of Aerie.Api's calendar DTOs (Models/Calendar/Dtos.cs). Note what is
 * absent: no token material is on these records, because none of it leaves the
 * database — see the note at the top of that file.
 */
export interface Calendar {
  id: string;
  providerCalendarId: string;
  name: string;
  /** The color the provider reports. `colorOverride` wins when set; both are here so a recolor can be undone. */
  providerColor: string | null;
  colorOverride: string | null;
  included: boolean;
  sortOrder: number;
  timeZone: string | null;
  isPrimary: boolean;
}

export interface CalendarAccount {
  id: string;
  provider: string;
  accountEmail: string;
  displayName: string | null;
  connectedAt: string;
  lastSyncedAt: string | null;
  lastSyncError: string | null;
  /** The grant is dead and only re-consent fixes it. The row stays listed so the page can offer "Reconnect". */
  needsReauth: boolean;
  enabled: boolean;
  calendars: Calendar[];
}

/** The admin-owned half of a calendar — everything a provider refresh deliberately leaves alone. */
export interface CalendarVisibilityRequest {
  included: boolean;
  /** A hex color (`#rgb` or `#rrggbb`), or null to fall back to the provider's. Rejected server-side if it is neither. */
  colorOverride: string | null;
  sortOrder: number;
}

/** What a "Refresh calendars" run changed, so the page can say so rather than just re-rendering. */
export interface CalendarDiscovery {
  added: number;
  updated: number;
  removed: number;
}

/** What an on-demand event sync did. Per-account failures are a count here; the reason lands on that account's `lastSyncError`. */
export interface CalendarSync {
  accounts: number;
  calendars: number;
  written: number;
  removed: number;
  failedAccounts: number;
}

/**
 * Mirrors of Aerie.Api's Panel DTOs (Models/Panels/Dtos.cs). A Panel is the
 * tier above a Routine: a kiosk tile that opens a sub-UI holding several calls
 * to action, some of them existing Routines and some of them Controls -
 * typed, device-bound surfaces that report state as well as write it.
 *
 * Only the admin half is here. The kiosk's PanelSummary and PanelStateDto live
 * in the dashboard app, which is the only thing that reads them.
 */
export type PanelItemKind = 'Routine' | 'Control';

export type ControlKind = 'Switch' | 'Thermostat';

export type ControlRole = 'Power' | 'Setpoint' | 'Mode' | 'Ambient';

export interface PanelControlBinding {
  id: string;
  role: ControlRole;
  channelId: string;
}

/**
 * One entry in a Panel's ordered list. The nullable fields mirror the server's
 * exactly, including which `kind` makes each one non-null: `routineId` is set
 * only on a Routine item, and everything from `controlKind` down only on a
 * Control.
 */
export interface PanelItem {
  id: string;
  sortOrder: number;
  kind: PanelItemKind;
  routineId: string | null;
  controlKind: ControlKind | null;
  label: string | null;
  icon: string | null;
  color: string | null;
  /** Thermostat only: the HVAC mode that means "on" for this device — "cool" on an air conditioner, "heat" on a radiator. */
  onMode: string | null;
  /** Thermostat bounds in °F. Null means the server's default (60 / 85 / 1). */
  minF: number | null;
  maxF: number | null;
  stepF: number | null;
  bindings: PanelControlBinding[];
}

export interface Panel {
  id: string;
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  sortOrder: number;
  included: boolean;
  items: PanelItem[];
}

export interface PanelControlBindingWriteRequest {
  role: ControlRole;
  channelId: string;
}

export interface PanelItemWriteRequest {
  sortOrder: number;
  kind: PanelItemKind;
  routineId: string | null;
  controlKind: ControlKind | null;
  label: string | null;
  icon: string | null;
  color: string | null;
  onMode: string | null;
  minF: number | null;
  maxF: number | null;
  stepF: number | null;
  bindings: PanelControlBindingWriteRequest[];
}

/** Items are embedded and replaced wholesale on every write, exactly as RoutineWriteRequest does with its actions — the item list *is* the panel. */
export interface PanelWriteRequest {
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  sortOrder: number;
  included: boolean;
  items: PanelItemWriteRequest[];
}

// ---- Photos ----
//
// Mirrors Modules/Photos/Dtos.cs. No Immich URL appears in any of these on
// purpose: the page builds every image src against Aerie's own proxy, so the
// browser never learns where the photo server is (docs/plans/immich.md v+2).

export interface PhotoAlbum {
  id: string;
  immichAlbumId: string;
  name: string;
  description: string | null;
  assetCount: number;
  hasCover: boolean;
  included: boolean;
  sortOrder: number;
  providerUpdatedAt: string | null;
  updatedAt: string;
}

/** The admin-owned half of an album. A null sortOrder leaves the arrangement alone, which is what a checkbox means. */
export interface PhotoAlbumSelectionRequest {
  included: boolean;
  sortOrder: number | null;
}

export interface PhotoAlbumSync {
  added: number;
  updated: number;
  removed: number;
}

export interface PhotosStatus {
  isConfigured: boolean;
  baseUrl: string | null;
  hasApiKey: boolean;
  reachable: boolean;
  version: string | null;
  error: string | null;
  albumCount: number;
  includedAlbumCount: number;
}

export interface CarouselPhoto {
  assetId: string;
  albumName: string;
  takenAt: string | null;
  city: string | null;
  country: string | null;
}

export interface PhotoCarousel {
  photos: CarouselPhoto[];
  totalPhotos: number;
  generatedAt: string;
  error: string | null;
}

// ---- Aerie revision (docs/plans/version.md) ----

/**
 * Where a build sits relative to another. Only `Behind` ever justifies acting -
 * `Ahead` happens legitimately mid-rollout and a client that reacted to it
 * would thrash. PascalCase to match every other enum crossing this wire:
 * Program.cs registers JsonStringEnumConverter with no naming policy, so the
 * C# member name is what arrives.
 */
export type RevisionDrift = 'Unknown' | 'Current' | 'Behind' | 'Ahead';

export interface ClientRevisionVerdict {
  revision: string;
  sequence: number;
  drift: RevisionDrift;
}

export interface FluxKustomizationRevision {
  name: string;
  appliedRevision: string | null;
  ready: boolean | null;
}

export interface FluxSourceRevision {
  name: string;
  revision: string | null;
  branch: string | null;
  kustomizations: FluxKustomizationRevision[];
}

export interface ClusterRevisions {
  sources: FluxSourceRevision[];
  /** Set when Flux could not be read; the rest of the response still stands. */
  unavailable: string | null;
}

export interface AerieRevisionInfo {
  revision: string;
  sequence: number;
  builtAt: string | null;
  /** The verdict on *this browser's* build, echoed back from the headers it sent. */
  client: ClientRevisionVerdict | null;
  /** Only present for an authenticated caller, and only when Flux could be read. */
  cluster: ClusterRevisions | null;
}
