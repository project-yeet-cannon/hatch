import type { OutsideClimate } from '../types';
import { deriveZoneStatus } from '../lib/zonePresentation';
import { formatShortTime } from '../lib/format';
import { TempChart, TempChartAxis } from './TempChart';

interface OutsideCardProps {
  outside: OutsideClimate;
  timeZone: string;
}

// Outside doesn't have a comfort band of its own; classify relative to a
// generic "pleasant" range purely to pick a line color for the chart.
const OUTSIDE_COMFORT_RANGE = { lowF: 60, highF: 75 };

export function OutsideCard({ outside, timeZone }: OutsideCardProps) {
  const status = deriveZoneStatus(outside.currentTempF, OUTSIDE_COMFORT_RANGE);

  return (
    <div className="hf-out">
      <div className="hf-brow" style={{ marginBottom: 12 }}>
        <span className="hf-swatch" style={{ background: 'var(--warm)' }} />
        <span className="hf-name" style={{ width: 'auto' }}>
          Outside
        </span>
        <span className="hf-temp">{Math.round(outside.currentTempF)}°</span>
      </div>
      <TempChart history={outside.history} forecast={outside.forecast} status={status} />
      <TempChartAxis history={outside.history} forecast={outside.forecast} timeZone={timeZone} />
      <div className="hf-foot">
        <span className="hf-stat">
          <b>☀ {outside.sunHoursRemaining}h</b> sun left
        </span>
        <span className="hf-stat">
          <b>{outside.humidityPct}%</b> humidity
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
      <div className="hf-note" style={{ marginTop: 11, color: 'var(--warm-ink)' }}>
        {outside.note}
      </div>
    </div>
  );
}
