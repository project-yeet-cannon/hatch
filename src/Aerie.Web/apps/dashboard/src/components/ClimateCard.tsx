import { useState } from 'react';
import type { CSSProperties } from 'react';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import type { ComfortStatus, DayOutlook, OutlookCondition, OutsideClimate, ZoneClimate } from '../types';
import { deriveZonePresentation, deriveZoneStatus } from '../lib/zonePresentation';
import { deriveAqiPresentation } from '../lib/aqiPresentation';
import { classifyReading } from '../lib/staleness';
import { formatShortTime } from '../lib/format';
import { iconFor } from '../lib/icons';
import { TempChart, TempChartAxis } from './TempChart';
import { ZoneReadingBody } from './ZoneReadingBody';

/**
 * The climate card: Outside plus every lead zone in one card - a tab row of
 * miniature readings over one full-size pane
 * (docs/plans/dashboard-redesign.md, "The climate card").
 *
 * The tabs are deliberately the old collapsed zone row, miniaturized - swatch,
 * name, temperature - so every room's current reading stays glanceable while
 * only one chart's worth of height is spent. Selection is plain state; the
 * parent keys this component on resetToken, so the idle reset lands back on
 * Outside by remount, the same way the <details> collapse used to work.
 *
 * Staleness carries over intact from the cards this replaces: a stale
 * selection dims its number and chart and dates itself; 'none' shows the
 * muted swatch, the em dash and "no data". The *tab* of a quiet zone dims its
 * temperature too - the wall never hides which room went quiet.
 */

// Outside doesn't have a comfort band of its own; classify relative to a
// generic "pleasant" range purely to pick a line color for the chart.
const OUTSIDE_COMFORT_RANGE = { lowF: 60, highF: 75 };

const SWATCH_VAR: Record<ComfortStatus, string> = {
  warm: 'var(--warm)',
  cool: 'var(--cool)',
  comfortable: 'var(--comfort)',
  unknown: 'var(--muted)',
};

/** The outlook vocabulary's one glyph each, resolved through the FA registry routines already bundle. */
const CONDITION_ICON: Record<OutlookCondition, string> = {
  clear: 'sun',
  partlyCloudy: 'cloud-sun',
  cloudy: 'cloud',
  fog: 'smog',
  drizzle: 'cloud-rain',
  rain: 'cloud-showers-heavy',
  snow: 'snowflake',
  storm: 'cloud-bolt',
};

interface ClimateCardProps {
  outside: OutsideClimate;
  /** The pinned zones (or the fallback leads) - see lib/leadZones.ts. */
  leadZones: ZoneClimate[];
  timeZone: string;
  /** Now on the server's clock - what every reading's age is measured against. See App.tsx. */
  nowOnServerClock: string;
}

export function ClimateCard({ outside, leadZones, timeZone, nowOnServerClock }: ClimateCardProps) {
  // null selects Outside; otherwise a zone id. A poll that drops the selected
  // zone - the admin unpinned it while someone stood there - falls back to
  // Outside by the same expression the panel overlay uses for a dropped panel.
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const selectedZone = selectedId === null ? undefined : leadZones.find((zone) => zone.id === selectedId);

  const outsideFreshness = classifyReading(outside.currentAsOf, nowOnServerClock);
  const outsideStatus =
    outsideFreshness === 'none' ? 'unknown' : deriveZoneStatus(outside.currentTempF, OUTSIDE_COMFORT_RANGE);

  return (
    <div className="hf-climate">
      <div className="hf-ctabs" role="tablist" aria-label="Climate">
        <Tab
          label="Outside"
          tempF={outsideFreshness !== 'none' ? outside.currentTempF : null}
          swatch={outsideFreshness === 'none' ? 'var(--muted)' : 'var(--warm)'}
          stale={outsideFreshness === 'stale'}
          selected={selectedZone === undefined}
          onSelect={() => setSelectedId(null)}
        />
        {leadZones.map((zone) => {
          const presentation = deriveZonePresentation(zone, timeZone, nowOnServerClock);
          return (
            <Tab
              key={zone.id}
              label={zone.name}
              tempF={presentation.freshness !== 'none' ? zone.currentTempF : null}
              swatch={SWATCH_VAR[presentation.status]}
              stale={presentation.freshness === 'stale'}
              selected={selectedZone?.id === zone.id}
              onSelect={() => setSelectedId(zone.id)}
            />
          );
        })}
      </div>
      <div className="hf-climate-rule" aria-hidden="true" />
      {selectedZone === undefined ? (
        <OutsidePane
          outside={outside}
          timeZone={timeZone}
          nowOnServerClock={nowOnServerClock}
          freshness={outsideFreshness}
          status={outsideStatus}
        />
      ) : (
        <ZonePane key={selectedZone.id} zone={selectedZone} timeZone={timeZone} nowOnServerClock={nowOnServerClock} />
      )}
    </div>
  );
}

