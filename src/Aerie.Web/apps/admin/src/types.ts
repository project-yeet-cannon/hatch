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

export type DeviceKind = 'Thermostat' | 'Hygrometer' | 'SmartSwitch' | 'Light' | 'Speaker';

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
  | 'MediaPlayback';

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

export type RoutineActionKind = 'SetPower' | 'SetTemperature' | 'SetHvacMode' | 'SetFanMode' | 'TriggerScene';

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
  actions: RoutineAction[];
}

export interface RoutineWriteRequest {
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  sortOrder: number;
  included: boolean;
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
