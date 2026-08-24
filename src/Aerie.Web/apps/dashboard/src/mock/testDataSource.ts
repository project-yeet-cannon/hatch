import type {
  CalendarDay,
  CameraSummary,
  ComfortRange,
  DailyExtreme,
  DashboardData,
  DashboardDataSource,
  HazardAlert,
  HourlyOutside,
  OutsideClimate,
  RoutineSummary,
  SunEvents,
  TempPoint,
  ZoneClimate,
} from '../types';

const HOUR_MS = 60 * 60 * 1000;
const HISTORY_HOURS = 9;
const FORECAST_HOURS = 7;
const STEP_HOURS = 0.5;

const X = 'XXXX';
const X_LONG = 'XXXXXXXXXXXXXXXX';
const NUM = 9999;

// Timestamps and the timezone have to stay real/valid: the charts parse and
// scale them, and an invalid IANA zone throws inside Intl and blanks the
// whole page. This source is for catching hardcoded *content*, not for
// exercising time handling, so those two are deliberately exempt from the
// X/9999 treatment.
const TIME_ZONE = 'UTC';

function timeWindow(now: Date): { history: Date[]; forecast: Date[] } {
  const history: Date[] = [];
  for (let h = -HISTORY_HOURS; h <= 0; h += STEP_HOURS) {
    history.push(new Date(now.getTime() + h * HOUR_MS));
  }
  const forecast: Date[] = [];
  for (let h = 0; h <= FORECAST_HOURS; h += STEP_HOURS) {
    forecast.push(new Date(now.getTime() + h * HOUR_MS));
  }
  return { history, forecast };
}

function xSeries(dates: Date[]): TempPoint[] {
  return dates.map((d) => ({ time: d.toISOString(), tempF: NUM }));
}

function xExtreme(date: Date): DailyExtreme {
  return { tempF: NUM, time: date.toISOString() };
}

function xComfortRange(): ComfortRange {
  return { lowF: NUM, highF: NUM };
}

function xRoutine(index: number): RoutineSummary {
  return { id: `${X}-${index}`, name: X_LONG, description: X_LONG, icon: 'certificate', color: '#ff00ff', isToggle: false, isActive: null };
}

// One of each, so the "not set up yet" message is on screen next to a camera
// that would play - the two tiles look the same until you tap them.
function xCamera(index: number): CameraSummary {
  return { id: `${X}-camera-${index}`, name: X_LONG, isConfigured: index % 2 === 1 };
}

// Two days, so the empty-day case and the populated one are both on screen.
// The dates have to stay real - CalendarSection parses them for its heading -
// which is the same exemption TIME_ZONE takes above.
function xCalendar(now: Date): CalendarDay[] {
  const date = (dayOffset: number) =>
    new Date(now.getTime() + dayOffset * 24 * HOUR_MS).toISOString().slice(0, 10);
  return [
    {
      date: date(0),
      events: [1, 2].map((i) => ({
        id: `${X}-cal-${i}`,
        calendarName: X_LONG,
        color: '#ff00ff',
        title: X_LONG,
        location: X_LONG,
        isAllDay: i === 1,
        startsAt: now.toISOString(),
        endsAt: new Date(now.getTime() + HOUR_MS).toISOString(),
      })),
    },
    { date: date(1), events: [] },
  ];
}

// One per kind, so a banner that hardcodes either one's wording gives itself
// away. Severity stays a real value - it is an enum on the wire, not free text,
// and an X would only prove the component falls back rather than that it reads
// the field.
function xAlerts(now: Date): HazardAlert[] {
  return [
    {
      id: `${X}-alert-1`,
      kind: 'Weather',
      severity: 'Extreme',
      title: X_LONG,
      detail: X_LONG,
      startsAt: now.toISOString(),
      endsAt: new Date(now.getTime() + HOUR_MS).toISOString(),
    },
    {
      id: `${X}-alert-2`,
      kind: 'AirQuality',
      severity: 'Moderate',
      title: X_LONG,
      detail: X_LONG,
      startsAt: null,
      endsAt: null,
    },
  ];
}

function xZone(index: number, now: Date): ZoneClimate {
  const { history, forecast } = timeWindow(now);
  return {
    id: `${X}-${index}`,
    name: X_LONG,
    currentTempF: NUM,
    comfortRange: xComfortRange(),
    history: xSeries(history),
    forecast: xSeries(forecast),
    low: xExtreme(history[0]),
    high: xExtreme(forecast[forecast.length - 1]),
  };
}

function xHourly(dates: Date[]): HourlyOutside[] {
  return dates.map((d) => ({
    time: d.toISOString(),
    humidityPct: NUM,
    cloudCoverPct: NUM,
    precipIn: NUM,
  }));
}

function xOutside(now: Date): OutsideClimate {
  const { history, forecast } = timeWindow(now);
  return {
    currentTempF: NUM,
    humidityPct: NUM,
    sunHoursRemaining: NUM,
    sunsetTime: now.toISOString(),
    precipitation: { amountIn: NUM, window: X_LONG },
    note: X_LONG,
    history: xSeries(history),
    forecast: xSeries(forecast),
    hourly: xHourly([...history, ...forecast]),
  };
}

// Real (not X/9999) so the theme still cycles sensibly when previewed against
// this source - see the comment on TIME_ZONE above for why timestamps are exempt.
function xSunEvents(now: Date): SunEvents {
  return {
    dawn: new Date(now.getTime() - 2 * HOUR_MS).toISOString(),
    sunrise: new Date(now.getTime() - HOUR_MS).toISOString(),
    sunset: new Date(now.getTime() + HOUR_MS).toISOString(),
    dusk: new Date(now.getTime() + 2 * HOUR_MS).toISOString(),
  };
}

/**
 * Every text field is all-X, every number is 9999, so any hardcoded label or
 * value still baked into the UI - rather than sourced from DashboardData -
 * stands out immediately when you switch to this source and run the app.
 */
export class TestDataSource implements DashboardDataSource {
  async getDashboardData(): Promise<DashboardData> {
    const now = new Date();
    return {
      generatedAt: now.toISOString(),
      timezone: TIME_ZONE,
      zones: [1, 2, 3].map((i) => xZone(i, now)),
      outside: xOutside(now),
      sunEvents: xSunEvents(now),
      routines: [1, 2].map((i) => xRoutine(i)),
      cameras: [1, 2].map((i) => xCamera(i)),
      calendar: xCalendar(now),
      alerts: xAlerts(now),
    };
  }
}
