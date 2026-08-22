import type { ZoneClimate } from '../types';
import { formatClockParts, formatMonthDay, formatWeekday } from '../lib/format';

/**
 * What the wall is when nobody has walked up to it: a clock, the date, and one
 * indoor temperature, all sized to be read from the far end of a hallway rather
 * than from arm's length.
 *
 * It covers the dashboard rather than replacing it (see .hf-standby) - the
 * snapshot underneath keeps polling, so lifting standby shows current data
 * instantly instead of a skeleton, and nothing here has to re-fetch on the way
 * back. Fixed positioning also means mounting it moves no scroll position,
 * which matters more than it sounds: a scroll would arrive back at
 * lib/kioskLifecycle.ts as user activity and lift the standby it just entered.
 *
 * Deliberately not a screensaver. Nothing moves, nothing animates, nothing
 * asks to be looked at - the whole point is a display that has stopped
 * competing for attention.
 */
export function StandbyView({
  now,
  timeZone,
  zones,
}: {
  now: Date;
  timeZone: string;
  zones: ZoneClimate[];
}) {
  const clock = formatClockParts(now, timeZone);
  const iso = now.toISOString();
  // Zones arrive in the operator's own order (ZoneService sorts by SortOrder,
  // then name), so the first one with a reading is the closest thing to "the
  // house temperature" that exists without inventing a new setting for it.
  const indoor = zones.find((zone) => zone.currentTempF !== null) ?? null;
  const indoorTempF = indoor?.currentTempF ?? null;

  return (
    <div className="hf-standby">
      <div className="hf-sb-clock">
        {clock.time}
        <span className="hf-sb-ampm">{clock.period}</span>
      </div>
      <div className="hf-sb-date">
        {formatWeekday(iso, timeZone)}, {formatMonthDay(iso, timeZone)}
      </div>
      {/* Omitted entirely rather than shown as a dash: a placeholder at this
          size is a large piece of furniture saying nothing. */}
      {indoor !== null && indoorTempF !== null && (
        <div className="hf-sb-temp">
          <span className="hf-sb-degrees">{Math.round(indoorTempF)}°</span>
          <span className="hf-sb-zone">{indoor.name}</span>
        </div>
      )}
    </div>
  );
}
