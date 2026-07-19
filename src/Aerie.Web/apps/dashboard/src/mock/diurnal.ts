/** Small helpers for generating plausible-looking temperature/weather curves. */

import { hourOfDayInZone } from '../lib/timezone';

export interface DiurnalCurve {
  meanF: number;
  amplitudeF: number;
  /** Hour of day (0-24, in the house's timezone) the curve peaks at. */
  peakHour: number;
}

/**
 * Smooth day/night temperature cycle sampled at an arbitrary point in time.
 * Hour-of-day is resolved in `timeZone`, not the machine's local time - the
 * house's diurnal cycle shouldn't shift depending on where this code runs.
 */
export function diurnalTempF(date: Date, timeZone: string, curve: DiurnalCurve): number {
  const hour = hourOfDayInZone(date, timeZone);
  return (
    curve.meanF +
    curve.amplitudeF * Math.cos((2 * Math.PI * (hour - curve.peakHour)) / 24)
  );
}

/** Deterministic pseudo-random value in [0, 1) for a given seed. */
export function pseudoNoise(seed: number): number {
  const x = Math.sin(seed * 12.9898) * 43758.5453;
  return x - Math.floor(x);
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
