import { describe, expect, it } from 'vitest';
import { PR_TEXT_MAX, pullRequestWords } from './pullRequest';

describe('pullRequestWords', () => {
  it('drops the scheme, which every one of them has', () => {
    expect(pullRequestWords('https://example.com/owner/repo/pull/12')).toBe('example.com/owner/repo/pull/12');
  });

  it('drops a plain http scheme too', () => {
    expect(pullRequestWords('http://example.com/owner/repo/pull/12')).toBe('example.com/owner/repo/pull/12');
  });

  it('drops a www. and a trailing slash', () => {
    expect(pullRequestWords('https://www.example.com/owner/repo/pull/12/')).toBe('example.com/owner/repo/pull/12');
  });

  /* A self-hosted forge on a port is a pull request like any other. Nothing
     here reads the host for a company it recognises. */
  it('leaves a host nobody has heard of exactly as it is', () => {
    expect(pullRequestWords('https://forge.internal:3000/team/thing/-/merge_requests/7')).toBe(
      'forge.internal:3000/team/thing/-/merge_requests/7',
    );
  });

  it('cuts a URL too long for the chip and says it cut', () => {
    const long = `https://example.com/owner/repo/pull/12?${'q'.repeat(80)}`;
    const words = pullRequestWords(long);

    expect(words.length).toBeLessThanOrEqual(PR_TEXT_MAX + 1);
    expect(words.endsWith('…')).toBe(true);
  });
});
