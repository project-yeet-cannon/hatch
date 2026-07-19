import type { ZoneClimate } from '../types';
import { deriveZonePresentation } from '../lib/zonePresentation';
import { formatShortTime } from '../lib/format';
import { TempChart, TempChartAxis } from './TempChart';

interface ZoneCardProps {
  zone: ZoneClimate;
  timeZone: string;
  defaultOpen?: boolean;
}

const SWATCH_VAR: Record<string, string> = {
  warm: 'var(--warm)',
  cool: 'var(--cool)',
  comfortable: 'var(--comfort)',
};

const BADGE_CLASS: Record<string, string> = {
  warm: 'b-warm',
  cool: 'b-cool',
  comfortable: 'b-ok',
};

export function ZoneCard({ zone, timeZone, defaultOpen }: ZoneCardProps) {
  const presentation = deriveZonePresentation(zone, timeZone);
  const badgeClass = BADGE_CLASS[presentation.status];

  return (
    <details className="hf-zone" open={defaultOpen}>
      <summary className="hf-zsum">
        <span className="hf-swatch" style={{ background: SWATCH_VAR[presentation.status] }} />
        <span className="hf-name">{zone.name}</span>
        <TempChart
          history={zone.history}
          forecast={zone.forecast}
          comfortRange={zone.comfortRange}
          status={presentation.status}
          compact
        />
        <span className="hf-temp">{Math.round(zone.currentTempF)}°</span>
        <span className={`hf-badge ${badgeClass}`}>{presentation.summaryLabel}</span>
        <span className="hf-chev">›</span>
      </summary>
      <div className="hf-body">
        <div className="hf-brow">
          <span className={`hf-badge ${badgeClass}`}>{presentation.bodyBadgeLabel}</span>
          <span className="hf-note">{presentation.statusNote}</span>
        </div>
        <TempChart
          history={zone.history}
          forecast={zone.forecast}
          comfortRange={zone.comfortRange}
          status={presentation.status}
        />
        <TempChartAxis history={zone.history} forecast={zone.forecast} timeZone={timeZone} />
        <div className="hf-foot">
          <span className="hf-stat">
            <b>{zone.low.tempF}°</b> low · {formatShortTime(zone.low.time, timeZone)}
          </span>
          <span className="hf-stat">
            <b>{zone.high.tempF}°</b> high · {formatShortTime(zone.high.time, timeZone)}
          </span>
        </div>
      </div>
    </details>
  );
}
