import type { DashboardDataSource } from './types';
import { ApiDashboardDataSource } from './api/apiDataSource';
import { MockDashboardDataSource } from './mock/mockDataSource';
import { TestDataSource } from './mock/testDataSource';

/**
 * Single place the rest of the app asks for a data source. Defaults to the real
 * Aerie.Api endpoint; override with a `?source=` query param for development:
 *
 *   (default)      -> ApiDashboardDataSource   — live data from GET /api/dashboard
 *   ?source=mock   -> MockDashboardDataSource   — synthetic but realistic data
 *   ?source=test   -> TestDataSource            — all-X text / all-9999 numbers,
 *                                                 to spot any hardcoded UI values
 */
export function getDashboardDataSource(): DashboardDataSource {
  const params = new URLSearchParams(window.location.search);
  switch (params.get('source')) {
    case 'mock':
      return new MockDashboardDataSource();
    case 'test':
      return new TestDataSource();
    default:
      return new ApiDashboardDataSource();
  }
}
