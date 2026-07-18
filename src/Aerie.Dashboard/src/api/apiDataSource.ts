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
    if (!res.ok) {
      throw new Error(`Dashboard API request failed: ${res.status} ${res.statusText}`);
    }
    return (await res.json()) as DashboardData;
  }
}
