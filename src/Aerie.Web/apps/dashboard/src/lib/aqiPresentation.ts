import type { AirQualityBand, OutsideAirQuality } from '../types';

/**
 * When an AQI reading stops being current enough to show at full strength.
 * Two hours: the provider refreshes hourly, so one missed refresh is normal
 * jitter and two is a reading worth trusting less. The pill mutes rather than
 * vanishes - the same "trust this less, don't ignore this" the climate bars
 * use for stale.
 */
export const AQI_STALE_AFTER_MS = 2 * 60 * 60 * 1000;

export interface AqiPresentation {
  /** The pill's text, e.g. "AQI 42 · Good". */
  label: string;
  /**
   * The hue the pill mixes into --card. Good takes the circadian comfort
   * accent - clean air is furniture, and furniture follows the hour. The
   * other bands take the hazard ladder's fixed hexes for the AlertBanner's
   * reason: a warning that shifted hue with the phase would be reporting the
   * time of day rather than the air. Luminance still follows the phase either
   * way, because the color only ever appears mixed into --card.
   */
  color: string;
  /** True past [AQI_STALE_AFTER_MS], or when the reading can't be dated. */
  stale: boolean;
}

/**
 * Short display words, not AirQualityBands.NameOf's full EPA phrasing - the
 * pill is furniture on a stats row, and "Unhealthy for Sensitive Groups" is a
 * sentence. The full phrasing still appears where it matters: the bad-air
 * HazardAlert's title, which the server writes.
 */
const BAND_WORDS: Record<AirQualityBand, string> = {
  Good: 'Good',
  Moderate: 'Moderate',
  UnhealthyForSensitiveGroups: 'Sensitive groups',
  Unhealthy: 'Unhealthy',
  VeryUnhealthy: 'Very unhealthy',
  Hazardous: 'Hazardous',
};

/** The hazard ladder's hexes, shared with AlertBanner's severityColor by value - see the note there. */
const BAND_COLOR: Record<AirQualityBand, string> = {
  Good: 'var(--comfort)',
  Moderate: '#d9a441',
  UnhealthyForSensitiveGroups: '#e07a3f',
  Unhealthy: '#d14343',
  VeryUnhealthy: '#d14343',
  Hazardous: '#d14343',
};

/**
 * @param nowOnServerClock the same advanced server-clock "now" the climate
 * bars measure against (App.tsx) - both timestamps in the age comparison come
 * from the server's clock, so a wrong device clock can't mute the pill.
 */
export function deriveAqiPresentation(
  airQuality: OutsideAirQuality,
  nowOnServerClock: string,
): AqiPresentation {
  const asOf = Date.parse(airQuality.asOf);
  const now = Date.parse(nowOnServerClock);
  // An undateable reading is trusted less, not hidden - the number is still
  // the best answer anyone has, which is the climate bars' rule too.
  const stale = Number.isNaN(asOf) || Number.isNaN(now) || now - asOf >= AQI_STALE_AFTER_MS;
  return {
    label: `AQI ${airQuality.usAqi} · ${BAND_WORDS[airQuality.band]}`,
    color: BAND_COLOR[airQuality.band],
    stale,
  };
}
