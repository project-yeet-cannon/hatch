/**
 * Just enough OKLab/OKLCh to mix colors the way eyes read them, with no
 * dependency and no DOM. Used by circadianTheme.ts.
 *
 * ## Why not sRGB
 *
 * The circadian palette's whole job is to travel between colors. Channel-wise
 * sRGB interpolation - what this file replaced - does two things wrong on that
 * trip, and both were visible on the wall:
 *
 * - **It desaturates.** The straight line in RGB between two saturated colors
 *   of different hue passes near the gray axis, so a blue afternoon fading to
 *   an orange evening spends its middle as mud.
 * - **It is not perceptually uniform.** Half the numeric distance from white to
 *   black is nowhere near half the perceived distance, so "50% through the
 *   transition" looked nothing like halfway.
 *
 * OKLab fixes both: `L` is perceived lightness (0 black, 1 white) and `a`/`b`
 * are opponent axes whose Euclidean distance approximates perceived difference.
 *
 * ## Rectangular, not polar, on purpose
 *
 * [mixLab] interpolates `a`/`b` directly rather than hue-and-chroma. Polar
 * interpolation holds chroma up across a hue sweep, which is right for two
 * *nearby* hues and badly wrong for two *opposite* ones: the arc from an
 * afternoon blue sky to a golden-hour orange one passes through fully saturated
 * green. Rectangular mixing takes the short way through low chroma instead,
 * which is what a hazy late-afternoon sky actually does. Adjacent keyframes in
 * the theme are authored close enough in hue that everywhere else the two
 * agree.
 */

/** A color in OKLCh - lightness 0..1, chroma ~0..0.4, hue in degrees. */
export interface Lch {
  l: number;
  c: number;
  h: number;
}

/** A color in OKLab - lightness 0..1, and the two opponent axes. */
export interface Lab {
  l: number;
  a: number;
  b: number;
}

export function lch(l: number, c: number, h: number): Lch {
  return { l, c, h };
}

export function lchToLab({ l, c, h }: Lch): Lab {
  const rad = (h * Math.PI) / 180;
  return { l, a: c * Math.cos(rad), b: c * Math.sin(rad) };
}

export function labToLch({ l, a, b }: Lab): Lch {
  const hue = (Math.atan2(b, a) * 180) / Math.PI;
  return { l, c: Math.hypot(a, b), h: hue < 0 ? hue + 360 : hue };
}

/** Linear blend of two OKLab colors. `t` is clamped to 0..1. */
export function mixLab(x: Lab, y: Lab, t: number): Lab {
  const k = clamp01(t);
  return {
    l: x.l + (y.l - x.l) * k,
    a: x.a + (y.a - x.a) * k,
    b: x.b + (y.b - x.b) * k,
  };
}

/** The same color at a different lightness, hue and chroma untouched. */
export function atLightness(color: Lch, l: number): Lch {
  return { ...color, l: clamp01(l) };
}

/**
 * `#rrggbb` for a color, chroma-reduced until it fits sRGB.
 *
 * Out-of-gamut OKLCh is normal - the space is much larger than sRGB, and a
 * generated token can easily land outside it. Per-channel clipping would shift
 * hue *and* lightness; halving chroma toward the gray axis holds both, which
 * matters because lightness is exactly what the contrast floor is enforced on.
 */
export function toHex(color: Lch): string {
  const inGamut = reduceToGamut(color);
  const [r, g, b] = labToSrgb(lchToLab(inGamut));
  return `#${channel(r)}${channel(g)}${channel(b)}`;
}

/** `rgba(...)` for a color at a given alpha, gamut-reduced as [toHex] is. */
export function toRgba(color: Lch, alpha: number): string {
  const [r, g, b] = labToSrgb(lchToLab(reduceToGamut(color)));
  return `rgba(${byte(r)}, ${byte(g)}, ${byte(b)}, ${Number(clamp01(alpha).toFixed(3))})`;
}

/** Parses `#rgb`, `#rrggbb`, or `rgb(a)` into OKLCh. Throws on anything else. */
export function parseToLch(color: string): Lch {
  return labToLch(srgbToLab(parseRgb(color)));
}

export function clamp01(value: number): number {
  return value < 0 ? 0 : value > 1 ? 1 : value;
}

function reduceToGamut(color: Lch): Lch {
  if (color.c <= 0 || fits(color)) return color;
  let lo = 0;
  let hi = color.c;
  // 12 halvings resolve chroma far finer than an 8-bit channel can show.
  for (let i = 0; i < 12; i += 1) {
    const mid = (lo + hi) / 2;
    if (fits({ ...color, c: mid })) lo = mid;
    else hi = mid;
  }
  return { ...color, c: lo };
}

function fits(color: Lch): boolean {
  const [r, g, b] = labToSrgb(lchToLab(color));
  const slack = 0.0005;
  return (
    r >= -slack && r <= 1 + slack && g >= -slack && g <= 1 + slack && b >= -slack && b <= 1 + slack
  );
}

/** OKLab -> linear sRGB -> gamma sRGB, each channel returned as an unclamped 0..1. */
function labToSrgb({ l, a, b }: Lab): [number, number, number] {
  const lp = l + 0.3963377774 * a + 0.2158037573 * b;
  const mp = l - 0.1055613458 * a - 0.0638541728 * b;
  const sp = l - 0.0894841775 * a - 1.291485548 * b;
  const L = lp * lp * lp;
  const M = mp * mp * mp;
  const S = sp * sp * sp;
  return [
    gamma(4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S),
    gamma(-1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S),
    gamma(-0.0041960863 * L - 0.7034186147 * M + 1.707614701 * S),
  ];
}

function srgbToLab([r, g, b]: [number, number, number]): Lab {
  const R = linear(r);
  const G = linear(g);
  const B = linear(b);
  const l = Math.cbrt(0.4122214708 * R + 0.5363325363 * G + 0.0514459929 * B);
  const m = Math.cbrt(0.2119034982 * R + 0.6806995451 * G + 0.1073969566 * B);
  const s = Math.cbrt(0.0883024619 * R + 0.2817188376 * G + 0.6299787005 * B);
  return {
    l: 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
    a: 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
    b: 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
  };
}

function gamma(c: number): number {
  return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.pow(c, 1 / 2.4) - 0.055;
}

function linear(c: number): number {
  return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
}

function byte(channelValue: number): number {
  return Math.round(clamp01(channelValue) * 255);
}

function channel(channelValue: number): string {
  return byte(channelValue).toString(16).padStart(2, '0');
}

function parseRgb(color: string): [number, number, number] {
  const hex = color.match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
  if (hex) {
    const digits =
      hex[1].length === 3
        ? hex[1]
            .split('')
            .map((c) => c + c)
            .join('')
        : hex[1];
    const n = parseInt(digits, 16);
    return [((n >> 16) & 255) / 255, ((n >> 8) & 255) / 255, (n & 255) / 255];
  }
  const rgb = color.match(/^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*[\d.]+\s*)?\)$/i);
  if (rgb) {
    return [Number(rgb[1]) / 255, Number(rgb[2]) / 255, Number(rgb[3]) / 255];
  }
  throw new Error(`oklab: unsupported color format "${color}"`);
}
