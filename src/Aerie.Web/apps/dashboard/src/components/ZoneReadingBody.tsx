import type { ZoneClimate } from '../types';
import type { ZonePresentation } from '../lib/zonePresentation';
import { formatShortTime } from '../lib/format';
import { TempChart, TempChartAxis } from './TempChart';

const BADGE_CLASS: Record<ZonePresentation['status'], string> = {
  warm: 'b-warm',
  cool: 'b-cool',
  comfortable: 'b-ok',
  unknown: 'b-unknown',
};

/**
 * One zone's full reading: status row, chart, axis, low/high footer. The same
 * object in both places it appears - a ZoneCard's expanded body under "More
 * rooms" and the climate card's pane when the zone's tab is selected - shared
 * as a component so the two can't drift apart
 * (docs/plans/dashboard-redesign.md, the climate card).
 *
 * Purely presentational; the caller derives the presentation so it can also
 * drive its own chrome (the summary row's badge, a tab's swatch) from the
 * same derivation.
 */
export function ZoneReadingBody({
  zone,
  timeZone,
  presentation,
}: {
  zone: ZoneClimate;
  timeZone: string;
  presentation: ZonePresentation;
}) {
  const badgeClass = BADGE_CLASS[presentation.status];

  return (
    <>
      <div className="hf-brow">
        <span className={`hf-badge ${badgeClass}`}>{presentation.bodyBadgeLabel}</span>
        {/* The "as of" only appears when it is load-bearing - dating a live
            number is noise, and at 'none' there is nothing to date. */}
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
    </>
  );
}
