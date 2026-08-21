import { useEffect, useState } from 'react';
import { getSettings, putSetting } from '../api/client';

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

  useEffect(() => {
    load();
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
      <div className="admin-page-header">
        <h2>Settings</h2>
      </div>

      {error && <p className="text-danger mb-2">{error}</p>}
      {loading && <p className="text-muted">Loading…</p>}

      {!loading && (
        <div className="card">
          <div className="grid cols-2">
            {FIELDS.map((field) => (
              <div className="field" key={field.key}>
                <label className="field-label">{field.label}</label>
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
                  <button
                    className="btn-secondary"
                    disabled={savingKey === field.key || (field.type === 'password' && !values[field.key])}
                    onClick={() => save(field.key)}
                  >
                    {savingKey === field.key ? 'Saving…' : savedKey === field.key ? 'Saved' : 'Save'}
                  </button>
                </div>
                {field.help && <p className="text-muted mt-1">{field.help}</p>}
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
