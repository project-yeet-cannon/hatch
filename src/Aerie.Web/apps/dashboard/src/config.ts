/**
 * The house's default timezone. Used by the mock data source and by UI bits
 * (like the day/night theme) that need "now" before real data has loaded.
 * A real Aerie.Api-backed source will supply `timezone` on DashboardData
 * itself; this is just the fallback/mock default.
 */
export const DEFAULT_TIME_ZONE = 'America/New_York';
