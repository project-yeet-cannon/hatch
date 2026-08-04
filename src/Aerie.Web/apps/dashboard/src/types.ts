/**
 * Data contract for the home dashboard. Everything the page renders comes
 * from a `DashboardData` object — the UI has no knowledge of where it came
 * from (a mock generator today, a real Aerie.Api endpoint later). Only raw
 * physical quantities live here; derived labels (badge text, status words,
 * comfort classification) are computed from this data by the presentation
 * layer so they can't drift out of sync with the numbers.
 */

export type ComfortStatus = 'warm' | 'cool' | 'comfortable' | 'unknown';

/** A single temperature sample. */
export interface TempPoint {
  /** ISO 8601 timestamp. */
  time: string;
  tempF: number;
}

export interface ComfortRange {
  lowF: number;
  highF: number;
}

export interface DailyExtreme {
  tempF: number;
  /** ISO 8601 timestamp of when the extreme occurred, or is forecast to. */
  time: string;
}

export interface ZoneClimate {
  /** Stable identifier, e.g. a Home Assistant area id. */
  id: string;
  name: string;
  /** Null when there's no reading yet - distinct from a real 0°F. */
  currentTempF: number | null;
  comfortRange: ComfortRange;
  /** Actual readings, oldest first, ending at "now". */
  history: TempPoint[];
  /** Projected readings, starting at "now". */
  forecast: TempPoint[];
  low: DailyExtreme | null;
  high: DailyExtreme | null;
}

export interface HourlyOutside {
  time: string;
  humidityPct: number;
  cloudCoverPct: number;
  precipIn: number;
}

export interface OutsideClimate {
  /** Null when there's no reading yet - distinct from a real 0°F. */
  currentTempF: number | null;
  humidityPct: number | null;
  sunHoursRemaining: number;
  /** ISO 8601 timestamp. */
  sunsetTime: string;
  precipitation: {
    amountIn: number;
    /** Human-readable window, e.g. "4–6pm"; empty when none is forecast. */
    window: string;
  };
  /** Freeform summary, as you'd get from a weather provider's text forecast. */
  note: string;
  history: TempPoint[];
  forecast: TempPoint[];
  hourly: HourlyOutside[];
}

/**
 * The four sun events bounding the dashboard's circadian theme phases: full
 * light (sunrise-sunset), evening transition (sunset-dusk), full dark
 * (dusk-dawn), morning transition (dawn-sunrise). ISO 8601 timestamps.
 */
export interface SunEvents {
  dawn: string;
  sunrise: string;
  sunset: string;
  dusk: string;
}

/** A Routine as shown on the kiosk - just enough to render a tap-to-trigger button. */
export interface RoutineSummary {
  id: string;
  name: string;
  description: string | null;
}

export interface DashboardData {
  /** ISO 8601 timestamp of when this snapshot was produced. */
  generatedAt: string;
  /** IANA timezone the data should be displayed in, e.g. "America/Denver". */
  timezone: string;
  zones: ZoneClimate[];
  outside: OutsideClimate;
  sunEvents: SunEvents;
  routines: RoutineSummary[];
}

/** Anything that can produce a dashboard snapshot — mock today, a live API later. */
export interface DashboardDataSource {
  getDashboardData(): Promise<DashboardData>;
}
