import { useEffect, useState } from 'react';
import type { CSSProperties } from 'react';
import type { DashboardData } from './types';
import { getDashboardDataSource } from './dataSource';
import { DEFAULT_TIME_ZONE } from './config';
import { formatClockParts, formatMonthDay, formatWeekday } from './lib/format';
import { getCircadianPhase, resolveThemeStyle } from './lib/circadianTheme';
import { circadianTokens } from './theme/tokens';
import { ZoneCard } from './components/ZoneCard';
import { OutsideCard } from './components/OutsideCard';
import { RoutinesSection } from './components/RoutinesSection';
import { CalendarSection } from './components/CalendarSection';
import { AlertBanner } from './components/AlertBanner';
import { DashboardSkeleton } from './components/DashboardSkeleton';
import { clientLogger } from './lib/clientLogger';
import { useKioskLifecycle } from './hooks/useKioskLifecycle';

const REFRESH_INTERVAL_MS = 60_000;

export function App() {
  const [data, setData] = useState<DashboardData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [now, setNow] = useState(() => new Date());
  // Bumps ~30s after the last touch; see hooks/useKioskLifecycle.ts. Also owns
  // the reload-on-new-deploy side of the kiosk's lifecycle, which needs nothing
  // from this component.
  const resetToken = useKioskLifecycle();

  useEffect(() => {
    clientLogger.info('App mounted, starting dashboard data source');
    const source = getDashboardDataSource();
    let cancelled = false;
    let firstLoad = true;

    const load = () => {
      source
        .getDashboardData()
        .then((snapshot) => {
          if (cancelled) return;
          clientLogger.info(firstLoad ? 'Initial dashboard data loaded' : 'Dashboard data refreshed', {
            zoneCount: snapshot.zones.length,
          });
          firstLoad = false;
          setData(snapshot);
          setError(null);
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          const message = err instanceof Error ? err.message : String(err);
          clientLogger.error('Dashboard data load failed', { firstLoad, reason: message });
          setError(message);
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
  const clock = formatClockParts(now, timeZone);

  return (
    <div className="hf-page" style={themeStyle as CSSProperties}>
      <div className="hfdev">
        <div className="hf-head">
          <div className="hf-hl">
            <span className="hf-day">{formatMonthDay(data?.generatedAt ?? now.toISOString(), timeZone)}</span>
            <span className="hf-date">{formatWeekday(data?.generatedAt ?? now.toISOString(), timeZone)}</span>
          </div>
          <div className="hf-hr">
            <span className="hf-clock">
              {clock.time}
              <span className="hf-ampm">{clock.period}</span>
            </span>
          </div>
        </div>
        {data ? (
          <>
            {/* The top of the column, above everything: a hazard is the one
                thing here that changes what you do on the way out the door. It
                renders nothing when there is nothing active, which is most
                days - so sitting here costs a calm day no space at all. */}
            <AlertBanner alerts={data.alerts} timeZone={data.timezone} />
            {/* Keyed on resetToken so an idle reset remounts the cards, which is
                what puts each <details> back to defaultOpen - `open` is
                uncontrolled DOM state that no re-render would otherwise undo. */}
            <div className="hf-zones" key={resetToken}>
              <OutsideCard outside={data.outside} timeZone={data.timezone} />
              {data.zones.map((zone, i) => (
                <ZoneCard key={zone.id} zone={zone} timeZone={data.timezone} defaultOpen={i === 0} />
              ))}
            </div>
            {/* Below the zones, above the routines: the agenda is read, the
                routines are touched, so the reachable half of the screen stays
                the tappable one. An agenda whose every day is empty renders
                nothing at all - an empty day is only worth saying when some
                other day isn't. */}
            {data.calendar.some((day) => day.events.length > 0) && (
              <CalendarSection calendar={data.calendar} timeZone={data.timezone} now={now} />
            )}
            {data.routines.length > 0 && <RoutinesSection routines={data.routines} resetToken={resetToken} />}
          </>
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
