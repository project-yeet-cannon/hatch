/**
 * How old a reading is allowed to be before the wall stops standing behind it.
 *
 * The two thresholds are one statement, not two, which is why they live
 * together: `stale` is only meaningful as "past the point a working sensor
 * would have reported, but still worth showing", and `none` only as "past the
 * point the number is worth showing at all". Moving one without the other
 * leaves a band that means nothing.
 *
 * Ten minutes rather than five: SampleChannels polls every minute, but it reads
 * Home Assistant's recorder, so the cadence that actually matters is the
 * *device's* - a thermostat reporting every few minutes is healthy, and a
 * five-minute floor would flag it as broken most of the day.
 *
 * Measured against the snapshot's own `generatedAt` rather than the device
 * clock. Both timestamps then come from the same machine, so a tablet whose
 * clock has drifted - which is a thing that happens to a device that has been
 * offline for a while - cannot make a fresh house look dead or the reverse.
 */
export const STALE_AFTER_MS = 10 * 60_000;
export const NO_DATA_AFTER_MS = 20 * 60_000;

export type ReadingFreshness = 'fresh' | 'stale' | 'none';

/**
 * @param asOf ISO 8601 timestamp of the reading, or null.
 * @param generatedAt ISO 8601 timestamp the snapshot was produced at.
 *
 * A null `asOf` is 'none' rather than 'fresh': the server sends null both when
 * there is no reading and when the value came from a history bucket, and in
 * neither case can this client date the number in front of it (see
 * ZoneClimate.currentAsOf). Treating "I cannot tell you how old this is" as
 * fresh is the exact mistake the field exists to prevent.
 */
export function classifyReading(asOf: string | null | undefined, generatedAt: string): ReadingFreshness {
  if (!asOf) return 'none';

  const readingAt = Date.parse(asOf);
  const snapshotAt = Date.parse(generatedAt);
  if (Number.isNaN(readingAt) || Number.isNaN(snapshotAt)) return 'none';

  // Negative age means the reading is stamped after the snapshot that carries
  // it - clock skew between an HA recorder and the API, not a fault, and
  // certainly not something to gray a card out over.
  const ageMs = Math.max(0, snapshotAt - readingAt);
  if (ageMs >= NO_DATA_AFTER_MS) return 'none';
  if (ageMs >= STALE_AFTER_MS) return 'stale';
  return 'fresh';
}

/** How old the reading is, in ms, or null when it cannot be dated. For the "as of" affordance. */
export function readingAgeMs(asOf: string | null | undefined, generatedAt: string): number | null {
  if (!asOf) return null;
  const readingAt = Date.parse(asOf);
  const snapshotAt = Date.parse(generatedAt);
  if (Number.isNaN(readingAt) || Number.isNaN(snapshotAt)) return null;
  return Math.max(0, snapshotAt - readingAt);
}
