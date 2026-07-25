import type {
  ComfortRange,
  DailyExtreme,
  DashboardData,
  DashboardDataSource,
  HourlyOutside,
  OutsideClimate,
  SunEvents,
  TempPoint,
  ZoneClimate,
} from '../types';
import { type DiurnalCurve, clamp, diurnalTempF, pseudoNoise } from './diurnal';
import { deriveZoneStatus } from '../lib/zonePresentation';
import { formatShortTime } from '../lib/format';
import { hourOfDayInZone, zonedWallClock } from '../lib/timezone';
import { DEFAULT_TIME_ZONE } from '../config';

const HOUR_MS = 60 * 60 * 1000;
const HISTORY_HOURS = 9;
const FORECAST_HOURS = 7;
const STEP_HOURS = 0.5;

const TIME_ZONE = DEFAULT_TIME_ZONE;

const ZONE_CURVES: Record<string, { name: string; curve: DiurnalCurve }> = {
  living_room: { name: 'Living Room', curve: { meanF: 70, amplitudeF: 4, peakHour: 17 } },
  bedroom: { name: 'Bedroom', curve: { meanF: 69.5, amplitudeF: 1, peakHour: 14 } },
  office: { name: 'Office', curve: { meanF: 67.5, amplitudeF: 1.5, peakHour: 12 } },
};

const INDOOR_COMFORT_RANGE: ComfortRange = { lowF: 68, highF: 71 };
const OUTSIDE_CURVE: DiurnalCurve = { meanF: 58, amplitudeF: 9, peakHour: 14 };

function buildSeries(now: Date, curve: DiurnalCurve): { history: TempPoint[]; forecast: TempPoint[] } {
  const history: TempPoint[] = [];
  for (let h = -HISTORY_HOURS; h <= 0; h += STEP_HOURS) {
    const time = new Date(now.getTime() + h * HOUR_MS);
    history.push({ time: time.toISOString(), tempF: round1(diurnalTempF(time, TIME_ZONE, curve)) });
  }

  const forecast: TempPoint[] = [];
  for (let h = 0; h <= FORECAST_HOURS; h += STEP_HOURS) {
    const time = new Date(now.getTime() + h * HOUR_MS);
    forecast.push({ time: time.toISOString(), tempF: round1(diurnalTempF(time, TIME_ZONE, curve)) });
  }

  return { history, forecast };
}

function extremesOf(points: TempPoint[]): { low: DailyExtreme; high: DailyExtreme } {
  const low = points.reduce((min, p) => (p.tempF < min.tempF ? p : min));
  const high = points.reduce((max, p) => (p.tempF > max.tempF ? p : max));
  return {
    low: { tempF: Math.round(low.tempF), time: low.time },
    high: { tempF: Math.round(high.tempF), time: high.time },
  };
}

function buildZone(id: string, now: Date): ZoneClimate {
  const { name, curve } = ZONE_CURVES[id];
  const { history, forecast } = buildSeries(now, curve);
  const { low, high } = extremesOf([...history, ...forecast]);
  return {
    id,
    name,
    currentTempF: round1(diurnalTempF(now, TIME_ZONE, curve)),
    comfortRange: INDOOR_COMFORT_RANGE,
    history,
    forecast,
    low,
    high,
  };
}

function buildHourlyOutside(now: Date): HourlyOutside[] {
  const hourly: HourlyOutside[] = [];
  for (let h = -HISTORY_HOURS; h <= FORECAST_HOURS; h += STEP_HOURS) {
    const time = new Date(now.getTime() + h * HOUR_MS);
    const hourOfDay = hourOfDayInZone(time, TIME_ZONE);
    const humidityPct = Math.round(
      clamp(55 + 20 * Math.sin((hourOfDay / 24) * 2 * Math.PI + 2) + 10 * pseudoNoise(h * 3.1), 30, 95),
    );
    const cloudCoverPct = Math.round(clamp(30 + 40 * pseudoNoise(h * 7.7), 0, 100));
    const rainRoll = pseudoNoise(h * 5.3);
    const precipIn = h >= 0 && rainRoll > 0.8 ? round2(rainRoll * 0.4) : 0;
    hourly.push({ time: time.toISOString(), humidityPct, cloudCoverPct, precipIn });
  }
  return hourly;
}

