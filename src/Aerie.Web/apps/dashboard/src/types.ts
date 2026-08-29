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
  /**
   * ISO 8601. When the reading behind `currentTempF` was actually taken.
   *
   * Null is a different statement from "no reading", and the distinction is the
   * whole point of the field: the server returns null both when there is no
   * reading and when the value fell back to a history bucket, because a bucket
   * boundary is not a moment anything was measured. Either way the client knows
   * only that it cannot date this number, which lib/staleness.ts reads as "no
   * data" rather than as "fresh".
   *
   * Without it, `currentTempF` is the newest sample anywhere in a nine-hour
   * window and a sensor that died at noon still reads confidently at 5pm.
   */
  currentAsOf: string | null;
  comfortRange: ComfortRange;
  /** Actual readings, oldest first, ending at "now". */
  history: TempPoint[];
  /** Projected readings, starting at "now". */
  forecast: TempPoint[];
  low: DailyExtreme | null;
  high: DailyExtreme | null;
  /**
   * The admin's "lead on the wall" flag: pinned zones take the climate card's
   * tabs, the rest render under "More rooms". False when the API predates the
   * flag (absent reads as false via lib/snapshotDefaults.ts); when *no* zone
   * is pinned, the client leads with the first two by sort order so the card
   * never renders empty-tabbed - see lib/leadZones.ts.
   */
  pinned: boolean;
}

export interface HourlyOutside {
  time: string;
  humidityPct: number;
  cloudCoverPct: number;
  precipIn: number;
}

/**
 * The condition vocabulary for a day's outlook - deliberately small, one FA
 * glyph each. The API's half maps WMO weather codes onto these words
 * (docs/plans/dashboard-redesign.md, the follow-up section); the client never
 * sees a code.
 */
export type OutlookCondition =
  | 'clear'
  | 'partlyCloudy'
  | 'cloudy'
  | 'fog'
  | 'drizzle'
  | 'rain'
  | 'snow'
  | 'storm';

/** One day's shape, for the outside pane's Today/Tomorrow cells. */
export interface DayOutlook {
  highF: number;
  lowF: number;
  condition: OutlookCondition;
}

/**
 * The six US AQI bands, mirroring AirQualityBands.cs
 * (src/Aerie.Api/Services/Hazards) name for name - the enum is on the wire,
 * and the pill's color is keyed by it.
 */
export type AirQualityBand =
  | 'Good'
  | 'Moderate'
  | 'UnhealthyForSensitiveGroups'
  | 'Unhealthy'
  | 'VeryUnhealthy'
  | 'Hazardous';

/**
 * The always-on outdoor air quality reading - a different statement from the
 * bad-air HazardAlert, which stays the loud path. This one is quiet furniture
 * on the outside pane.
 */
export interface OutsideAirQuality {
  usAqi: number;
  band: AirQualityBand;
  /**
   * ISO 8601. When the index was measured. The pill mutes past two hours and
   * trusts this field for it, not its own fetch time - the same "the server
   * dates the reading" rule currentAsOf follows.
   */
  asOf: string;
}

