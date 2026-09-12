import { describe, expect, it } from 'vitest';
import { APP_BASENAME, hrefWithin, routerBasename } from './basename';

describe('routerBasename', () => {
  it('is the prefix when served under it on the house hostname', () => {
    expect(routerBasename('/apps/hatch/')).toBe(APP_BASENAME);
    expect(routerBasename('/apps/hatch')).toBe(APP_BASENAME);
    expect(routerBasename('/apps/hatch/issues/AER-12')).toBe(APP_BASENAME);
  });

  it('is the root on the hatch hostname, where the rewrite is invisible', () => {
    expect(routerBasename('/')).toBe('/');
    expect(routerBasename('/issues/AER-12')).toBe('/');
    expect(routerBasename('/import')).toBe('/');
  });

  it('matches by segment, so a neighbouring app is not mistaken for this one', () => {
    expect(routerBasename('/apps/hatchery/')).toBe('/');
    expect(routerBasename('/apps/admin/')).toBe('/');
  });
});

describe('hrefWithin', () => {
  it('names the prefix on the house hostname, where a raw anchor would otherwise miss it', () => {
    expect(hrefWithin(APP_BASENAME, '/issues/AER-12')).toBe('/apps/hatch/issues/AER-12');
  });

  it('changes nothing on the hatch hostname', () => {
    expect(hrefWithin('/', '/issues/AER-12')).toBe('/issues/AER-12');
  });
});
