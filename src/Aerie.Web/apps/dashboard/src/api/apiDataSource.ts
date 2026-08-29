import { handledUnauthorized } from '../lib/signIn';
import { withSnapshotDefaults } from '../lib/snapshotDefaults';
import type { DashboardData, DashboardDataSource } from '../types';

/**
 * The real data source: fetches a DashboardData snapshot from Aerie.Api's
 * `GET /api/dashboard`. The API returns the contract shape (camelCase, ISO
 * timestamps); the only massaging is the absent-field defaults for contract
 * fields the API doesn't send yet.
 */
export class ApiDashboardDataSource implements DashboardDataSource {
  private readonly endpoint: string;

  constructor(endpoint = '/api/dashboard') {
    this.endpoint = endpoint;
  }

  async getDashboardData(): Promise<DashboardData> {
    const res = await fetch(this.endpoint, { headers: { Accept: 'application/json' } });
    // The kiosk's whole failure mode: without this the tablet renders its last
    // good snapshot forever and never asks anyone to sign in.
    if (handledUnauthorized(res)) return await new Promise<DashboardData>(() => {});
    if (!res.ok) {
      throw new Error(`Dashboard API request failed: ${res.status} ${res.statusText}`);
    }
    // The one seam where "the API hasn't caught up to the contract yet" is
    // resolved - absent fields become the contract's null/false so nothing
    // downstream has to re-ask. See lib/snapshotDefaults.ts.
    return withSnapshotDefaults(await res.json());
  }
}
