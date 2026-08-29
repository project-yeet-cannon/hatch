import { describe, expect, it } from 'vitest';
import { classifyReading, NO_DATA_AFTER_MS, readingAgeMs, STALE_AFTER_MS } from './staleness';

const GENERATED_AT = '2026-08-28T17:00:00.000Z';
const agoBy = (ms: number) => new Date(Date.parse(GENERATED_AT) - ms).toISOString();

describe('classifyReading', () => {
  it('is fresh for a reading that just landed', () => {
    expect(classifyReading(GENERATED_AT, GENERATED_AT)).toBe('fresh');
    expect(classifyReading(agoBy(60_000), GENERATED_AT)).toBe('fresh');
  });

  it('is fresh right up to the stale threshold', () => {
    expect(classifyReading(agoBy(STALE_AFTER_MS - 1), GENERATED_AT)).toBe('fresh');
  });

  it('is stale from the threshold, inclusive', () => {
    expect(classifyReading(agoBy(STALE_AFTER_MS), GENERATED_AT)).toBe('stale');
    expect(classifyReading(agoBy(NO_DATA_AFTER_MS - 1), GENERATED_AT)).toBe('stale');
  });

  it('is no data from twenty minutes, inclusive', () => {
    expect(classifyReading(agoBy(NO_DATA_AFTER_MS), GENERATED_AT)).toBe('none');
    expect(classifyReading(agoBy(9 * 60 * 60_000), GENERATED_AT)).toBe('none');
  });

  it('treats a reading it cannot date as no data, not as fresh', () => {
    // The server sends null both for "no reading" and for a value that came
    // from a history bucket. Neither can be dated, and calling an undateable
    // number fresh is the exact mistake currentAsOf exists to prevent.
    expect(classifyReading(null, GENERATED_AT)).toBe('none');
    expect(classifyReading(undefined, GENERATED_AT)).toBe('none');
    expect(classifyReading('', GENERATED_AT)).toBe('none');
  });

  it('treats an unparseable timestamp on either side as no data', () => {
    expect(classifyReading('not a date', GENERATED_AT)).toBe('none');
    expect(classifyReading(GENERATED_AT, 'not a date')).toBe('none');
  });

  it('does not gray a card out over clock skew', () => {
    // A reading stamped after the snapshot carrying it is skew between the HA
    // recorder and the API, not a fault.
    const future = new Date(Date.parse(GENERATED_AT) + 90_000).toISOString();
    expect(classifyReading(future, GENERATED_AT)).toBe('fresh');
  });

  it('compares against the snapshot, not the device clock', () => {
    // Both timestamps come from the same machine, so a tablet whose own clock
    // has drifted by hours still classifies correctly.
    const snapshot = '2020-01-01T00:00:00.000Z';
    const reading = '2020-01-01T00:00:30.000Z';
    expect(classifyReading(reading, snapshot)).toBe('fresh');
  });

  it('the two thresholds are ordered, or the stale band does not exist', () => {
    expect(STALE_AFTER_MS).toBeLessThan(NO_DATA_AFTER_MS);
  });
});

describe('readingAgeMs', () => {
  it('reports the age', () => {
    expect(readingAgeMs(agoBy(7 * 60_000), GENERATED_AT)).toBe(7 * 60_000);
  });

  it('is null when the reading cannot be dated', () => {
    expect(readingAgeMs(null, GENERATED_AT)).toBeNull();
    expect(readingAgeMs('nope', GENERATED_AT)).toBeNull();
  });

  it('clamps skew to zero rather than reporting a negative age', () => {
    const future = new Date(Date.parse(GENERATED_AT) + 5_000).toISOString();
    expect(readingAgeMs(future, GENERATED_AT)).toBe(0);
  });
});