export interface OutsideClimate {
  /** Null when there's no reading yet - distinct from a real 0°F. */
  currentTempF: number | null;
  /** ISO 8601. Same contract as `ZoneClimate.currentAsOf`, including what null means. */
  currentAsOf: string | null;
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
  /**
   * Null until the follow-up forecast provider lands (and null after it when
   * the provider is unreachable) - the outlook cells render nothing on null,
   * never a placeholder. The API omitting the field entirely reads as null
   * via lib/snapshotDefaults.ts, so the client and the API need no lockstep
   * deploy.
   */
  todayOutlook: DayOutlook | null;
  /** Same contract as todayOutlook. */
  tomorrowOutlook: DayOutlook | null;
  /** Null when no air quality provider is configured or its reading went stale server-side. Same absent-means-null rule as the outlooks. */
  airQuality: OutsideAirQuality | null;
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

/**
 * A Panel as the kiosk's tile row renders it: id, name, icon, color, and
 * nothing else. Deliberately not the items - the dashboard snapshot is a 60s
 * poll carrying every panel whether or not anyone opened one, and live control
 * state is both too stale at that interval and too expensive to gather
 * unconditionally. The overlay fetches its own (see PanelState).
 *
 * Mirrors Aerie.Api's PanelSummary (src/Aerie.Api/Models/Panels/Dtos.cs).
 */
export interface PanelSummary {
  id: string;
  name: string;
  icon: string | null;
  color: string | null;
}

/** Which of the two things a panel item is. Mirrors Ef.PanelItemKind. */
export type PanelItemKind = 'Routine' | 'Control';

/** The control surfaces the kiosk knows how to draw. Mirrors Ef.ControlKind - append-only, and Light/Camera are a later append rather than a redesign. */
export type ControlKind = 'Switch' | 'Thermostat';

/**
 * Live state for one item in an open panel, mirroring PanelItemStateDto. One
 * shape for both kinds, because the overlay renders one ordered list: the
 * control fields are null on a routine item and the routine fields are null on
 * a control.
 *
 * The three null-vs-false distinctions the server is careful about, and the
 * kiosk has to preserve:
 *  - isOn is null both when a control has no on/off at all and when it has one
 *    that has never reported. A confident "Off" for a device nobody has heard
 *    from is worse than saying nothing.
 *  - isActive is false, not null, for a toggle routine with no samples - that
 *    is what the dashboard tile beside it reads, and the two must agree.
 *  - minF/maxF/stepF arrive resolved against the server's defaults, so the
 *    client-side clamp and the server-side one work from the same numbers.
 */
export interface PanelItemState {
  /** The *item* id - what the panel's power/setpoint endpoints address. */
  id: string;
  kind: PanelItemKind;
  label: string | null;
  icon: string | null;
  color: string | null;
  controlKind: ControlKind | null;
  isOn: boolean | null;
  setpointF: number | null;
  ambientF: number | null;
  /** The HVAC mode channel's raw state ("cool", "off"), when one is bound. */
  mode: string | null;
  minF: number | null;
  maxF: number | null;
  stepF: number | null;
  /** Null on a control. On a routine item, what /api/routines/{id}/trigger takes. */
  routineId: string | null;
  isToggle: boolean | null;
  isActive: boolean | null;
}

export interface PanelState {
  id: string;
  name: string;
  items: PanelItemState[];
}

/**
 * The slice of Panels an open overlay needs: read the state, and the two writes
 * a control can make. Routine items inside a panel deliberately aren't here -
 * they go out through the same /api/routines calls the dashboard tiles use, so
 * there is exactly one trigger path in the app.
 *
 * Same seam as GatherSource, for the same reason: ?source= has to switch the
 * whole screen, the overlay included.
 */
export interface PanelSource {
  getState(panelId: string): Promise<PanelState>;
  /** Absolute, not a toggle - a stale client is then wrong about what it displays, never about what it sends. */
  setPower(panelId: string, itemId: string, on: boolean): Promise<void>;
  /** Clamped and snapped to the control's step server-side; the kiosk clamps too so a disabled button is visible rather than a silent refusal. */
  setSetpoint(panelId: string, itemId: string, valueF: number): Promise<void>;
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
  panels: PanelSummary[];
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

/**
 * Photos — the family library on the wall, as the kiosk consumes it. Mirrors
 * Modules/Photos/Dtos.cs field for field.
 *
 * Note what is absent: an Immich host, a key, a URL of any kind. The kiosk asks
 * Aerie for a manifest of asset ids and then for those ids' bytes, both on the
 * origin it is already signed in to, so the photo frame needs no second auth
 * and the library's credential never leaves the API
 * (docs/plans/immich.md v+2).
 */
export interface CarouselPhoto {
  assetId: string;
  albumName: string;
  /** ISO 8601, or null for a photo the library knows no date for - a scan, usually. */
  takenAt: string | null;
  city: string | null;
  country: string | null;
}

export interface PhotoCarousel {
  /** A shuffled sample, not the whole selection. Server-side shuffle, per request, so two tablets aren't on the same photo. */
  photos: CarouselPhoto[];
  /** How many photos the selection holds in all, of which photos is a sample. */
  totalPhotos: number;
  generatedAt: string;
  /** Set when the library could not be refreshed. Photos may still be populated from the last good fetch - the wall keeps drawing. */
  error: string | null;
}

/**
 * The slice of Photos the wall needs: a manifest, and where to get one photo's
 * bytes. Same seam as GatherSource and PanelSource, for the same reason -
 * ?source= has to switch the whole screen.
 *
 * imageUrl is on the source rather than being a shared helper because it is the
 * half that differs: the API source points at Aerie's proxy, and the mock draws
 * its own pictures without a server in the picture at all.
 */
export interface PhotoSource {
  getCarousel(count?: number): Promise<PhotoCarousel>;
  imageUrl(assetId: string): string;
}
