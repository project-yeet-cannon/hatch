import { describe, expect, it } from 'vitest';
import {
  activityWords,
  boundsChanged,
  boundsOf,
  boundsProblem,
  boundsRequest,
  isControllable,
  runnerActivity,
} from './runners';
import type { Runner } from '../types';

const NOW = new Date('2026-09-08T12:00:00Z');

const at = (msFromNow: number) => new Date(NOW.getTime() + msFromNow).toISOString();

const GONE_AFTER = 90;

const runner = (over: Partial<Runner> = {}): Runner => ({
  name: 'here:/checkouts/one',
  kind: 'loop',
  firstSeenAt: at(-3_600_000),
  lastSeenAt: at(0),
  claimKey: null,
  line: null,
  lineAt: null,
  state: 'running',
  under: null,
  maxRuns: null,
  maxSpend: null,
  untilAt: null,
  goneAfterSeconds: GONE_AFTER,
  ...over,
});

describe('runnerActivity', () => {
  it('is idle between increments', () => {
    expect(runnerActivity(runner(), NOW)).toBe('idle');
  });

  it('is working while it holds a ticket', () => {
    expect(runnerActivity(runner({ claimKey: 'AER-12' }), NOW)).toBe('working');
  });

  it('is here exactly at the horizon and gone one second past it', () => {
    expect(runnerActivity(runner({ lastSeenAt: at(-GONE_AFTER * 1000) }), NOW)).toBe('idle');
    expect(runnerActivity(runner({ lastSeenAt: at(-GONE_AFTER * 1000 - 1000) }), NOW)).toBe('gone');
  });

  it('is gone whatever it was asked to do, because silence is a fact and a pause is a request', () => {
    const quiet = { lastSeenAt: at(-GONE_AFTER * 1000 - 1000) };

    expect(runnerActivity(runner({ ...quiet, state: 'paused' }), NOW)).toBe('gone');
    expect(runnerActivity(runner({ ...quiet, state: 'stopping' }), NOW)).toBe('gone');
    expect(runnerActivity(runner({ ...quiet, claimKey: 'AER-12' }), NOW)).toBe('gone');
  });

  it('says what it was asked to do before what it is holding', () => {
    // A loop finishing its last increment: the ticket is true, and "this is the
    // last one" is what somebody watching needs.
    expect(runnerActivity(runner({ state: 'stopping', claimKey: 'AER-12' }), NOW)).toBe('stopping');
    expect(runnerActivity(runner({ state: 'paused' }), NOW)).toBe('paused');
  });

  it('reads a heartbeat it cannot parse as gone rather than as fine', () => {
    expect(runnerActivity(runner({ lastSeenAt: 'the other day' }), NOW)).toBe('gone');
  });

  it('names the ticket in the words on the row', () => {
    expect(activityWords(runner({ claimKey: 'AER-12' }), NOW)).toBe('Working AER-12');
    expect(activityWords(runner(), NOW)).toBe('Idle');
  });
});

describe('isControllable', () => {
  it('is a loop only, because nothing would ever read an instruction off a single increment', () => {
    expect(isControllable(runner())).toBe(true);
    expect(isControllable(runner({ kind: 'once' }))).toBe(false);
  });
});

describe('the bounds form', () => {
  it('shows an unbounded runner as empty boxes rather than as zeroes', () => {
    expect(boundsOf(runner())).toEqual({ under: '', maxRuns: '', maxSpend: '', untilAt: '' });
  });

  it('round-trips what the row holds', () => {
    const bounded = runner({ under: 'AER-930', maxRuns: 12, maxSpend: 40, untilAt: '2026-09-09T06:00:00Z' });

    expect(boundsOf(bounded)).toEqual({
      under: 'AER-930',
      maxRuns: '12',
      maxSpend: '40',
      untilAt: '2026-09-09T06:00:00Z',
    });
    expect(boundsChanged(boundsOf(bounded), bounded)).toBe(false);
  });

  it('notices a cap being taken off', () => {
    const bounded = runner({ maxRuns: 12 });

    expect(boundsChanged({ ...boundsOf(bounded), maxRuns: '' }, bounded)).toBe(true);
  });

  it('sends every field, so a cleared box clears the cap', () => {
    // The tri-state, from the other end: a field left out of the request means
    // "leave it alone", so an emptied box that was not sent would silently keep
    // the cap it was clearing.
    expect(boundsRequest({ under: ' AER-930 ', maxRuns: '', maxSpend: '40', untilAt: '' })).toEqual({
      under: 'AER-930',
      maxRuns: '',
      maxSpend: '40',
      untilAt: '',
    });
  });

  it('says an until needs an hour before the server has to', () => {
    expect(boundsProblem({ under: '', maxRuns: '', maxSpend: '', untilAt: '2026-09-09' })).toContain('an hour');
    expect(boundsProblem({ under: '', maxRuns: '', maxSpend: '', untilAt: '2026-09-09T06:00:00Z' })).toBeNull();
    expect(boundsProblem({ under: '', maxRuns: '', maxSpend: '', untilAt: '' })).toBeNull();
  });
});
