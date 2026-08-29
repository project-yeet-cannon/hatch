import type { ComfortStatus, TempPoint, ZoneClimate } from '../types';
import { formatShortTime } from './format';
import { classifyReading, type ReadingFreshness } from './staleness';

export interface ZonePresentation {
  status: ComfortStatus;
  /** Short word shown collapsed, e.g. "warming" / "steady" / "cool". */
  summaryLabel: string;
  /** Short phrase shown expanded, e.g. "running cool". */
  bodyBadgeLabel: string;
  /** Explanatory note shown expanded, e.g. "1° below comfort since noon". */
  statusNote: string;
  /** How much the card is allowed to stand behind its own number. */
  freshness: ReadingFreshness;
  /**
   * When the reading was taken, e.g. "as of 3:42 PM" - shown only while stale.
   * Null when fresh (nobody needs to be told a live number is live) and null at
   * 'none' (there is nothing to date, which is what 'none' means).
   */
  asOfLabel: string | null;
}

export function deriveZoneStatus(
  currentTempF: number | null,
  comfortRange: ZoneClimate['comfortRange'],
): ComfortStatus {
  if (currentTempF === null) return 'unknown';
  if (currentTempF > comfortRange.highF) return 'warm';
  if (currentTempF < comfortRange.lowF) return 'cool';
  return 'comfortable';
}

/**
 * @param nowOnServerClock now, expressed on the server's clock - what the
 * reading's age is measured against. See lib/staleness.ts for why not the
 * device clock, and App.tsx for why it is advanced rather than the snapshot's
 * `generatedAt` verbatim.
 */
export function deriveZonePresentation(
  zone: ZoneClimate,
  timeZone: string,
  nowOnServerClock: string,
): ZonePresentation {
  const freshness = classifyReading(zone.currentAsOf, nowOnServerClock);

  // One guard for both ways of having nothing to say. A zone that never
  // reported and a zone that stopped reporting twenty minutes ago are the same
  // card - the second one just took longer to get there - and giving them one
  // code path is what stops the two from drifting into different-looking states
  // that mean the same thing.
  if (zone.currentTempF === null || freshness === 'none') {
    return {
      status: 'unknown',
      summaryLabel: 'no data',
      bodyBadgeLabel: 'no data',
      statusNote: zone.currentTempF === null ? 'waiting for a reading' : 'nothing reported in the last 20 minutes',
      freshness: 'none',
      asOfLabel: null,
    };
  }

  const asOfLabel =
    freshness === 'stale' && zone.currentAsOf !== null
      ? `as of ${formatShortTime(zone.currentAsOf, timeZone)}`
      : null;

  const currentTempF = zone.currentTempF;
  const status = deriveZoneStatus(currentTempF, zone.comfortRange);

  if (status === 'warm') {
    const peak = peakOf(zone.forecast.slice(1));
    const stillRising = peak && peak.tempF > currentTempF + 0.3;
    return {
      status,
      summaryLabel: 'warming',
      bodyBadgeLabel: 'warming',
      statusNote: stillRising
        ? `trending up, ≈${Math.round(peak!.tempF)}° by ${formatShortTime(peak!.time, timeZone)}`
        : 'past today’s high, cooling from here',
      freshness,
      asOfLabel,
    };
  }

  if (status === 'cool') {
    const since = firstBelow(zone.history, zone.comfortRange.lowF);
    const delta = Math.round(zone.comfortRange.lowF - currentTempF);
    return {
      status,
      summaryLabel: 'cool',
      bodyBadgeLabel: 'running cool',
      statusNote: since
        ? `${delta}° below comfort since ${formatShortTime(since.time, timeZone)}`
        : `${delta}° below comfort`,
      freshness,
      asOfLabel,
    };
  }

  const upcoming = zone.forecast.map((p) => p.tempF);
  const min = Math.round(Math.min(...upcoming, currentTempF));
  const max = Math.round(Math.max(...upcoming, currentTempF));
  return {
    status,
    summaryLabel: 'steady',
    bodyBadgeLabel: 'comfortable',
    statusNote: min === max ? `holding ${min}° through the evening` : `holding ${min}–${max}° through the evening`,
    freshness,
    asOfLabel,
  };
}

function peakOf(points: TempPoint[]): TempPoint | undefined {
  return points.reduce<TempPoint | undefined>(
    (best, p) => (!best || p.tempF > best.tempF ? p : best),
    undefined,
  );
}

function firstBelow(points: TempPoint[], thresholdF: number): TempPoint | undefined {
  return points.find((p) => p.tempF < thresholdF);
}
