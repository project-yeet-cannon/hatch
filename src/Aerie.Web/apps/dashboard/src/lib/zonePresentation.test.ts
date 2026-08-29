import { describe, expect, it } from 'vitest';
import { deriveZonePresentation } from './zonePresentation';
import { NO_DATA_AFTER_MS, STALE_AFTER_MS } from './staleness';
import type { ZoneClimate } from '../types';

const GENERATED_AT = '2026-08-28T17:00:00.000Z';
const TIME_ZONE = 'America/New_York';
const agoBy = (ms: number) => new Date(Date.parse(GENERATED_AT) - ms).toISOString();

function zone(overrides: Partial<ZoneClimate> = {}): ZoneClimate {
  return {
    id: 'living_room',
    name: 'Living Room',
    currentTempF: 70,
    currentAsOf: agoBy(60_000),
    comfortRange: { lowF: 68, highF: 72 },
    history: [{ time: agoBy(30 * 60_000), tempF: 69 }],
    forecast: [{ time: GENERATED_AT, tempF: 70 }],
    low: null,
    high: null,
    ...overrides,
  };
}

describe('deriveZonePresentation freshness', () => {
  it('says nothing about age while the reading is fresh', () => {
    const presentation = deriveZonePresentation(zone(), TIME_ZONE, GENERATED_AT);
    expect(presentation.freshness).toBe('fresh');
    expect(presentation.asOfLabel).toBeNull();
    expect(presentation.summaryLabel).toBe('steady');
  });

  it('keeps the number and dates it once stale', () => {
    // Still the best answer anyone has, so the status and the labels are
    // unchanged - only the confidence in them moves.
    const presentation = deriveZonePresentation(
      zone({ currentAsOf: agoBy(STALE_AFTER_MS + 60_000) }),
      TIME_ZONE,
      GENERATED_AT,
    );
    expect(presentation.freshness).toBe('stale');
    expect(presentation.summaryLabel).toBe('steady');
    expect(presentation.asOfLabel).toMatch(/^as of /);
  });

  it('falls to no data past twenty minutes, whatever the temperature says', () => {
    const presentation = deriveZonePresentation(
      zone({ currentTempF: 70, currentAsOf: agoBy(NO_DATA_AFTER_MS + 60_000) }),
      TIME_ZONE,
      GENERATED_AT,
    );
    expect(presentation.freshness).toBe('none');
    expect(presentation.status).toBe('unknown');
    expect(presentation.summaryLabel).toBe('no data');
    expect(presentation.statusNote).toBe('nothing reported in the last 20 minutes');
    // Nothing to date, which is what 'none' means.
    expect(presentation.asOfLabel).toBeNull();
  });

  it('distinguishes never-reported from stopped-reporting in the note, not the state', () => {
    // Same card, different sentence: one zone is waiting for its first reading
    // and the other lost the one it had, and the wall should not pretend those
    // are the same story while rendering them the same way.
    const never = deriveZonePresentation(
      zone({ currentTempF: null, currentAsOf: null }),
      TIME_ZONE,
      GENERATED_AT,
    );
    const stopped = deriveZonePresentation(
      zone({ currentAsOf: agoBy(NO_DATA_AFTER_MS) }),
      TIME_ZONE,
      GENERATED_AT,
    );

    expect(never.freshness).toBe('none');
    expect(stopped.freshness).toBe('none');
    expect(never.status).toBe(stopped.status);
    expect(never.summaryLabel).toBe(stopped.summaryLabel);
    expect(never.statusNote).toBe('waiting for a reading');
    expect(stopped.statusNote).toBe('nothing reported in the last 20 minutes');
  });

  it('treats a reading the server could not date as no data', () => {
    // currentAsOf is null when the value fell back to a history bucket. The
    // number exists; its age does not, and an undateable number is not fresh.
    const presentation = deriveZonePresentation(zone({ currentAsOf: null }), TIME_ZONE, GENERATED_AT);
    expect(presentation.freshness).toBe('none');
    expect(presentation.summaryLabel).toBe('no data');
  });

  it('carries freshness through every comfort branch, not just the steady one', () => {
    const asOf = agoBy(STALE_AFTER_MS + 1);
    const warm = deriveZonePresentation(zone({ currentTempF: 80, currentAsOf: asOf }), TIME_ZONE, GENERATED_AT);
    const cool = deriveZonePresentation(zone({ currentTempF: 60, currentAsOf: asOf }), TIME_ZONE, GENERATED_AT);

    expect(warm.summaryLabel).toBe('warming');
    expect(warm.freshness).toBe('stale');
    expect(warm.asOfLabel).not.toBeNull();

    expect(cool.summaryLabel).toBe('cool');
    expect(cool.freshness).toBe('stale');
    expect(cool.asOfLabel).not.toBeNull();
  });
});
