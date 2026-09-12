import { describe, expect, it } from 'vitest';
import { detectPlatform } from './runnerPlatform';

/* Real user agents, because the point of this function is that it survives the
   strings browsers actually send rather than the ones a test would invent. */
const agents = {
  chromeWindows:
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  edgeWindows:
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0',
  safariMac:
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15',
  chromeMac:
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36',
  firefoxLinux: 'Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0',
  chromeAndroid:
    'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36',
  safariIphone:
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1',
};

describe('detectPlatform', () => {
  it('finds Windows', () => {
    expect(detectPlatform(agents.chromeWindows)).toBe('win-x64');
    expect(detectPlatform(agents.edgeWindows)).toBe('win-x64');
  });

  /* The one worth writing down. Every Mac says "Intel Mac OS X" - Apple never
     changed it - so the string cannot be trusted for the architecture, and the
     page offers osx-x64 one click below for the Intel machines still out
     there. */
  it('sends every Mac to Apple Silicon, including the ones that say Intel', () => {
    expect(detectPlatform(agents.safariMac)).toBe('osx-arm64');
    expect(detectPlatform(agents.chromeMac)).toBe('osx-arm64');
  });

  it('finds Linux', () => {
    expect(detectPlatform(agents.firefoxLinux)).toBe('linux-x64');
  });

  /* Android names Linux, and would otherwise be offered a linux-x64 binary that
     could not run - which it still is, because there is nothing better to
     offer. Checked ahead of both so the ordering is deliberate rather than
     lucky. */
  it('does not read Android as a desktop Linux by accident', () => {
    expect(detectPlatform(agents.chromeAndroid)).toBe('linux-x64');
  });

  it('reads a phone reading the page as the Mac it is closest to', () => {
    expect(detectPlatform(agents.safariIphone)).toBe('osx-arm64');
  });

  it('falls back rather than answering nothing', () => {
    expect(detectPlatform('')).toBe('linux-x64');
    expect(detectPlatform('curl/8.7.1')).toBe('linux-x64');
    expect(detectPlatform('Mozilla/5.0 (X11; FreeBSD amd64)')).toBe('linux-x64');
  });

  it('answers one of the four published runtime identifiers, whatever it is given', () => {
    const published = ['win-x64', 'osx-arm64', 'osx-x64', 'linux-x64'];

    for (const agent of [...Object.values(agents), '', 'nonsense']) {
      expect(published).toContain(detectPlatform(agent));
    }
  });
});
