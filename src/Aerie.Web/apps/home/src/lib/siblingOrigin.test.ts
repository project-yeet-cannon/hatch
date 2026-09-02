import { describe, expect, it } from 'vitest';
import { siblingOrigin } from './siblingOrigin';

describe('siblingOrigin', () => {
  it('swaps the first label for the sibling service', () => {
    expect(siblingOrigin('home.example.org', 'metrics')).toBe('https://metrics.example.org/');
  });

  it('keeps every label below the first, so a deeper host still resolves', () => {
    expect(siblingOrigin('home.house.example.org', 'logs')).toBe('https://logs.house.example.org/');
  });

  it('has no domain to borrow from a bare label', () => {
    expect(siblingOrigin('localhost', 'share')).toBeNull();
  });

  it('has no domain to borrow from an IPv4 literal', () => {
    expect(siblingOrigin('192.168.1.240', 'share')).toBeNull();
  });

  it('has no domain to borrow from an IPv6 literal', () => {
    expect(siblingOrigin('fd00::1', 'share')).toBeNull();
  });

  /* A two-label host is the interesting edge: the domain is what is left after
     the first label is dropped, which for `example.org` is `org` - a sibling
     under a public suffix, and a link nobody wants. This case does not arise
     in the house (the apps are always served from a subdomain) and the
     alternative - a public-suffix list in the picker - costs more than the
     wrong link it would prevent, so it is recorded rather than handled. */
  it('drops the first label of a two-label host, suffix and all', () => {
    expect(siblingOrigin('example.org', 'share')).toBe('https://share.org/');
  });
});
