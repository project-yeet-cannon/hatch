import type { ComfortStatus, TempPoint, ZoneClimate } from '../types';
import { formatShortTime } from './format';

export interface ZonePresentation {
  status: ComfortStatus;
  /** Short word shown collapsed, e.g. "warming" / "steady" / "cool". */
  summaryLabel: string;
  /** Short phrase shown expanded, e.g. "running cool". */
  bodyBadgeLabel: string;
  /** Explanatory note shown expanded, e.g. "1° below comfort since noon". */
  statusNote: string;
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

export function deriveZonePresentation(zone: ZoneClimate, timeZone: string): ZonePresentation {
  if (zone.currentTempF === null) {
    return {
      status: 'unknown',
      summaryLabel: 'no data',
      bodyBadgeLabel: 'no data',
      statusNote: 'waiting for a reading',
    };
  }

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
