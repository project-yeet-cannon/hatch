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

/**
 * A Routine as shown on the kiosk - just enough to render a tap-to-trigger
 * tile. isActive is null for a momentary routine, and for a toggle routine
 * (isToggle) reflects whether every SetPower action's channel currently
 * reads "on".
 */
export interface RoutineSummary {
  id: string;
  name: string;
  description: string | null;
  icon: string | null;
  color: string | null;
  isToggle: boolean;
  isActive: boolean | null;
}

/**
 * One camera as the kiosk's button row renders it - a name for the tile and an
 * id to open the stream with (docs/camera-devices-architecture.md).
 *
 * isConfigured is whether the camera has an address to stream from yet. The
 * relay answers a camera without one with a 409, but a failed WebSocket
 * handshake reaches the browser with no status code on it, so the modal cannot
 * tell "not set up yet" from "unavailable" on its own - it is told, here, for
 * free.
 */
export interface CameraSummary {
  id: string;
  name: string;
  isConfigured: boolean;
}

/**
 * One cached calendar event as the kiosk agenda renders it. `color` is the
 * calendar's admin override when there is one and the provider's own color
 * otherwise - the client can't tell which it got, and shouldn't care.
 */
export interface CalendarEventSummary {
  id: string;
  calendarName: string;
  color: string | null;
  title: string;
  location: string | null;
  isAllDay: boolean;
  /** ISO 8601 timestamp. For an all-day event, the day's bounds in the house's timezone. */
  startsAt: string;
  endsAt: string;
}

/**
 * One local day of the agenda window. Days with no events are still present,
 * so "nothing tomorrow" is renderable and distinguishable from a day that
 * never synced. A multi-day event appears on each day it covers.
 */
export interface CalendarDay {
  /** Local calendar date, "YYYY-MM-DD". */
  date: string;
  events: CalendarEventSummary[];
}

/** Which half of the outdoor hazard feature produced an alert. */
export type HazardKind = 'Weather' | 'AirQuality';

/**
 * How loud to be about a hazard. One vocabulary across both halves - a weather
 * alert reports its issuing office's severity, and bad air is mapped onto the
 * same words - so the kiosk styles severity once rather than per kind.
 */
export type HazardSeverity = 'Unknown' | 'Minor' | 'Moderate' | 'Severe' | 'Extreme';

/**
 * One thing outside worth saying out loud: a watch, warning, or advisory as
 * issued, or the single synthetic alert bad air produces. The list is empty on
 * a calm, clean-air day, which is most days.
 */
export interface HazardAlert {
  /** Stable within a snapshot, for keying a list. */
  id: string;
  kind: HazardKind;
  severity: HazardSeverity;
  /** What is being warned about, e.g. "Winter Storm Warning" or an air quality band. */
  title: string;
  /** A sentence of detail, when there is one. */
  detail: string | null;
  /** ISO 8601. When it takes effect, or when a forecast peak arrives; null reads as "already in effect". */
  startsAt: string | null;
  /** ISO 8601. When it stops applying; null when open-ended. */
  endsAt: string | null;
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
  cameras: CameraSummary[];
  calendar: CalendarDay[];
  alerts: HazardAlert[];
}

/** Anything that can produce a dashboard snapshot — mock today, a live API later. */
export interface DashboardDataSource {
  getDashboardData(): Promise<DashboardData>;
}

/**
 * Gather — the family's shared lists, as the kiosk consumes them. These mirror
 * the module's DTOs (src/Aerie.Api/Modules/Gather/Dtos.cs) field for field; the
 * API answers in camelCase with ISO timestamps, so nothing is transformed on
 * the way in.
 */
export interface GatherListSummary {
  id: string;
  name: string;
  icon: string | null;
  color: string | null;
  /** Still to get. This is the number the wall tile shows. */
  openCount: number;
  /** Already in the cart — what "Clear checked" would sweep. */
  checkedCount: number;
  /** ISO 8601. */
  createdAt: string;
  /** ISO 8601. Moves on item activity too, not just a rename. */
  updatedAt: string;
}

export interface GatherItem {
  id: string;
  listId: string;
  name: string;
  /** Free text — "a dozen", "2 lbs", "x2" are all real answers. */
  quantity: string | null;
  note: string | null;
  isChecked: boolean;
  /** ISO 8601, or null while the item is open. */
  checkedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

/**
 * A list and everything on it. Items arrive in display order — unchecked first
 * by age, then checked most-recently-first — decided server-side so the wall
 * and the phone can't drift apart on it.
 */
export interface GatherListDetail {
  list: GatherListSummary;
  items: GatherItem[];
}

/**
 * The slice of Gather the wall needs: read the lists, open one, add, check,
 * sweep. Deliberately not the whole module API — renaming an item and editing a
 * quantity are phone jobs, and leaving them out is what keeps every control in
 * the overlay above the keyboard.
 */
export interface GatherSource {
  getLists(): Promise<GatherListSummary[]>;
  getList(id: string): Promise<GatherListDetail>;
  /** Upserts on the normalized name: re-adding a checked item brings it back open. */
  addItem(listId: string, name: string): Promise<GatherItem>;
  setChecked(listId: string, itemId: string, isChecked: boolean): Promise<GatherItem>;
  /** Resolves to how many items were removed. */
  clearChecked(listId: string): Promise<number>;
}
