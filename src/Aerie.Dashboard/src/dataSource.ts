import type { DashboardDataSource } from './types';
import { MockDashboardDataSource } from './mock/mockDataSource';
import { TestDataSource } from './mock/testDataSource';

/**
 * Single place the rest of the app asks for a data source. Swapping the mock
 * for a real Aerie.Api-backed implementation later is a one-line change here.
 *
 * Visit with ?source=test to switch to TestDataSource, which returns all-X
 * text and all-9999 numbers - anything else on the page is hardcoded, not
 * data-driven.
 */
export function getDashboardDataSource(): DashboardDataSource {
  const params = new URLSearchParams(window.location.search);
  if (params.get('source') === 'test') {
    return new TestDataSource();
  }
  return new MockDashboardDataSource();
}
