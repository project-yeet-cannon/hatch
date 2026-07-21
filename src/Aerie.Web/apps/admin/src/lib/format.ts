import type { DeviceChannel } from '../types';

export const DEFAULT_HOME_ASSISTANT_BASE_URL = 'http://homeassistant.local:8123';

/** Home Assistant's device config page for a given HA device id, under the given (or default) base URL. */
export function homeAssistantDeviceUrl(haDeviceId: string, baseUrl: string | null | undefined): string {
  const base = (baseUrl?.trim() || DEFAULT_HOME_ASSISTANT_BASE_URL).replace(/\/+$/, '');
  return `${base}/config/devices/device/${haDeviceId}`;
}

/** Compact relative age, e.g. "5m ago", "3h ago", "2d ago"; falls back to a date beyond a week. */
export function formatAge(iso: string, now: Date = new Date()): string {
  const seconds = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 1000));
  if (seconds < 60) return 'just now';
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 7) return `${days}d ago`;
  return new Date(iso).toLocaleDateString();
}

/** "<value> (<age>)" for a channel's last known sample, or "No data yet" when it has none. */
export function formatLastValue(channel: DeviceChannel): string {
  if (channel.lastValueAt === null) return 'No data yet';
  const value = channel.lastValue !== null ? String(channel.lastValue) : (channel.lastState ?? '—');
  return `${value} (${formatAge(channel.lastValueAt)})`;
}
