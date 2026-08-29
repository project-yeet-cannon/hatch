import type { DashboardData } from '../types';

/**
 * Fills the contract fields the live API may not send yet.
 *
 * The redesign's client contract grew three outside fields and a zone flag
 * ahead of the API (docs/plans/dashboard-redesign.md, "The mock-first data
 * contract") - deliberately, so the API's half can land whenever it lands with
 * no lockstep deploy. Until it does, the wire shape is missing those fields
 * entirely, and this is the one place that turns "absent" into the contract's
 * null/false. Every consumer downstream gets to trust the types.
 *
 * Written against the raw parsed JSON rather than DashboardData so the
 * "before the API caught up" shape is representable without lying casts at
 * the call site.
 */
export function withSnapshotDefaults(raw: unknown): DashboardData {
  const data = raw as DashboardData;
  return {
    ...data,
    zones: data.zones.map((zone) => ({ ...zone, pinned: zone.pinned ?? false })),
    outside: {
      ...data.outside,
      todayOutlook: data.outside.todayOutlook ?? null,
      tomorrowOutlook: data.outside.tomorrowOutlook ?? null,
      airQuality: data.outside.airQuality ?? null,
    },
  };
}
