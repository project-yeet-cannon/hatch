import { describe, expect, it } from 'vitest';
import { createSkewWatcher, SKEW_REPORT_THRESHOLD_MS, SKEW_RESTATE_DELTA_MS } from './clockSkew';

const SERVER_NOW = '2026-08-28T17:00:00.000Z';
const serverMs = Date.parse(SERVER_NOW);

describe('createSkewWatcher', () => {
  it('says nothing while the clocks agree', () => {
    const watcher = createSkewWatcher();
    expect(watcher.note(serverMs, SERVER_NOW)).toBeNull();
    expect(watcher.note(serverMs + 800, SERVER_NOW)).toBeNull();
  });

  it('ignores ordinary request latency', () => {
    const watcher = createSkewWatcher();
    expect(watcher.note(serverMs + SKEW_REPORT_THRESHOLD_MS - 1, SERVER_NOW)).toBeNull();
  });

  it('reports a device clock that is ahead, signed', () => {
    const watcher = createSkewWatcher();
    expect(watcher.note(serverMs + 10 * 60_000, SERVER_NOW)).toBe(10 * 60_000);
  });

  it('reports a device clock that is behind', () => {
    const watcher = createSkewWatcher();
    expect(watcher.note(serverMs - 10 * 60_000, SERVER_NOW)).toBe(-10 * 60_000);
  });

  it('says it once, not on every poll', () => {
    // A tablet an hour out would otherwise fill a 64-slot buffer with one fact.
    const watcher = createSkewWatcher();
    expect(watcher.note(serverMs + 60 * 60_000, SERVER_NOW)).not.toBeNull();
    for (let i = 0; i < 60; i++) {
      expect(watcher.note(serverMs + 60 * 60_000 + i * 100, SERVER_NOW)).toBeNull();
    }
  });

  it('says it again when the skew moves materially', () => {
    const watcher = createSkewWatcher();
    watcher.note(serverMs + 5 * 60_000, SERVER_NOW);
    expect(watcher.note(serverMs + 5 * 60_000 + SKEW_RESTATE_DELTA_MS, SERVER_NOW)).not.toBeNull();
  });

  it('re-arms once the clocks agree again', () => {
    // An NTP correction lands, then the clock drifts out a second time. The
    // second drift has to be reportable or the watcher only ever fires once.
    const watcher = createSkewWatcher();
    expect(watcher.note(serverMs + 5 * 60_000, SERVER_NOW)).not.toBeNull();
    expect(watcher.note(serverMs, SERVER_NOW)).toBeNull();
    expect(watcher.note(serverMs + 5 * 60_000, SERVER_NOW)).not.toBeNull();
  });

  it('says nothing about an unparseable server timestamp', () => {
    const watcher = createSkewWatcher();
    expect(watcher.note(serverMs, 'not a date')).toBeNull();
  });
});
