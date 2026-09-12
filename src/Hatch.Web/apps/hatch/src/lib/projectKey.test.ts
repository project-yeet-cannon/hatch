import { describe, expect, it } from 'vitest';
import { isValidProjectKey, normalizeProjectKey, rekeyObjection } from './projectKey';

describe('isValidProjectKey', () => {
  it('takes two to six characters starting with a letter', () => {
    expect(isValidProjectKey('AER')).toBe(true);
    expect(isValidProjectKey('OPS42')).toBe(true);
    expect(isValidProjectKey('A1')).toBe(true);
  });

  it('takes a key typed in lower case, because that is not a mistake', () => {
    expect(isValidProjectKey('hat')).toBe(true);
    expect(normalizeProjectKey(' hat ')).toBe('HAT');
  });

  it('refuses what the server would refuse', () => {
    expect(isValidProjectKey('A')).toBe(false);
    expect(isValidProjectKey('TOOLONG')).toBe(false);
    expect(isValidProjectKey('1AB')).toBe(false);
    expect(isValidProjectKey('A-B')).toBe(false);
    expect(isValidProjectKey('')).toBe(false);
  });
});

describe('rekeyObjection', () => {
  it('asks for a key before anything else', () => {
    expect(rekeyObjection('AER', '', '')).toContain('new key');
  });

  it('says what shape a key is', () => {
    expect(rekeyObjection('AER', 'A', 'AER')).toContain('two to six');
  });

  it('has nothing to do when the key is the one it already has', () => {
    expect(rekeyObjection('AER', 'aer', 'AER')).toContain('already has that key');
  });

  /* The speed bump. A valid, free, different key is still not enough - the
     operator has to have typed the key they are about to break. */
  it('holds out for the confirmation', () => {
    expect(rekeyObjection('AER', 'HAT', '')).toBe('Type AER to confirm.');
    expect(rekeyObjection('AER', 'HAT', 'HAT')).toBe('Type AER to confirm.');
  });

  it('takes the confirmation in any case', () => {
    expect(rekeyObjection('AER', 'HAT', 'aer')).toBeNull();
  });

  it('is silent when everything is in order', () => {
    expect(rekeyObjection('AER', 'HAT', 'AER')).toBeNull();
  });
});
