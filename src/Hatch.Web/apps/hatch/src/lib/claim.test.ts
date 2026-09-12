import { describe, expect, it } from 'vitest';
import { QUIET_FRACTION, agoPhrase, claimHealth, claimTitle } from './claim';
import type { IssueClaim } from '../types';

const NOW = new Date('2026-09-07T08:00:00Z');

const at = (msFromNow: number) => new Date(NOW.getTime() + msFromNow).toISOString();

const TTL = 300;

const claim = (over: Partial<IssueClaim> = {}): IssueClaim => ({
  claimedBy: 'hatch',
  runner: 'somewhere:/checkouts/one',
  claimedAt: at(-60_000),
  heartbeatAt: at(0),
  chatter: null,
  chatterAt: null,
  ttlSeconds: TTL,
  ...over,
});

const quietAfterMs = TTL * 1000 * QUIET_FRACTION;

describe('claimHealth', () => {
  it('is fresh on a heartbeat that just landed', () => {
    expect(claimHealth(claim(), NOW)).toBe('fresh');
  });

  it('is fresh exactly at the threshold and quiet one second past it', () => {
    expect(claimHealth(claim({ heartbeatAt: at(-quietAfterMs) }), NOW)).toBe('fresh');
    expect(claimHealth(claim({ heartbeatAt: at(-quietAfterMs - 1000) }), NOW)).toBe('quiet');
  });

  /* The regression the TTL on the wire exists for. Without it a client would
     have to hardcode a count of minutes, and the same silence would have to
     read the same way under both leases - which is exactly the judgement a
     lease length is supposed to make. */
  it('reads the same silence differently under a different lease', () => {
    const silent = at(-4 * 60_000);

    expect(claimHealth(claim({ heartbeatAt: silent, ttlSeconds: 60 * 60 }), NOW)).toBe('fresh');
    expect(claimHealth(claim({ heartbeatAt: silent, ttlSeconds: 300 }), NOW)).toBe('quiet');
  });

  it('is fresh on a heartbeat from a clock a few seconds ahead of this one', () => {
    expect(claimHealth(claim({ heartbeatAt: at(5_000) }), NOW)).toBe('fresh');
  });

  it('is quiet on a heartbeat it cannot read, rather than passing it as fine', () => {
    expect(claimHealth(claim({ heartbeatAt: 'not an instant' }), NOW)).toBe('quiet');
  });
});

describe('agoPhrase', () => {
  it('is just now under ten seconds', () => {
    expect(agoPhrase(at(0), NOW)).toBe('just now');
    expect(agoPhrase(at(-9_000), NOW)).toBe('just now');
  });

  it('counts seconds up to a minute', () => {
    expect(agoPhrase(at(-10_000), NOW)).toBe('10 seconds ago');
    expect(agoPhrase(at(-40_000), NOW)).toBe('40 seconds ago');
    expect(agoPhrase(at(-59_000), NOW)).toBe('59 seconds ago');
  });

  it('counts minutes, singular at one', () => {
    expect(agoPhrase(at(-60_000), NOW)).toBe('1 minute ago');
    expect(agoPhrase(at(-4 * 60_000), NOW)).toBe('4 minutes ago');
    expect(agoPhrase(at(-59 * 60_000), NOW)).toBe('59 minutes ago');
  });

  it('counts hours, singular at one', () => {
    expect(agoPhrase(at(-60 * 60_000), NOW)).toBe('1 hour ago');
    expect(agoPhrase(at(-3 * 60 * 60_000), NOW)).toBe('3 hours ago');
  });

  it('counts days, singular at one', () => {
    expect(agoPhrase(at(-24 * 60 * 60_000), NOW)).toBe('1 day ago');
    expect(agoPhrase(at(-3 * 24 * 60 * 60_000), NOW)).toBe('3 days ago');
  });

  it('clamps a future instant to just now rather than counting backwards', () => {
    expect(agoPhrase(at(30_000), NOW)).toBe('just now');
  });

  it('says never where there is no instant at all', () => {
    expect(agoPhrase(null, NOW)).toBe('never');
    expect(agoPhrase(undefined, NOW)).toBe('never');
    expect(agoPhrase('not an instant', NOW)).toBe('never');
  });
});

describe('claimTitle', () => {
  it('names the holder, the runner and how long ago it was last heard from', () => {
    const said = claimTitle(claim({ heartbeatAt: at(-4 * 60_000) }), NOW);

    expect(said).toContain('hatch');
    expect(said).toContain('somewhere:/checkouts/one');
    expect(said).toContain('4 minutes ago');
  });
});
