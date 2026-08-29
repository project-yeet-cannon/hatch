import type { ComfortStatus, ZoneClimate } from '../types';
import { deriveZonePresentation } from '../lib/zonePresentation';
import { formatShortTime } from '../lib/format';
import { TempChart, TempChartAxis } from './TempChart';

interface ZoneCardProps {
  zone: ZoneClimate;
  timeZone: string;
  /**
   * Now, on the server's clock - the snapshot's `generatedAt` advanced by how
   * long the page has held it. What a reading's age is measured against; see
   * App.tsx for why it is not `generatedAt` itself.
   */
  nowOnServerClock: string;
  defaultOpen?: boolean;
}

const SWATCH_VAR: Record<ComfortStatus, string> = {
  warm: 'var(--warm)',
  cool: 'var(--cool)',
  comfortable: 'var(--comfort)',
  unknown: 'var(--muted)',
};

const BADGE_CLASS: Record<ComfortStatus, string> = {
  warm: 'b-warm',
  cool: 'b-cool',
  comfortable: 'b-ok',
  unknown: 'b-unknown',
};

export function ZoneCard({ zone, timeZone, nowOnServerClock, defaultOpen }: ZoneCardProps) {
  const presentation = deriveZonePresentation(zone, timeZone, nowOnServerClock);
  const badgeClass = BADGE_CLASS[presentation.status];
  // 'none' is already carried by presentation.status ('unknown'), which mutes
  // the swatch, the badge and the chart on its own. 'stale' has no status of
  // its own - the reading is still the best answer anyone has - so it dims the
  // card instead, which says "trust this less" without saying "ignore this".
  const stale = presentation.freshness === 'stale';

  return (
    <details className={`hf-zone${stale ? ' stale' : ''}`} open={defaultOpen}>
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
        {/* Reads presentation rather than the raw value: at 'none' the number
            is still in the payload and is no longer worth showing, and a card
            that draws it anyway is exactly the confident-but-wrong wall this
            all exists to stop. */}
        <span className="hf-temp">
          {presentation.freshness !== 'none' && zone.currentTempF !== null ? `${Math.round(zone.currentTempF)}°` : '—'}
        </span>
        <span className={`hf-badge ${badgeClass}`}>{presentation.summaryLabel}</span>
        <span className="hf-chev">›</span>
      </summary>
      <div className="hf-body">
        <div className="hf-brow">
          <span className={`hf-badge ${badgeClass}`}>{presentation.bodyBadgeLabel}</span>
          {/* The "as of" only appears when it is load-bearing, and only in the
              expanded body - the interaction to find out how old a reading is
              is opening the card, which is the interaction that already exists. */}
          {presentation.asOfLabel && <span className="hf-asof">{presentation.asOfLabel}</span>}
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
            {zone.low ? (
              <>
                <b>{zone.low.tempF}°</b> low · {formatShortTime(zone.low.time, timeZone)}
              </>
            ) : (
              <>
                <b>—</b> low
              </>
            )}
          </span>
          <span className="hf-stat">
            {zone.high ? (
              <>
                <b>{zone.high.tempF}°</b> high · {formatShortTime(zone.high.time, timeZone)}
              </>
            ) : (
              <>
                <b>—</b> high
              </>
            )}
          </span>
        </div>
      </div>
    </details>
  );
}
