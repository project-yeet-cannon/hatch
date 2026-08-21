export function formatWeekday(iso: string, timeZone: string): string {
  return new Intl.DateTimeFormat('en-US', { weekday: 'long', timeZone }).format(new Date(iso));
}

export function formatMonthDay(iso: string, timeZone: string): string {
  return new Intl.DateTimeFormat('en-US', { month: 'long', day: 'numeric', timeZone }).format(
    new Date(iso),
  );
}

/** Clock time split from its AM/PM period, so they can be sized independently. */
export function formatClockParts(date: Date, timeZone: string): { time: string; period: string } {
  const parts = new Intl.DateTimeFormat('en-US', {
    hour: 'numeric',
    minute: '2-digit',
    hour12: true,
    timeZone,
  }).formatToParts(date);
  const hour = parts.find((p) => p.type === 'hour')?.value ?? '';
  const minute = parts.find((p) => p.type === 'minute')?.value ?? '';
  const period = parts.find((p) => p.type === 'dayPeriod')?.value ?? '';
  return { time: `${hour}:${minute}`, period };
}

/** Compact axis label, e.g. "6a", "10p", "now". */
export function formatAxisHour(iso: string, timeZone: string): string {
  const hour = Number(
    new Intl.DateTimeFormat('en-US', { hour: 'numeric', hourCycle: 'h23', timeZone }).format(
      new Date(iso),
    ),
  );
  const period = hour < 12 ? 'a' : 'p';
  const twelveHour = hour % 12 === 0 ? 12 : hour % 12;
  return `${twelveHour}${period}`;
}

export function formatShortTime(iso: string, timeZone: string): string {
  return new Intl.DateTimeFormat('en-US', {
    hour: 'numeric',
    minute: '2-digit',
    timeZone,
  })
    .format(new Date(iso))
    .toLowerCase()
    .replace(' ', '');
}

/**
 * A "YYYY-MM-DD" agenda date as a day heading. Parsed field by field into a
 * local Date rather than through `new Date(iso)`, which would read it as UTC
 * midnight and render the day before it anywhere west of Greenwich.
 */
export function formatAgendaDate(date: string): string {
  const [year, month, day] = date.split('-').map(Number);
  return new Intl.DateTimeFormat('en-US', { weekday: 'long', month: 'short', day: 'numeric' }).format(
    new Date(year, month - 1, day),
  );
}