function Tab({
  label,
  tempF,
  swatch,
  stale,
  selected,
  onSelect,
}: {
  label: string;
  tempF: number | null;
  swatch: string;
  stale: boolean;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={selected}
      className={`hf-ctab${stale ? ' stale' : ''}`}
      onClick={onSelect}
    >
      <span className="hf-ctab-name">
        <span className="hf-swatch" style={{ background: swatch }} />
        <span className="hf-ctab-label">{label}</span>
      </span>
      <span className="hf-ctab-temp">{tempF !== null ? `${Math.round(tempF)}°` : '—'}</span>
    </button>
  );
}

function OutsidePane({
  outside,
  timeZone,
  nowOnServerClock,
  freshness,
  status,
}: {
  outside: OutsideClimate;
  timeZone: string;
  nowOnServerClock: string;
  freshness: ReturnType<typeof classifyReading>;
  status: ComfortStatus;
}) {
  const aqi = outside.airQuality === null ? null : deriveAqiPresentation(outside.airQuality, nowOnServerClock);
  const hasOutlook = outside.todayOutlook !== null || outside.tomorrowOutlook !== null;

  return (
    <div className={`hf-cpane${freshness === 'stale' ? ' stale' : ''}`} role="tabpanel">
      <div className="hf-cpane-head">
        <span className="hf-cpane-temp">
          {freshness !== 'none' && outside.currentTempF !== null ? `${Math.round(outside.currentTempF)}°` : '—'}
        </span>
        <span className="hf-cpane-note">{outside.note}</span>
        {freshness === 'stale' && outside.currentAsOf !== null && (
          <span className="hf-asof hf-cpane-side">as of {formatShortTime(outside.currentAsOf, timeZone)}</span>
        )}
        {freshness === 'none' && <span className="hf-badge b-unknown hf-cpane-side">no data</span>}
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
        {aqi !== null && (
          <span
            className={`hf-aqi${aqi.stale ? ' stale' : ''}`}
            style={{ '--aqi-color': aqi.color } as CSSProperties}
          >
            {aqi.label}
          </span>
        )}
      </div>
      {hasOutlook && (
        <div className="hf-outlook">
          <OutlookCell label="Today" outlook={outside.todayOutlook} />
          <OutlookCell label="Tomorrow" outlook={outside.tomorrowOutlook} />
        </div>
      )}
    </div>
  );
}

/** One of the two outlook cells. Renders nothing for a null day - one provider gap doesn't blank its sibling. */
function OutlookCell({ label, outlook }: { label: string; outlook: DayOutlook | null }) {
  if (outlook === null) return null;
  return (
    <span className="hf-outlook-cell">
      <span className="hf-outlook-label">{label}</span>
      <span className="hf-outlook-vals">
        <b>{Math.round(outlook.highF)}°</b>
        <span className="hf-outlook-lo">{Math.round(outlook.lowF)}°</span>
        <FontAwesomeIcon icon={iconFor(CONDITION_ICON[outlook.condition])} className="hf-outlook-icon" />
      </span>
    </span>
  );
}

function ZonePane({
  zone,
  timeZone,
  nowOnServerClock,
}: {
  zone: ZoneClimate;
  timeZone: string;
  nowOnServerClock: string;
}) {
  const presentation = deriveZonePresentation(zone, timeZone, nowOnServerClock);
  return (
    <div className={`hf-cpane${presentation.freshness === 'stale' ? ' stale' : ''}`} role="tabpanel">
      <div className="hf-cpane-head">
        <span className="hf-cpane-temp">
          {presentation.freshness !== 'none' && zone.currentTempF !== null ? `${Math.round(zone.currentTempF)}°` : '—'}
        </span>
      </div>
      <ZoneReadingBody zone={zone} timeZone={timeZone} presentation={presentation} />
    </div>
  );
}
