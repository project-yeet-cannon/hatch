import { describe, expect, it } from 'vitest';
import { CARD_TEXT_MAX, truncate } from './text';

describe('truncate', () => {
  it('leaves text that already fits exactly as it was', () => {
    expect(truncate('Renew the certificate', 40)).toBe('Renew the certificate');
  });

  it('leaves text of exactly the limit alone', () => {
    expect(truncate('abcde', 5)).toBe('abcde');
  });

  it('cuts on a word boundary and says it cut', () => {
    expect(truncate('Renew the wildcard certificate before August', 20)).toBe('Renew the wildcard…');
  });

  /* A 200-character "word" is a URL or a hash. There is no gap to be polite
     about, and searching back for one would throw the whole allowance away. */
  it('hard-cuts a single long token', () => {
    expect(truncate(`short ${'x'.repeat(40)}`, 20)).toBe('short xxxxxxxxxxxxxx…');
  });

  it('defaults to the length a card holds', () => {
    const long = 'word '.repeat(200);
    expect(truncate(long).length).toBeLessThanOrEqual(CARD_TEXT_MAX + 1);
  });
});
