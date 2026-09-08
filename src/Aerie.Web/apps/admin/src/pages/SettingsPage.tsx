import { useEffect, useState } from 'react';
import { Button, Card, EmptyState, Field, Grid, PageHeader, Text } from '@aerie/ui';
import { getAlerts, getSettings, putSetting } from '../api/client';
import type { HazardAlert } from '../types';

interface FieldDef {
  key: string;
  label: string;
  type: 'text' | 'number' | 'password';
  help: string;
}

const FIELDS: FieldDef[] = [
  { key: 'TimeZone', label: 'Time zone', type: 'text', help: 'IANA timezone, e.g. America/Denver' },
  { key: 'Latitude', label: 'Latitude', type: 'number', help: 'Degrees, used to calculate sunset locally, e.g. 40.7128' },
  { key: 'Longitude', label: 'Longitude', type: 'number', help: 'Degrees, used to calculate sunset locally, e.g. -74.0060' },
  { key: 'WeatherEntity', label: 'Weather entity', type: 'text', help: 'HA entity id, e.g. weather.home' },
  { key: 'ComfortToleranceF', label: 'Comfort tolerance (°F)', type: 'number', help: '' },
  { key: 'DefaultComfortLowF', label: 'Default comfort low (°F)', type: 'number', help: '' },
  { key: 'DefaultComfortHighF', label: 'Default comfort high (°F)', type: 'number', help: '' },
  { key: 'HomeAssistantHost', label: 'Home Assistant host', type: 'text', help: 'Hostname or IP the API connects to, e.g. homeassistant.local' },
  { key: 'HomeAssistantPort', label: 'Home Assistant port', type: 'number', help: 'e.g. 8123' },
  {
    key: 'HomeAssistantToken',
    label: 'Home Assistant token',
    type: 'password',
    help: 'Long-lived access token. Stored obfuscated; leave blank to keep the current value.',
  },
  {
    key: 'MediaLibraryBaseUrl',
    label: 'Media library base URL',
    type: 'text',
    help: 'Base URL speakers fetch tracks from, e.g. https://home.example.com/media. Must be reachable from the speakers themselves, and match what MediaLibrary:RootPath serves.',
  },
  { key: 'KioskWifiSsid', label: 'Kiosk Wi-Fi SSID', type: 'text', help: 'Wi-Fi network the kiosk tablet joins during QR provisioning' },
  { key: 'KioskWifiSecurityType', label: 'Kiosk Wi-Fi security type', type: 'text', help: 'WPA, WEP, or blank for an open network' },
  {
    key: 'KioskWifiPassword',
    label: 'Kiosk Wi-Fi password',
    type: 'password',
    help: 'Stored obfuscated; leave blank to keep the current value.',
  },
  {
    key: 'GoogleClientId',
    label: 'Google client ID',
    type: 'text',
    help: 'OAuth client ID from your own Google Cloud project, used to connect family calendars. See the setup note on the Calendars page.',
  },
  {
    key: 'GoogleClientSecret',
    label: 'Google client secret',
    type: 'password',
    help: 'Stored obfuscated; leave blank to keep the current value.',
  },
  {
    key: 'GoogleOAuthRedirectUri',
    label: 'Google OAuth redirect URI',
    type: 'text',
    help: 'Only needed when a proxy rewrites the address this app is served from. Blank derives it from the request, which is usually right. Must match the Cloud console exactly.',
  },
  {
    key: 'CalendarAgendaDays',
    label: 'Calendar agenda days',
    type: 'number',
    help: 'How many days of agenda the kiosk shows, counting today. Defaults to 2 - today and tomorrow.',
  },
  {
    key: 'WeatherAlertProvider',
    label: 'Weather alert provider',
    type: 'text',
    help: 'Which service supplies watches and warnings. "nws" (api.weather.gov, US only) or "none" to turn them off.',
  },
  {
    key: 'WeatherAlertContact',
    label: 'Weather alert contact',
    type: 'text',
    help: 'An email or URL sent as the User-Agent when fetching alerts. NWS asks callers to identify themselves and may throttle traffic it cannot.',
  },
  {
    key: 'AirQualityProvider',
    label: 'Air quality provider',
    type: 'text',
    help: 'Which service supplies air quality. "open-meteo" (global, keyless) or "none" to turn it off.',
  },
  {
    key: 'AirQualityAlertThresholdAqi',
    label: 'Air quality alert threshold (US AQI)',
    type: 'number',
    help: 'The kiosk says nothing below this. Defaults to 101, the bottom of "Unhealthy for Sensitive Groups".',
  },
  // The Anthropic API key, and not the Claude subscription token beside it in
  // the settings table: that one is Hatch's, and it is set on Hatch's own
  // Settings page so an installation with no admin app can still set it. The
  // key stays here because what it pays for - the family Game app - is here.
  // Both are still ordinary site settings; only the page moved.
  {
    key: 'AnthropicApiKey',
    label: 'Anthropic API key',
    type: 'password',
    help: 'Lets the family Game app write games from what a child types. Billed to this key, so it is yours to supply. Stored obfuscated; leave blank to keep the current value.',
  },
  {
    key: 'HazardMaxSeverityAgeHours',
    label: 'Hazard window (hours)',
    type: 'number',
    help: 'How far ahead an alert may start and still be shown. Defaults to 48 - today and tomorrow.',
  },
];

