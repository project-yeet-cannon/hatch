import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import type { DashboardData } from './types';
import { getDashboardDataSource } from './dataSource';
import { DEFAULT_TIME_ZONE } from './config';
import { formatClock, formatMonthDay, formatWeekday } from './lib/format';
import { getCircadianPhase, resolveThemeStyle } from './lib/circadianTheme';
import { circadianTokens } from './theme/tokens';
import { ZoneCard } from './components/ZoneCard';
import { OutsideCard } from './components/OutsideCard';
import { DashboardSkeleton } from './components/DashboardSkeleton';

const REFRESH_INTERVAL_MS = 60_000;

export function App() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());

  useEffect(() => {
    const source = getDashboardDataSource();
    let cancelled = false;

    const load = () => {
      source
        .getDashboardData()
        .then((snapshot) => {
          if (cancelled) return;
          setData(snapshot);
          setError(null);
        })
        .catch((err: unknown) => {
          if (!cancelled) setError(err instanceof Error ? err.message : String(err));
        });
    };

    load();
    const refresh = setInterval(load, REFRESH_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(refresh);
    };
  }, []);

  useEffect(() => {
    const tick = setInterval(() => setNow(new Date()), 15_000);
    return () => clearInterval(tick);
  }, []);

  // Before the first snapshot arrives we don't know the house's timezone yet,
  // so fall back to the configured default — it's only used for a couple of
  // seconds' worth of header rendering and gets replaced once `data` loads.
  const timeZone = data?.timezone ?? DEFAULT_TIME_ZONE;
  const themeStyle = data
    ? resolveThemeStyle(getCircadianPhase(now, data.sunEvents), circadianTokens)
    : resolveThemeStyle({ kind: 'day' }, circadianTokens);

  return (
    <div className="hf-page" style={themeStyle as CSSProperties}>
      <div className="hfdev">
        <div className="hf-head">
          <div className="hf-hl">
            <span className="hf-day">{formatMonthDay(data?.generatedAt ?? now.toISOString(), timeZone)}</span>
            <span className="hf-date">{formatWeekday(data?.generatedAt ?? now.toISOString(), timeZone)}</span>
          </div>
          <div className="hf-hr">
            <span className="hf-clock">{formatClock(now, timeZone)}</span>
          </div>
        </div>
        {data ? (
          <div className="hf-zones">
            <OutsideCard outside={data.outside} timeZone={data.timezone} />
            {data.zones.map((zone, i) => (
              <ZoneCard key={zone.id} zone={zone} timeZone={data.timezone} defaultOpen={i === 0} />
            ))}
          </div>
        ) : error ? (
          <div className="hf-note" role="alert" style={{ margin: 0 }}>
            Couldn’t load dashboard data — {error}
          </div>
        ) : (
          <DashboardSkeleton />
        )}
      </div>
    </div>
  );
}
