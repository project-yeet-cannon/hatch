import { describe, expect, it } from 'vitest';
import { DEFAULT_LEAD_COUNT, partitionZones } from './leadZones';
import type { ZoneClimate } from '../types';

function zone(id: string, pinned: boolean): ZoneClimate {
  return {
    id,
    name: id,
    currentTempF: 70,
    currentAsOf: '2026-08-28T17:00:00.000Z',
    comfortRange: { lowF: 68, highF: 72 },
    history: [],
    forecast: [],
    low: null,
    high: null,
    pinned,
  };
}

const ids = (zones: ZoneClimate[]) => zones.map((z) => z.id);

describe('partitionZones', () => {
  it('leads with the pinned zones and keeps snapshot order on both sides', () => {
    const { leads, rest } = partitionZones([zone('a', false), zone('b', true), zone('c', true), zone('d', false)]);
    expect(ids(leads)).toEqual(['b', 'c']);
    expect(ids(rest)).toEqual(['a', 'd']);
  });

  it('leads with one zone when only one is pinned - the pin is the statement, not the count', () => {
    const { leads, rest } = partitionZones([zone('a', false), zone('b', true), zone('c', false)]);
    expect(ids(leads)).toEqual(['b']);
    expect(ids(rest)).toEqual(['a', 'c']);
  });

  it('falls back to the first zones by sort order when none are pinned', () => {
    const { leads, rest } = partitionZones([zone('a', false), zone('b', false), zone('c', false)]);
    expect(ids(leads)).toEqual(['a', 'b']);
    expect(leads).toHaveLength(DEFAULT_LEAD_COUNT);
    expect(ids(rest)).toEqual(['c']);
  });

  it('handles fewer zones than the fallback count', () => {
    const { leads, rest } = partitionZones([zone('a', false)]);
    expect(ids(leads)).toEqual(['a']);
    expect(rest).toEqual([]);
  });

  it('handles no zones at all', () => {
    const { leads, rest } = partitionZones([]);
    expect(leads).toEqual([]);
    expect(rest).toEqual([]);
  });

  it('respects every pin even past a comfortable tab count - the admin holds the taste knob', () => {
    const zones = ['a', 'b', 'c', 'd', 'e'].map((id) => zone(id, true));
    const { leads, rest } = partitionZones(zones);
    expect(leads).toHaveLength(5);
    expect(rest).toEqual([]);
  });
});