const PASSWORD_KEYS = new Set(FIELDS.filter((f) => f.type === 'password').map((f) => f.key));

export function SettingsPage() {
  const [values, setValues] = useState<Record<string, string>>({});
  // Password fields come back from the API redacted, so we track "is a value
  // already set" separately instead of loading the redacted string into the
  // editable input.
  const [configured, setConfigured] = useState<Record<string, boolean>>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [savingKey, setSavingKey] = useState<string | null>(null);
  const [savedKey, setSavedKey] = useState<string | null>(null);
  // Separate from `error` above: the hazard settings are still editable when
  // the alerts endpoint is unreachable, and one card failing shouldn't read as
  // the page failing.
  const [alerts, setAlerts] = useState<HazardAlert[] | null>(null);
  const [alertsError, setAlertsError] = useState<string | null>(null);
  const [refreshingAlerts, setRefreshingAlerts] = useState(false);

  useEffect(() => {
    load();
    loadAlerts();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      const settings = await getSettings();
      setValues(Object.fromEntries(settings.filter((s) => !PASSWORD_KEYS.has(s.key)).map((s) => [s.key, s.value])));
      setConfigured(Object.fromEntries(settings.filter((s) => PASSWORD_KEYS.has(s.key)).map((s) => [s.key, s.value.length > 0])));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  async function loadAlerts() {
    setRefreshingAlerts(true);
    setAlertsError(null);
    try {
      setAlerts(await getAlerts());
    } catch (err) {
      setAlertsError(err instanceof Error ? err.message : String(err));
    } finally {
      setRefreshingAlerts(false);
    }
  }

  async function save(key: string) {
    // Password fields left blank mean "no change" - there's nothing to send.
    if (PASSWORD_KEYS.has(key) && !values[key]) return;

    setSavingKey(key);
    setSavedKey(null);
    setError(null);
    try {
      await putSetting(key, values[key] ?? '');
      setSavedKey(key);
      if (PASSWORD_KEYS.has(key)) {
        setConfigured((prev) => ({ ...prev, [key]: true }));
        setValues((prev) => ({ ...prev, [key]: '' }));
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSavingKey(null);
    }
  }

  return (
    <div>
      <PageHeader title="Settings" />

      {error && <Text tone="danger" className="mb-2">{error}</Text>}
      {loading && <Text tone="muted">Loading…</Text>}

      {!loading && (
        <Card>
          <Grid cols={2}>
            {FIELDS.map((field) => (
              <Field label={field.label} key={field.key}>
                <div className="flex gap-1">
                  <input
                    type={field.type}
                    value={values[field.key] ?? ''}
                    placeholder={field.type === 'password' && configured[field.key] ? 'Token is set — enter a new value to change it' : undefined}
                    onChange={(e) => {
                      setValues((prev) => ({ ...prev, [field.key]: e.target.value }));
                      setSavedKey((prev) => (prev === field.key ? null : prev));
                    }}
                  />
                  <Button
                    disabled={savingKey === field.key || (field.type === 'password' && !values[field.key])}
                    onClick={() => save(field.key)}
                  >
                    {savingKey === field.key ? 'Saving…' : savedKey === field.key ? 'Saved' : 'Save'}
                  </Button>
                </div>
                {field.help && <Text tone="muted" className="mt-1">{field.help}</Text>}
              </Field>
            ))}
          </Grid>
        </Card>
      )}

      {/* Config verification, which is why it sits under the settings that
          produce it rather than on a page of its own: an operator who has just
          set their coordinates and provider can see whether anything came back.
          Empty is the normal answer on a calm, clean-air day. */}
      <Card className="mt-2">
        <div className="flex gap-1">
          <h3>Active alerts</h3>
          <Button disabled={refreshingAlerts} onClick={loadAlerts}>
            {refreshingAlerts ? 'Refreshing…' : 'Refresh'}
          </Button>
        </div>
        <Text tone="muted" className="mt-1">
          What the kiosk would show right now, from the cached hazard data. Nothing here on a calm day with clean air —
          that is a working configuration, not a broken one. The sync job runs every 15 minutes, so a setting changed
          just now takes that long to show up.
        </Text>
        {alertsError && <Text tone="danger" className="mb-2">{alertsError}</Text>}
        {alerts && alerts.length === 0 && <EmptyState message="No active alerts." />}
        {alerts && alerts.length > 0 && (
          <ul>
            {alerts.map((alert) => (
              <li key={alert.id}>
                <strong>{alert.title}</strong> — {alert.severity} · {alert.kind === 'AirQuality' ? 'air quality' : 'weather'}
                {alert.detail && <Text as="span" tone="muted"> · {alert.detail}</Text>}
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}
