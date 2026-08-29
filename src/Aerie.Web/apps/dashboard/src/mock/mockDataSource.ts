import type {
  CalendarDay,
  CalendarEventSummary,
  CameraSummary,
  ComfortRange,
  DailyExtreme,
  DashboardData,
  DashboardDataSource,
  DayOutlook,
  HazardAlert,
  HourlyOutside,
  OutsideAirQuality,
  OutsideClimate,
  RoutineSummary,
  SunEvents,
  TempPoint,
  ZoneClimate,
} from '../types';
import { type DiurnalCurve, clamp, diurnalTempF, pseudoNoise } from './diurnal';
import { deriveZoneStatus } from '../lib/zonePresentation';
import { formatShortTime } from '../lib/format';
import { calendarDateInZone, hourOfDayInZone, zonedWallClock } from '../lib/timezone';
import { DEFAULT_TIME_ZONE } from '../config';
import { MOCK_PANELS } from './mockPanelSource';

const HOUR_MS = 60 * 60 * 1000;
const HISTORY_HOURS = 9;
const FORECAST_HOURS = 7;
const STEP_HOURS = 0.5;

const TIME_ZONE = DEFAULT_TIME_ZONE;

/**
 * `staleMinutes` is how long ago this zone last reported, and the first three
 * values are deliberately one per band of lib/staleness.ts - fresh, stale, and
 * past the point where the number is worth showing at all. Same reasoning as
 * the unconfigured camera below: a state that can only be reached by unplugging
 * a sensor is a state nobody will look at twice, and it will look wrong the
 * first time it happens for real.
 *
 * `pinned` splits two-and-two so both halves of the lead-zone partition are on
 * screen against this source: two tabs on the climate card, two rows under
 * "More rooms". The stale zone is one of the pinned ones on purpose - the tab
 * treatment of a dimmed reading has to be visible without unplugging anything.
 */
const ZONE_CURVES: Record<string, { name: string; curve: DiurnalCurve; staleMinutes: number; pinned: boolean }> = {
  living_room: { name: 'Living Room', curve: { meanF: 70, amplitudeF: 4, peakHour: 17 }, staleMinutes: 1, pinned: true },
  bedroom: { name: 'Bedroom', curve: { meanF: 69.5, amplitudeF: 1, peakHour: 14 }, staleMinutes: 14, pinned: true },
  office: { name: 'Office', curve: { meanF: 67.5, amplitudeF: 1.5, peakHour: 12 }, staleMinutes: 45, pinned: false },
  sunroom: { name: 'Sunroom', curve: { meanF: 73, amplitudeF: 6, peakHour: 15 }, staleMinutes: 3, pinned: false },
};

const INDOOR_COMFORT_RANGE: ComfortRange = { lowF: 68, highF: 71 };
const OUTSIDE_CURVE: DiurnalCurve = { meanF: 58, amplitudeF: 9, peakHour: 14 };

/**
 * Two cameras, one of them without an address, because the unconfigured tile is
 * a state the wall will spend time in - a camera is imported from discovery
 * before anyone fills in the admin form - and it should be visible in the mock
 * rather than only in production.
 */
const CAMERAS: CameraSummary[] = [
  { id: 'front-door', name: 'Front door', isConfigured: true },
  { id: 'driveway', name: 'Driveway', isConfigured: false },
];

