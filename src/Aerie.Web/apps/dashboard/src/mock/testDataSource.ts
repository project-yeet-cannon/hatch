import type {
  ComfortRange,
  DailyExtreme,
  DashboardData,
  DashboardDataSource,
  HourlyOutside,
  OutsideClimate,
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
    };
  }
}
