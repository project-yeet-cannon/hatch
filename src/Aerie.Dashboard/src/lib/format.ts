export function formatWeekday(iso: string, timeZone: string): string {
  return new Intl.DateTimeFormat('en-US', { weekday: 'long', timeZone }).format(new Date(iso));
}

export function formatMonthDay(iso: string, timeZone: string): string {
  return new Intl.DateTimeFormat('en-US', { month: 'long', day: 'numeric', timeZone }).format(
    new Date(iso),
  );
}

export function formatClock(date: Date, timeZone: string): string {
  return new Intl.DateTimeFormat('en-US', {
    hour: 'numeric',
    minute: '2-digit',
    timeZone,
  }).format(date);
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
