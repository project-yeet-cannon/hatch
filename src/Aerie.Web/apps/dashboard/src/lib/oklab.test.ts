import { describe, expect, it } from 'vitest';
import { labToLch, lch, lchToLab, mixLab, parseToLch, toHex, toRgba } from './oklab';

describe('lch/lab round trip', () => {
  it('survives a conversion out and back', () => {
    const start = lch(0.62, 0.11, 200);
    const back = labToLch(lchToLab(start));
    expect(back.l).toBeCloseTo(start.l, 6);
    expect(back.c).toBeCloseTo(start.c, 6);
    expect(back.h).toBeCloseTo(start.h, 4);
  });

  it('parses a hex back to the lightness it was built from', () => {
    const measured = parseToLch(toHex(lch(0.5, 0.08, 140)));
    expect(measured.l).toBeCloseTo(0.5, 2);
    expect(measured.h).toBeCloseTo(140, 0);
  });

  it('renders the sRGB primaries it should', () => {
    expect(toHex(parseToLch('#ffffff'))).toBe('#ffffff');
    expect(toHex(parseToLch('#000000'))).toBe('#000000');
    expect(toHex(parseToLch('#d8ecfb'))).toBe('#d8ecfb');
  });
});

describe('gamut reduction', () => {
  it('holds lightness and hue while dropping impossible chroma', () => {
    // 0.35 chroma at this lightness is far outside sRGB.
    const measured = parseToLch(toHex(lch(0.55, 0.35, 30)));
    expect(measured.l).toBeCloseTo(0.55, 2);
    expect(measured.h).toBeCloseTo(30, 0);
    expect(measured.c).toBeLessThan(0.35);
    expect(measured.c).toBeGreaterThan(0.1);
  });

  it('never emits an out-of-range channel', () => {
    for (let h = 0; h < 360; h += 15) {
      for (const l of [0.05, 0.3, 0.6, 0.95]) {
        expect(toHex(lch(l, 0.3, h))).toMatch(/^#[0-9a-f]{6}$/);
      }
    }
  });
});

describe('mixLab', () => {
  it('returns the endpoints at 0 and 1, and clamps beyond them', () => {
    const a = lchToLab(lch(0.2, 0.05, 20));
    const b = lchToLab(lch(0.8, 0.05, 200));
    expect(mixLab(a, b, 0)).toEqual(a);
    expect(mixLab(a, b, -3)).toEqual(a);
    for (const t of [1, 4]) {
      const end = mixLab(a, b, t);
      expect(end.l).toBeCloseTo(b.l, 12);
      expect(end.a).toBeCloseTo(b.a, 12);
      expect(end.b).toBeCloseTo(b.b, 12);
    }
  });

  it('takes the low-chroma route between opposing hues rather than through a third one', () => {
    // The afternoon-blue to golden-hour swing. Polar interpolation would put
    // saturated green in the middle of this; rectangular puts a pale haze.
    const mid = labToLch(mixLab(lchToLab(lch(0.9, 0.03, 235)), lchToLab(lch(0.85, 0.105, 62)), 0.5));
    expect(mid.c).toBeLessThan(0.05);
    expect(mid.h).not.toBeGreaterThan(180);
  });
});

describe('toRgba', () => {
  it('carries alpha through', () => {
    expect(toRgba(lch(0, 0, 0), 0.5)).toBe('rgba(0, 0, 0, 0.5)');
    expect(toRgba(lch(1, 0, 0), 1)).toBe('rgba(255, 255, 255, 1)');
  });
});
