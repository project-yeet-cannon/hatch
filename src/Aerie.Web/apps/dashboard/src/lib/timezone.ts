/** Pure timezone math - no house-specific knowledge lives here. */

/** Offset of `timeZone` from UTC, in minutes, at the given instant (DST-aware). */
function offsetMinutes(date: Date, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
    .formatToParts(date)
    .reduce<Record<string, string>>((acc, p) => {
      acc[p.type] = p.value;
      return acc;
    }, {});

  const asUtc = Date.UTC(
    Number(parts.year),
    Number(parts.month) - 1,
    Number(parts.day),
    Number(parts.hour),
    Number(parts.minute),
    Number(parts.second),
  );
  return (asUtc - date.getTime()) / 60_000;
}

/** Hour of day (0-24, fractional) as displayed in `timeZone` at the given instant. */
export function hourOfDayInZone(date: Date, timeZone: string): number {
  const zoned = new Date(date.getTime() + offsetMinutes(date, timeZone) * 60_000);
  return zoned.getUTCHours() + zoned.getUTCMinutes() / 60;
}

/**
 * The instant `hour:minute` occurs in `timeZone`, on the same calendar day
 * (in that zone) as `date`. Used for zone-local "events" like sunset.
 */
export function zonedWallClock(date: Date, timeZone: string, hour: number, minute: number): Date {
  const offset = offsetMinutes(date, timeZone);
  const zonedNow = new Date(date.getTime() + offset * 60_000);
  const zonedTarget = new Date(
    Date.UTC(zonedNow.getUTCFullYear(), zonedNow.getUTCMonth(), zonedNow.getUTCDate(), hour, minute, 0, 0),
  );
  return new Date(zonedTarget.getTime() - offset * 60_000);
}
