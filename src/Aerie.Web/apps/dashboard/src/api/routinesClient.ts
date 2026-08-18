/**
 * The dashboard's first outbound write call - everything else in this app is
 * read-only polling (see dataSource.ts). Kept separate from a
 * DashboardDataSource implementation since triggering isn't part of the
 * snapshot contract.
 */
export async function triggerRoutine(id: string): Promise<void> {
  const res = await fetch(`/api/routines/${id}/trigger`, { method: 'POST' });
  if (!res.ok) {
    throw new Error(`Trigger routine failed: ${res.status} ${res.statusText}`);
  }
}

/** The "off" half of a toggle routine - see RoutineSummary.isToggle. */
export async function turnOffRoutine(id: string): Promise<void> {
  const res = await fetch(`/api/routines/${id}/turn-off`, { method: 'POST' });
  if (!res.ok) {
    throw new Error(`Turn off routine failed: ${res.status} ${res.statusText}`);
  }
}