const ROUTINES: RoutineSummary[] = [
  {
    id: 'night-mode',
    name: 'Night mode',
    description: 'Basement night lights + bedroom white noise',
    icon: 'moon',
    color: '#5c6ac4',
    isToggle: false,
    isActive: null,
  },
  {
    id: 'max-ac',
    name: 'Max AC',
    description: 'Radiators off, AC down, fans on',
    icon: 'snowflake',
    color: '#3ba3d6',
    isToggle: false,
    isActive: null,
  },
  {
    id: 'outdoor-floodlights',
    name: 'Outdoor floodlights',
    description: 'Front + back floodlights',
    icon: 'lightbulb',
    color: '#f0b429',
    isToggle: true,
    isActive: false,
  },
];

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
  const { name, curve, staleMinutes, pinned } = ZONE_CURVES[id];
  const { history, forecast } = buildSeries(now, curve);
  const { low, high } = extremesOf([...history, ...forecast]);
  return {
    id,
    name,
    currentTempF: round1(diurnalTempF(now, TIME_ZONE, curve)),
    currentAsOf: new Date(now.getTime() - staleMinutes * 60_000).toISOString(),
    comfortRange: INDOOR_COMFORT_RANGE,
    history,
    forecast,
    low,
    high,
    pinned,
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

/**
 * The mock's variant knobs, so every state of the new outside fields is
 * reachable from a URL rather than by editing this file
 * (docs/plans/dashboard-redesign.md, "The mock-first data contract"):
 *
 *   ?source=mock&mock-aqi=118      any US AQI index; the band is derived
 *   ?source=mock&mock-aqi=stale    a reading three hours old (the pill mutes)
 *   ?source=mock&mock-aqi=none     no reading (the pill is absent)
 *   ?source=mock&mock-outlook=none no outlooks (the cells are absent)
 */
function mockKnob(name: string): string | null {
  return new URLSearchParams(window.location.search).get(name);
}

/** AirQualityBands.Of, mirrored - the mock plays the server here, so it owes the server's mapping. Exported for the boundary test alone. */
export function bandOf(usAqi: number): OutsideAirQuality['band'] {
  if (usAqi <= 50) return 'Good';
  if (usAqi <= 100) return 'Moderate';
  if (usAqi <= 150) return 'UnhealthyForSensitiveGroups';
  if (usAqi <= 200) return 'Unhealthy';
  if (usAqi <= 300) return 'VeryUnhealthy';
  return 'Hazardous';
}

function buildAirQuality(now: Date): OutsideAirQuality | null {
  const knob = mockKnob('mock-aqi');
  if (knob === 'none') return null;
  const usAqi = knob !== null && /^\d+$/.test(knob) ? Number(knob) : 42;
  const asOf = knob === 'stale' ? new Date(now.getTime() - 3 * HOUR_MS) : new Date(now.getTime() - 20 * 60_000);
  return { usAqi, band: bandOf(usAqi), asOf: asOf.toISOString() };
}

/**
 * Today from the outside curve's own extremes, tomorrow a couple of degrees
 * warmer with a different condition - so the two cells never render as twins
 * and both glyph paths are exercised. Rain in the hourly series wins today's
 * condition, matching what a provider's daily code would do.
 */
function buildOutlooks(
  outside: { history: TempPoint[]; forecast: TempPoint[] },
  hourly: HourlyOutside[],
): { todayOutlook: DayOutlook | null; tomorrowOutlook: DayOutlook | null } {
  if (mockKnob('mock-outlook') === 'none') return { todayOutlook: null, tomorrowOutlook: null };
  const { low, high } = extremesOf([...outside.history, ...outside.forecast]);
  const raining = hourly.some((h) => h.precipIn > 0);
  return {
    todayOutlook: { highF: high.tempF, lowF: low.tempF, condition: raining ? 'rain' : 'partlyCloudy' },
    tomorrowOutlook: { highF: high.tempF + 3, lowF: low.tempF + 2, condition: 'clear' },
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

/**
 * Today and tomorrow, the window CalendarAgendaDays defaults to. Hours past 24
 * roll into the next day, which is exactly how the API's second CalendarDay
 * gets populated - and tomorrow deliberately holds one all-day event and one
 * timed one so the ordering rule is visible against this source.
 */
function buildCalendar(now: Date): CalendarDay[] {
  const at = (hoursFromMidnight: number, minute = 0) => zonedWallClock(now, TIME_ZONE, hoursFromMidnight, minute);
  const dateOf = (dayOffset: number) =>
    calendarDateInZone(new Date(now.getTime() + dayOffset * 24 * HOUR_MS), TIME_ZONE);

  const event = (
    id: string,
    calendarName: string,
    color: string,
    title: string,
    location: string | null,
    startHour: number,
    endHour: number,
    isAllDay = false,
    endMinute = 0,
  ): CalendarEventSummary => ({
    id,
    calendarName,
    color,
    title,
    location,
    isAllDay,
    startsAt: at(startHour).toISOString(),
    endsAt: at(endHour, endMinute).toISOString(),
  });

  return [
    {
      date: dateOf(0),
      events: [
        event('cal-1', 'Family', '#7c9c6c', 'Trash out', null, 0, 24, true),
        event('cal-2', 'Family', '#7c9c6c', 'Dentist', '1200 Market St', 9, 10),
        event('cal-3', 'Work', '#5c6ac4', 'Design review', 'Zoom', 14, 15),
      ],
    },
    {
      date: dateOf(1),
      events: [
        event('cal-4', 'Family', '#7c9c6c', 'Anna out of town', null, 24, 48, true),
        event('cal-5', 'Family', '#7c9c6c', 'Soccer practice', 'Riverside Park', 41, 42, false, 30),
      ],
    },
  ];
}

/**
 * One of each kind, so the banner's severity treatment and its two-kind layout
 * are both visible against this source. The weather alert is in effect now and
 * the air quality one is still climbing, which is the pair of shapes the
 * component has to render differently.
 */
function buildAlerts(now: Date): HazardAlert[] {
  return [
    {
      id: 'mock-alert-1',
      kind: 'Weather',
      severity: 'Severe',
      title: 'Winter Storm Warning',
      detail: 'Winter Storm Warning issued for the metro area until 6 PM.',
      startsAt: new Date(now.getTime() - 2 * HOUR_MS).toISOString(),
      endsAt: new Date(now.getTime() + 6 * HOUR_MS).toISOString(),
    },
    {
      id: 'air-quality',
      kind: 'AirQuality',
      severity: 'Moderate',
      title: 'Unhealthy for Sensitive Groups',
      detail: 'US AQI 118 now, rising to 143',
      startsAt: new Date(now.getTime() + 4 * HOUR_MS).toISOString(),
      endsAt: null,
    },
  ];
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
    // Outside is always fresh in the mock: the interior zones already cover
    // the staleness ladder, and a permanently stale outside card would just
    // be noise behind every other thing the mock is for.
    currentAsOf: now.toISOString(),
    humidityPct: currentHumidity,
    sunHoursRemaining,
    sunsetTime: sunset.toISOString(),
    precipitation: derivePrecipitation(hourly),
    note: deriveOutsideNote(zones, currentTempF, forecast),
    history,
    forecast,
    hourly,
    ...buildOutlooks({ history, forecast }, hourly),
    airQuality: buildAirQuality(now),
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
      routines: ROUTINES,
      cameras: CAMERAS,
      panels: MOCK_PANELS,
      calendar: buildCalendar(now),
      alerts: buildAlerts(now),
    };
  }
}

function round1(n: number): number {
  return Math.round(n * 10) / 10;
}

function round2(n: number): number {
  return Math.round(n * 100) / 100;
}
