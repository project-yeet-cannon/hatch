import { describe, expect, it } from 'vitest';
import { CONSOLE_MESSAGE_LIMIT, createFailureDedupe, describeConsoleArgs, requestPath } from './logCapture';

describe('describeConsoleArgs', () => {
  it('joins plain arguments', () => {
    expect(describeConsoleArgs(['failed to', 'do the thing'])).toBe('failed to do the thing');
  });

  it('prefers an Error message over its stringification', () => {
    expect(describeConsoleArgs([new Error('boom')])).toBe('boom');
  });

  it('serialises objects', () => {
    expect(describeConsoleArgs([{ status: 502 }])).toBe('{"status":502}');
  });

  it('survives a circular structure rather than throwing', () => {
    const circular: Record<string, unknown> = {};
    circular.self = circular;
    // The capture path must never fail louder than the thing it reports.
    expect(() => describeConsoleArgs([circular])).not.toThrow();
    expect(describeConsoleArgs([circular])).toBe('[object Object]');
  });

  it('survives a value JSON.stringify returns undefined for', () => {
    expect(describeConsoleArgs([undefined])).toBe('undefined');
  });

  it('truncates a message too long for a tablet', () => {
    const long = 'x'.repeat(CONSOLE_MESSAGE_LIMIT + 50);
    const described = describeConsoleArgs([long]);
    expect(described).toHaveLength(CONSOLE_MESSAGE_LIMIT + 1);
    expect(described.endsWith('…')).toBe(true);
  });
});

describe('requestPath', () => {
  const base = 'https://kiosk.example.test/apps/dashboard/';

  it('reduces a relative path against the document', () => {
    expect(requestPath('/api/dashboard', base)).toBe('/api/dashboard');
  });

  it('drops the query, which is not part of the identity of a failure', () => {
    expect(requestPath('/api/photos?count=30', base)).toBe('/api/photos');
  });

  it('handles an absolute URL', () => {
    expect(requestPath('https://kiosk.example.test/api/sun-events', base)).toBe('/api/sun-events');
  });

  it('handles a URL object and a Request-like input', () => {
    expect(requestPath(new URL('/api/zones', base), base)).toBe('/api/zones');
    expect(requestPath({ url: `${base}api/routines` } as Request, base)).toBe('/apps/dashboard/api/routines');
  });

  it('falls back to the raw input when it will not parse', () => {
    expect(requestPath('::not a url::', '::also not::')).toBe('::not a url::');
  });
});

describe('createFailureDedupe', () => {
  it('lets the first occurrence through immediately', () => {
    const dedupe = createFailureDedupe(60_000);
    expect(dedupe.note('GET /api/dashboard 502', 0)).toEqual({ sinceLast: 0 });
  });

  it('suppresses repeats inside the window', () => {
    const dedupe = createFailureDedupe(60_000);
    dedupe.note('k', 0);
    expect(dedupe.note('k', 1_000)).toBeNull();
    expect(dedupe.note('k', 59_999)).toBeNull();
  });

  it('reports how many it swallowed when the window reopens', () => {
    const dedupe = createFailureDedupe(60_000);
    dedupe.note('k', 0);
    dedupe.note('k', 10_000);
    dedupe.note('k', 20_000);
    expect(dedupe.note('k', 60_000)).toEqual({ sinceLast: 2 });
  });

  it('resets the count after reporting it, so it is a delta not a total', () => {
    const dedupe = createFailureDedupe(60_000);
    dedupe.note('k', 0);
    dedupe.note('k', 10_000);
    expect(dedupe.note('k', 60_000)).toEqual({ sinceLast: 1 });
    expect(dedupe.note('k', 120_000)).toEqual({ sinceLast: 0 });
  });

  it('keys are independent - one noisy endpoint does not mute another', () => {
    const dedupe = createFailureDedupe(60_000);
    dedupe.note('GET /api/dashboard 502', 0);
    expect(dedupe.note('GET /api/photos 500', 1_000)).toEqual({ sinceLast: 0 });
  });

  it('does nothing at all when the window equals the caller\'s interval', () => {
    // Kept as a test because it is the trap this had already fallen into: a 60s
    // dedupe against App.tsx's 60s poll lands on the boundary every time, so
    // `now - lastLoggedAt < windowMs` is false forever and nothing collapses.
    // The window has to be longer than the interval it is protecting against -
    // see NETWORK_DEDUPE_MS in clientLogger.ts.
    const dedupe = createFailureDedupe(60_000);
    let logged = 0;
    for (let minute = 0; minute < 60; minute++) {
      if (dedupe.note('GET /api/dashboard 502', minute * 60_000) !== null) logged++;
    }
    expect(logged).toBe(60);
  });

  it('collapses an hour of a failing 60s poll once the window clears it', () => {
    // Sixty entries in a 64-slot buffer pushes every other fault on the page
    // out, which is the opposite of what the buffer is for.
    const dedupe = createFailureDedupe(5 * 60_000);
    let logged = 0;
    for (let minute = 0; minute < 60; minute++) {
      if (dedupe.note('GET /api/dashboard 502', minute * 60_000) !== null) logged++;
    }
    expect(logged).toBe(12);
  });
});
