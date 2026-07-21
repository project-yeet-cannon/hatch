import { useEffect, useState } from 'react';
import { getSettings, putSetting } from '../api/client';
import { DEFAULT_HOME_ASSISTANT_BASE_URL } from '../lib/format';

interface FieldDef {
  key: string;
  label: string;
  type: 'text' | 'number';
  help: string;
}

const FIELDS: FieldDef[] = [
  { key: 'TimeZone', label: 'Time zone', type: 'text', help: 'IANA timezone, e.g. America/Denver' },
  { key: 'SunEntity', label: 'Sun entity', type: 'text', help: 'HA entity id, e.g. sun.sun' },
  { key: 'WeatherEntity', label: 'Weather entity', type: 'text', help: 'HA entity id, e.g. weather.home' },
  { key: 'ComfortToleranceF', label: 'Comfort tolerance (°F)', type: 'number', help: '' },
  { key: 'DefaultComfortLowF', label: 'Default comfort low (°F)', type: 'number', help: '' },
  { key: 'DefaultComfortHighF', label: 'Default comfort high (°F)', type: 'number', help: '' },
  {
    key: 'HomeAssistantBaseUrl',
    label: 'Home Assistant base URL',
    type: 'text',
    help: `Used to link device pages back to Home Assistant. Defaults to ${DEFAULT_HOME_ASSISTANT_BASE_URL} if unset.`,
  },
];

export function SettingsPage() {
  const [values, setValues] = useState<Record<string, string>>({});
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
      setValues(Object.fromEntries(settings.map((s) => [s.key, s.value])));
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }

  async function save(key: string) {
    setSavingKey(key);
    setSavedKey(null);
    setError(null);
    try {
      await putSetting(key, values[key] ?? '');
      setSavedKey(key);
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
                    onChange={(e) => {
                      setValues((prev) => ({ ...prev, [field.key]: e.target.value }));
                      setSavedKey((prev) => (prev === field.key ? null : prev));
                    }}
                  />
                  <button
                    className="btn-secondary"
                    disabled={savingKey === field.key}
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
