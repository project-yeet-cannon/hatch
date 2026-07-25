import { useEffect, useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import type { DashboardData, SunEvents } from '../types';
import { type CircadianPhase, getCircadianPhase, resolveThemeStyle } from '../lib/circadianTheme';
import { circadianTokens } from '../theme/tokens';
import { DEFAULT_TIME_ZONE } from '../config';
import { MockDashboardDataSource } from '../mock/mockDataSource';
import { ZoneCard } from '../components/ZoneCard';
import { OutsideCard } from '../components/OutsideCard';

const MINUTES_PER_DAY = 24 * 60;
const SUN_EVENTS_DEBOUNCE_MS = 300;
const PLAY_TICK_MS = 200;
const SPEED_OPTIONS = [1, 4, 15, 60] as const;

function todayUtcIso(): string {
  return new Date().toISOString().slice(0, 10);
}

function formatZonedTime(date: Date, timeZone: string): string {
  return new Intl.DateTimeFormat('en-US', {
    hour: 'numeric',
    minute: '2-digit',
    timeZoneName: 'short',
    timeZone,
  }).format(date);
}

function describePhase(phase: CircadianPhase): string {
  switch (phase.kind) {
    case 'day':
      return 'Full light';
    case 'night':
      return 'Full dark';
    case 'eveningTransition':
      return `Evening transition — ${Math.round(phase.progress * 100)}%`;
    case 'morningTransition':
      return `Morning transition — ${Math.round(phase.progress * 100)}%`;
  }
}

/**
 * Dev-only theme scrubber. Fetches the day's real sun events for the site's
 * configured location from GET /api/sun-events (same endpoint + solar math
 * the production dashboard uses server-side), then lets you override the
 * date/location and scrub a simulated "now" across it. Renders through the
 * exact same getCircadianPhase/resolveThemeStyle functions App.tsx uses, so
 * what you see here is what the real dashboard would show at that instant.
 */
export function DevThemePage() {
  const [date, setDate] = useState(todayUtcIso());
  const [lat, setLat] = useState('');
  const [lon, setLon] = useState('');
  const [events, setEvents] = useState<SunEvents | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [minuteOfDay, setMinuteOfDay] = useState(12 * 60);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState<number>(SPEED_OPTIONS[1]);
  const [fixture, setFixture] = useState<DashboardData | null>(null);

  // Fixture zone/outside data for the example components below - fetched
  // once, unaffected by the controls (only the theme reacts to those).
  useEffect(() => {
    new MockDashboardDataSource().getDashboardData().then(setFixture).catch(() => {});
  }, []);

  // Real sun events for the selected date/location, debounced so typing in
  // the lat/lon fields doesn't fire a request per keystroke.
  useEffect(() => {
    const params = new URLSearchParams({ at: `${date}T12:00:00Z` });
    if (lat.trim() !== '') params.set('lat', lat.trim());
    if (lon.trim() !== '') params.set('lon', lon.trim());

    let cancelled = false;
    const timer = setTimeout(() => {
      fetch(`/api/sun-events?${params}`)
        .then((res) => {
          if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
          return res.json() as Promise<SunEvents>;
        })
        .then((data) => {
          if (cancelled) return;
          setEvents(data);
          setError(null);
        })
        .catch((err: unknown) => {
          if (!cancelled) setError(err instanceof Error ? err.message : String(err));
        });
    }, SUN_EVENTS_DEBOUNCE_MS);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [date, lat, lon]);

  useEffect(() => {
    if (!playing) return;
    const id = setInterval(() => {
      setMinuteOfDay((m) => (m + speed) % MINUTES_PER_DAY);
    }, PLAY_TICK_MS);
    return () => clearInterval(id);
  }, [playing, speed]);

  const simulatedNow = useMemo(
    () => new Date(Date.parse(`${date}T00:00:00.000Z`) + minuteOfDay * 60_000),
    [date, minuteOfDay],
  );

  const phase = events ? getCircadianPhase(simulatedNow, events) : null;
  const style = phase ? resolveThemeStyle(phase, circadianTokens) : {};

  return (
    <div className="hf-page devtheme-page" style={style as CSSProperties}>
      <div className="hfdev devtheme-panel">
        <h1 className="devtheme-title">Circadian theme preview (dev)</h1>
        {error && <p className="devtheme-error">Sun events request failed: {error}</p>}

        <div className="devtheme-controls">
          <label>
            Date
            <input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </label>
          <label>
            Time — {formatZonedTime(simulatedNow, DEFAULT_TIME_ZONE)}
            <input
              type="range"
              min={0}
              max={MINUTES_PER_DAY - 1}
              value={minuteOfDay}
              onChange={(e) => setMinuteOfDay(Number(e.target.value))}
            />
          </label>
          <label>
            Latitude
            <input type="number" placeholder="site default" value={lat} onChange={(e) => setLat(e.target.value)} />
          </label>
          <label>
            Longitude
            <input type="number" placeholder="site default" value={lon} onChange={(e) => setLon(e.target.value)} />
          </label>
          <div className="devtheme-playback">
            <button type="button" onClick={() => setPlaying((p) => !p)}>
              {playing ? 'Pause' : 'Play'}
            </button>
            <label>
              Speed
              <select value={speed} onChange={(e) => setSpeed(Number(e.target.value))}>
                {SPEED_OPTIONS.map((s) => (
                  <option key={s} value={s}>
                    {s}x
                  </option>
                ))}
              </select>
            </label>
          </div>
        </div>

        {phase && events && (
          <p className="devtheme-readout">
            Phase: <b>{describePhase(phase)}</b>
            <br />
            Dawn {formatTime(events.dawn)} · Sunrise {formatTime(events.sunrise)} · Sunset {formatTime(events.sunset)}{' '}
            · Dusk {formatTime(events.dusk)}
          </p>
        )}

        <div className="devtheme-swatches">
          {Object.entries(style).map(([name, value]) => (
            <div key={name} className="devtheme-swatch">
              <span
                className="devtheme-swatch-chip"
                style={name === '--shadow' ? { boxShadow: value, background: '#888' } : { background: value }}
              />
              <code>{name}</code>
            </div>
          ))}
        </div>
      </div>

      {fixture && (
        <div className="hf-zones devtheme-fixture">
          <OutsideCard outside={fixture.outside} timeZone={fixture.timezone} />
          {fixture.zones[0] && <ZoneCard zone={fixture.zones[0]} timeZone={fixture.timezone} defaultOpen />}
        </div>
      )}
    </div>
  );
}

function formatTime(iso: string): string {
  return formatZonedTime(new Date(iso), DEFAULT_TIME_ZONE);
}
