import { describe, expect, it } from 'vitest';
import { DEFAULT_LANDING, landingFor, returnToFromSearch, safeReturnTo } from './returnTo';

describe('safeReturnTo', () => {
  it('keeps a same-origin path, including its own query and fragment', () => {
    expect(safeReturnTo('/apps/family/storage/c/7QF2')).toBe('/apps/family/storage/c/7QF2');
    expect(safeReturnTo('/apps/admin/devices?tab=zones#top')).toBe('/apps/admin/devices?tab=zones#top');
  });

  it('refuses an absolute URL', () => {
    expect(safeReturnTo('https://evil.example/login')).toBeNull();
    expect(safeReturnTo('http://evil.example')).toBeNull();
  });

  // location.replace executes this one rather than navigating anywhere.
  it('refuses a javascript: URL', () => {
    expect(safeReturnTo('javascript:alert(1)')).toBeNull();
  });

  it('refuses a protocol-relative URL, which leaves the origin despite the leading slash', () => {
    expect(safeReturnTo('//evil.example')).toBeNull();
  });

  it('refuses the backslash form, which browsers fold into the protocol-relative one', () => {
    expect(safeReturnTo('/\\evil.example')).toBeNull();
  });

  it('refuses a return into this app, which would read as a sign-in that did nothing', () => {
    expect(safeReturnTo('/apps/auth')).toBeNull();
    expect(safeReturnTo('/apps/auth/')).toBeNull();
    expect(safeReturnTo('/apps/auth/r/K3M9P2QT')).toBeNull();
    expect(safeReturnTo('/apps/auth?r=/apps/')).toBeNull();
    expect(safeReturnTo('/auth')).toBeNull();
    expect(safeReturnTo('/auth/')).toBeNull();
  });

  it('does not mistake a sibling path for this app', () => {
    expect(safeReturnTo('/apps/authors')).toBe('/apps/authors');
    expect(safeReturnTo('/authors')).toBe('/authors');
  });

  it('refuses nothing at all', () => {
    expect(safeReturnTo(null)).toBeNull();
    expect(safeReturnTo(undefined)).toBeNull();
    expect(safeReturnTo('')).toBeNull();
  });
});

describe('returnToFromSearch', () => {
  it('reads and validates ?r=', () => {
    expect(returnToFromSearch('?r=%2Fapps%2Fadmin%2Fdevices')).toBe('/apps/admin/devices');
    expect(returnToFromSearch('?r=https%3A%2F%2Fevil.example')).toBeNull();
    expect(returnToFromSearch('')).toBeNull();
  });
});

describe('landingFor', () => {
  it('falls back to the app picker', () => {
    expect(landingFor(null)).toBe(DEFAULT_LANDING);
    expect(landingFor('/apps/admin/devices')).toBe('/apps/admin/devices');
  });
});
