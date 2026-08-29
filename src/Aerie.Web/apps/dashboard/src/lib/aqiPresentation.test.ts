import { describe, expect, it } from 'vitest';
import { AQI_STALE_AFTER_MS, deriveAqiPresentation } from './aqiPresentation';
import type { AirQualityBand, OutsideAirQuality } from '../types';

const NOW = '2026-08-28T17:00:00.000Z';
const agoBy = (ms: number) => new Date(Date.parse(NOW) - ms).toISOString();

function reading(band: AirQualityBand, usAqi: number, asOf = NOW): OutsideAirQuality {
  return { usAqi, band, asOf };
}

describe('deriveAqiPresentation', () => {
  it('labels with the index and the band word', () => {
    expect(deriveAqiPresentation(reading('Good', 42), NOW).label).toBe('AQI 42 · Good');
    expect(deriveAqiPresentation(reading('UnhealthyForSensitiveGroups', 118), NOW).label).toBe(
      'AQI 118 · Sensitive groups',
    );
  });

  it('gives clean air the circadian accent and bad air the hazard ladder', () => {
    expect(deriveAqiPresentation(reading('Good', 30), NOW).color).toBe('var(--comfort)');
    expect(deriveAqiPresentation(reading('Moderate', 80), NOW).color).toBe('#d9a441');
    expect(deriveAqiPresentation(reading('UnhealthyForSensitiveGroups', 120), NOW).color).toBe('#e07a3f');
    // The top three bands share the ladder's red - past Unhealthy the
    // distinction that matters is in the words, not another hue.
    for (const band of ['Unhealthy', 'VeryUnhealthy', 'Hazardous'] as const) {
      expect(deriveAqiPresentation(reading(band, 250), NOW).color).toBe('#d14343');
    }
  });

  it('is fresh up to the threshold and stale from it, inclusive', () => {
    expect(deriveAqiPresentation(reading('Good', 42, agoBy(AQI_STALE_AFTER_MS - 1)), NOW).stale).toBe(false);
    expect(deriveAqiPresentation(reading('Good', 42, agoBy(AQI_STALE_AFTER_MS)), NOW).stale).toBe(true);
  });

  it('treats an undateable reading as stale, not hidden', () => {
    expect(deriveAqiPresentation(reading('Good', 42, 'not a date'), NOW).stale).toBe(true);
    expect(deriveAqiPresentation(reading('Good', 42), 'not a date').stale).toBe(true);
  });
});
