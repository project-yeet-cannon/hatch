import { handledUnauthorized } from '../lib/signIn';
import type { DashboardData, DashboardDataSource } from '../types';

/**
 * The real data source: fetches a DashboardData snapshot from Aerie.Api's
 * `GET /api/dashboard`. The API returns exactly the contract shape (camelCase,
 * ISO timestamps), so no transformation is needed here.
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
    return (await res.json()) as DashboardData;
  }
}
