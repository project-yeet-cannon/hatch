import { useEffect, useState } from 'react';
import type { DashboardData } from './types';
import { getDashboardDataSource } from './dataSource';
import { formatClock, formatMonthDay, formatWeekday } from './lib/format';
import { hourOfDayInZone } from './lib/timezone';
import { ZoneCard } from './components/ZoneCard';
import { OutsideCard } from './components/OutsideCard';

const REFRESH_INTERVAL_MS = 60_000;

function isNight(date: Date, timeZone: string): boolean {
  const hour = hourOfDayInZone(date, timeZone);
  return hour >= 20 || hour < 6;
}

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

  if (!data) {
    if (error) {
      return (
        <div className="hfdev sky" role="alert">
          <div className="hf-note" style={{ margin: 0 }}>Couldn’t load dashboard data — {error}</div>
        </div>
      );
    }
    return null;
  }

  const themeClass = isNight(now, data.timezone) ? 'night' : 'sky';

  return (
    <div className={`hfdev ${themeClass}`}>
      <div className="hf-head">
        <div className="hf-hl">
          <span className="hf-day">{formatMonthDay(data.generatedAt, data.timezone)}</span>
          <span className="hf-date">{formatWeekday(data.generatedAt, data.timezone)}</span>
        </div>
        <div className="hf-hr">
          <span className="hf-clock">{formatClock(now, data.timezone)}</span>
        </div>
      </div>
      <div className="hf-zones">
        {data.zones.map((zone, i) => (
          <ZoneCard key={zone.id} zone={zone} timeZone={data.timezone} defaultOpen={i === 0} />
        ))}
      </div>
      <OutsideCard outside={data.outside} timeZone={data.timezone} />
    </div>
  );
}
