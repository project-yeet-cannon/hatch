import { describe, expect, it } from 'vitest';
import { withSnapshotDefaults } from './snapshotDefaults';
import type { DashboardData, DayOutlook, OutsideAirQuality } from '../types';

/**
 * A wire-shaped snapshot from an API that predates the redesign contract:
 * no `pinned` on zones, none of the three new outside fields. Built as plain
 * data rather than cast from DashboardData so the absence is real, not typed
 * away.
 */
function preContractSnapshot(): unknown {
  return {
    generatedAt: '2026-08-28T17:00:00.000Z',
    timezone: 'UTC',
    zones: [
      { id: 'a', name: 'A' },
      { id: 'b', name: 'B' },
    ],
    outside: { note: '' },
    sunEvents: {},
    routines: [],
    cameras: [],
    panels: [],
    calendar: [],
    alerts: [],
  };
}

describe('withSnapshotDefaults', () => {
  it('reads an absent pinned flag as false on every zone', () => {
    const data = withSnapshotDefaults(preContractSnapshot());
    expect(data.zones.map((z) => z.pinned)).toEqual([false, false]);
  });

  it('reads the absent outside fields as null', () => {
    const data = withSnapshotDefaults(preContractSnapshot());
    expect(data.outside.todayOutlook).toBeNull();
    expect(data.outside.tomorrowOutlook).toBeNull();
    expect(data.outside.airQuality).toBeNull();
  });

  it('passes present values through untouched', () => {
    const outlook: DayOutlook = { highF: 78, lowF: 61, condition: 'partlyCloudy' };
    const airQuality: OutsideAirQuality = { usAqi: 42, band: 'Good', asOf: '2026-08-28T16:30:00.000Z' };
    const raw = preContractSnapshot() as DashboardData;
    raw.zones[1] = { ...raw.zones[1], pinned: true };
    raw.outside = { ...raw.outside, todayOutlook: outlook, airQuality };

    const data = withSnapshotDefaults(raw);
    expect(data.zones.map((z) => z.pinned)).toEqual([false, true]);
    expect(data.outside.todayOutlook).toEqual(outlook);
    // The one still-absent field defaults; its siblings don't drag it along.
    expect(data.outside.tomorrowOutlook).toBeNull();
    expect(data.outside.airQuality).toEqual(airQuality);
  });

  it('leaves everything outside its remit alone', () => {
    const raw = preContractSnapshot();
    const data = withSnapshotDefaults(raw);
    expect(data.generatedAt).toBe('2026-08-28T17:00:00.000Z');
    expect(data.zones[0].id).toBe('a');
    expect(data.outside.note).toBe('');
  });
});
