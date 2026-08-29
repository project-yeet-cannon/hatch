import type { OutsideClimate } from '../types';
import { deriveZoneStatus } from '../lib/zonePresentation';
import { formatShortTime } from '../lib/format';
import { classifyReading } from '../lib/staleness';
import { TempChart, TempChartAxis } from './TempChart';

interface OutsideCardProps {
  outside: OutsideClimate;
  timeZone: string;
  /**
   * Now, on the server's clock - the snapshot's `generatedAt` advanced by how
   * long the page has held it. What a reading's age is measured against; see
   * App.tsx for why it is not `generatedAt` itself.
   */
  nowOnServerClock: string;
}

// Outside doesn't have a comfort band of its own; classify relative to a
// generic "pleasant" range purely to pick a line color for the chart.
const OUTSIDE_COMFORT_RANGE = { lowF: 60, highF: 75 };

export function OutsideCard({ outside, timeZone, nowOnServerClock }: OutsideCardProps) {
  // The outside temperature comes down the same road as a zone's and goes stale
  // the same way, so it gets the same three states rather than a story of its
  // own - see lib/staleness.ts.
  const freshness = classifyReading(outside.currentAsOf, nowOnServerClock);
  const status = freshness === 'none' ? 'unknown' : deriveZoneStatus(outside.currentTempF, OUTSIDE_COMFORT_RANGE);

  return (
    <div className={`hf-out${freshness === 'stale' ? ' stale' : ''}`}>
      <div className="hf-brow">
        <span className="hf-swatch" style={{ background: freshness === 'none' ? 'var(--muted)' : 'var(--warm)' }} />
        <span className="hf-name">Outside</span>
        <span className="hf-temp">
          {freshness !== 'none' && outside.currentTempF !== null ? `${Math.round(outside.currentTempF)}°` : '—'}
        </span>
        {freshness === 'stale' && outside.currentAsOf !== null && (
          <span className="hf-asof">as of {formatShortTime(outside.currentAsOf, timeZone)}</span>
        )}
        {freshness === 'none' && <span className="hf-badge b-unknown">no data</span>}
      </div>
      <TempChart history={outside.history} forecast={outside.forecast} status={status} />
      <TempChartAxis history={outside.history} forecast={outside.forecast} timeZone={timeZone} />
      <div className="hf-foot">
        <span className="hf-stat">
          <b>{outside.sunHoursRemaining}h</b> sun left
        </span>
        <span className="hf-stat">
          <b>{outside.humidityPct !== null ? `${outside.humidityPct}%` : '—'}</b> humidity
        </span>
        {outside.precipitation.amountIn > 0 && (
          <span className="hf-stat">
            <b>{outside.precipitation.amountIn}"</b> rain {outside.precipitation.window}
          </span>
        )}
        <span className="hf-stat">
          <b>{formatShortTime(outside.sunsetTime, timeZone)}</b> sunset
        </span>
      </div>
      <div className="hf-note hf-out-note">{outside.note}</div>
    </div>
  );
}
