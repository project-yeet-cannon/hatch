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

export interface UnmappedHaDevice {
  haDeviceId: string;
  suggestedName: string;
  suggestedKind: DeviceKind | null;
  entityIds: string[];
  suggestedChannels: DeviceChannelWriteRequest[];
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
