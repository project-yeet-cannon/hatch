import type { ZoneClimate } from '../types';

/**
 * How many zones lead when the admin hasn't pinned any. Two, because Phase 0
 * measured the house at two zones - the fallback renders the designed wall on
 * day one - and because two tabs plus Outside is comfortable at the narrow end
 * of the column.
 */
export const DEFAULT_LEAD_COUNT = 2;

export interface ZonePartition {
  /** The climate card's tabs, in snapshot (admin sort) order. */
  leads: ZoneClimate[];
  /** Everything else - the "More rooms" section, same order. */
  rest: ZoneClimate[];
}

/**
 * Splits the snapshot's zones into the climate card's tabs and the rest.
 *
 * Pinned zones lead when there are any; with none pinned - a fresh install, or
 * an API that predates the flag - the first [DEFAULT_LEAD_COUNT] by sort order
 * lead instead, so the card never renders empty-tabbed while zones exist.
 * Order is preserved on both sides: the snapshot arrives in the admin's sort
 * order and this function has no opinion of its own about it.
 */
export function partitionZones(zones: ZoneClimate[]): ZonePartition {
  const pinned = zones.filter((zone) => zone.pinned);
  if (pinned.length > 0) {
    return { leads: pinned, rest: zones.filter((zone) => !zone.pinned) };
  }
  return { leads: zones.slice(0, DEFAULT_LEAD_COUNT), rest: zones.slice(DEFAULT_LEAD_COUNT) };
}