function derivePrecipitation(hourly: HourlyOutside[]): OutsideClimate['precipitation'] {
  const rainy = hourly.filter((h) => h.precipIn > 0 && new Date(h.time) >= new Date());
  const amountIn = round1(rainy.reduce((sum, h) => sum + h.precipIn, 0));
  if (rainy.length === 0) {
    return { amountIn: 0, window: '' };
  }
  const start = rainy[0].time;
  const end = rainy[rainy.length - 1].time;
  return {
    amountIn,
    window: start === end ? formatShortTime(start, TIME_ZONE) : `${formatShortTime(start, TIME_ZONE)}–${formatShortTime(end, TIME_ZONE)}`,
  };
}

/** Plausible dawn/sunrise/sunset/dusk around `now` - synthetic, not astronomically accurate. */
function deriveSunEvents(now: Date): SunEvents {
  return {
    dawn: zonedWallClock(now, TIME_ZONE, 6, 6).toISOString(),
    sunrise: zonedWallClock(now, TIME_ZONE, 6, 31).toISOString(),
    sunset: zonedWallClock(now, TIME_ZONE, 20, 31).toISOString(),
    dusk: zonedWallClock(now, TIME_ZONE, 20, 56).toISOString(),
  };
}

function deriveOutsideNote(zones: ZoneClimate[], outsideNow: number, outsideForecast: TempPoint[]): string {
  const warmest = [...zones].sort((a, b) => (b.currentTempF ?? -Infinity) - (a.currentTempF ?? -Infinity))[0];
  const later = outsideForecast[outsideForecast.length - 1]?.tempF ?? outsideNow;
  const trend = later < outsideNow ? 'cooling into the evening' : 'warming into the afternoon';
  const warmestNote = warmest && deriveZoneStatus(warmest.currentTempF, warmest.comfortRange) === 'warm'
    ? ` ${warmest.name} is running warmest right now.`
    : '';
  return `Outside ${trend}.${warmestNote}`;
}

function buildOutside(now: Date, zones: ZoneClimate[], sunEvents: SunEvents): OutsideClimate {
  const { history, forecast } = buildSeries(now, OUTSIDE_CURVE);
  const hourly = buildHourlyOutside(now);
  const currentTempF = round1(diurnalTempF(now, TIME_ZONE, OUTSIDE_CURVE));
  const sunset = new Date(sunEvents.sunset);
  const sunHoursRemaining = round1(clamp((sunset.getTime() - now.getTime()) / HOUR_MS, 0, 24));
  const currentHumidity = hourly.find((h) => new Date(h.time) >= now)?.humidityPct ?? hourly[hourly.length - 1].humidityPct;

  return {
    currentTempF,
    humidityPct: currentHumidity,
    sunHoursRemaining,
    sunsetTime: sunset.toISOString(),
    precipitation: derivePrecipitation(hourly),
    note: deriveOutsideNote(zones, currentTempF, forecast),
    history,
    forecast,
    hourly,
  };
}

export class MockDashboardDataSource implements DashboardDataSource {
  async getDashboardData(): Promise<DashboardData> {
    const now = new Date();
    const zones = Object.keys(ZONE_CURVES).map((id) => buildZone(id, now));
    const sunEvents = deriveSunEvents(now);
    return {
      generatedAt: now.toISOString(),
      timezone: TIME_ZONE,
      zones,
      outside: buildOutside(now, zones, sunEvents),
      sunEvents,
    };
  }
}

function round1(n: number): number {
  return Math.round(n * 10) / 10;
}

function round2(n: number): number {
  return Math.round(n * 100) / 100;
}
