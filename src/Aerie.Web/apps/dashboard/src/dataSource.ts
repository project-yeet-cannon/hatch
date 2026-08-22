import type { DashboardDataSource, GatherSource } from './types';
import { ApiDashboardDataSource } from './api/apiDataSource';
import { ApiGatherSource } from './api/gatherClient';
import { MockDashboardDataSource } from './mock/mockDataSource';
import { MockGatherSource } from './mock/mockGatherSource';
import { TestDataSource } from './mock/testDataSource';
import { TestGatherSource } from './mock/testGatherSource';

/**
 * Single place the rest of the app asks for a data source. Defaults to the real
 * Aerie.Api endpoint; override with a `?source=` query param for development:
 *
 *   (default)      -> ApiDashboardDataSource   — live data from GET /api/dashboard
 *   ?source=mock   -> MockDashboardDataSource   — synthetic but realistic data
 *   ?source=test   -> TestDataSource            — all-X text / all-9999 numbers,
 *                                                 to spot any hardcoded UI values
 *
 * Gather (getGatherSource) reads the same param, so one URL switches the whole
 * screen — the tile and the overlay included — rather than leaving half the
 * page live against the API while the other half is synthetic.
 */

type SourceKind = 'mock' | 'test' | 'api';

function selectedSource(): SourceKind {
  const params = new URLSearchParams(window.location.search);
  switch (params.get('source')) {
    case 'mock':
      return 'mock';
    case 'test':
      return 'test';
    default:
      return 'api';
  }
}

export function getDashboardDataSource(): DashboardDataSource {
  switch (selectedSource()) {
    case 'mock':
      return new MockDashboardDataSource();
    case 'test':
      return new TestDataSource();
    default:
      return new ApiDashboardDataSource();
  }
}

/** Gather's lists, for the wall tile and the overlay. See src/api/gatherClient.ts. */
export function getGatherSource(): GatherSource {
  switch (selectedSource()) {
    case 'mock':
      return new MockGatherSource();
    case 'test':
      return new TestGatherSource();
    default:
      return new ApiGatherSource();
  }
}
