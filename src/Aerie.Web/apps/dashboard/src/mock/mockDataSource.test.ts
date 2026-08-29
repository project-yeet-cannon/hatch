import { describe, expect, it } from 'vitest';
import { bandOf } from './mockDataSource';

/**
 * The mock plays the server for the AQI pill, so its index-to-band mapping
 * owes AirQualityBands.cs (src/Aerie.Api/Services/Hazards) an exact mirror -
 * inclusive lower bounds, band changes at 51/101/151/201/301. A drift here
 * would have the mock previewing a pill color the real wall never shows.
 */
describe('bandOf', () => {
  it('matches the EPA boundaries the server uses, inclusively', () => {
    expect(bandOf(0)).toBe('Good');
    expect(bandOf(50)).toBe('Good');
    expect(bandOf(51)).toBe('Moderate');
    expect(bandOf(100)).toBe('Moderate');
    expect(bandOf(101)).toBe('UnhealthyForSensitiveGroups');
    expect(bandOf(150)).toBe('UnhealthyForSensitiveGroups');
    expect(bandOf(151)).toBe('Unhealthy');
    expect(bandOf(200)).toBe('Unhealthy');
    expect(bandOf(201)).toBe('VeryUnhealthy');
    expect(bandOf(300)).toBe('VeryUnhealthy');
    expect(bandOf(301)).toBe('Hazardous');
    expect(bandOf(500)).toBe('Hazardous');
  });
});
