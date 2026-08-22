/**
 * Pure day/dusk/night theme logic - no React, no DOM. Given "now" and the
 * day's four sun events (from DashboardData.sunEvents, ultimately
 * SolarCalculator.EventsForDay server-side), decides which phase of the
 * circadian cycle we're in and blends CSS custom-property values across it.
 *
 * Phases: full light (sunrise until the evening transition begins), an
 * evening transition (a long, slow fade starting EVENING_TRANSITION_LEAD_MINUTES
 * before actual sunset and running EVENING_TRANSITION_DURATION_HOURS deep into
 * dusk/night, so the display visibly settles rather than snapping at civil
 * twilight), full dark (rest of the night, until dawn), and a morning
 * transition (dawn-sunrise) that mirrors the old, shorter-style evening one.
 * Each transition blends through a warm "amber" keyframe at its midpoint
 * rather than going straight from light to dark, so the palette shifts from
 * high-energy blue tones toward low-energy orange ones as the sun goes down
 * (and back, in reverse, as it comes up) instead of just dimming.
 */
import type { SunEvents } from '../types';

/** How long before actual sunset the evening transition begins. */
const EVENING_TRANSITION_LEAD_MINUTES = 35;
/** How long the evening transition takes, start to full night. */
const EVENING_TRANSITION_DURATION_HOURS = 2;

const MINUTE_MS = 60_000;
const HOUR_MS = 3_600_000;

export type CircadianPhase =
  | { kind: 'day' }
  | { kind: 'night' }
  | { kind: 'eveningTransition'; progress: number }
  | { kind: 'morningTransition'; progress: number };

/** Every color token the theme drives, keyed by role rather than CSS variable name. */
export interface ThemeTokens {
  bgTop: string;
  bgMid: string;
  bgBottom: string;
  card: string;
  outbg: string;
  ink: string;
  muted: string;
  line: string;
  gridc: string;
  comfort: string;
  warm: string;
  cool: string;
  band: string;
  warmBg: string;
  warmInk: string;
  okBg: string;
  okInk: string;
  coolBg: string;
  coolInk: string;
  skel: string;
  shadowTint: string;
  /**
   * A Gather list's colour. Stored as one of six *names* rather than a hex
   * (src/Aerie.Web/apps/family/src/modules/gather/palette.ts), precisely so
   * each client can answer the name in its own light - the phone answers it
   * twice for light and dark, and the kiosk answers it continuously, here,
   * blended through the day like every other token in this file.
   */
  tintSky: string;
  tintMoss: string;
  tintClay: string;
  tintPlum: string;
  tintSun: string;
  tintSlate: string;
}

export interface CircadianTokenSets {
  day: ThemeTokens;
  amber: ThemeTokens;
  night: ThemeTokens;
}

/** Which phase of the cycle `now` falls in, and how far through a transition it is. */
export function getCircadianPhase(now: Date, events: SunEvents): CircadianPhase {
  const t = now.getTime();
  const dawn = Date.parse(events.dawn);
  const sunrise = Date.parse(events.sunrise);
  const sunset = Date.parse(events.sunset);

  const eveningStart = sunset - EVENING_TRANSITION_LEAD_MINUTES * MINUTE_MS;
  const eveningEnd = eveningStart + EVENING_TRANSITION_DURATION_HOURS * HOUR_MS;

  if (t < dawn) return { kind: 'night' };
  if (t < sunrise) return { kind: 'morningTransition', progress: progressBetween(t, dawn, sunrise) };
  if (t < eveningStart) return { kind: 'day' };
  if (t < eveningEnd) return { kind: 'eveningTransition', progress: progressBetween(t, eveningStart, eveningEnd) };
  return { kind: 'night' };
}

function progressBetween(t: number, start: number, end: number): number {
  if (end <= start) return 1;
  return Math.min(1, Math.max(0, (t - start) / (end - start)));
}

/** The resolved CSS custom properties for a phase, ready to spread onto an inline `style`. */
export function resolveThemeStyle(phase: CircadianPhase, tokens: CircadianTokenSets): Record<string, string> {
  switch (phase.kind) {
    case 'day':
      return cssVars(tokens.day);
    case 'night':
      return cssVars(tokens.night);
    case 'eveningTransition':
      return cssVars(blendThroughAmber(tokens.day, tokens.amber, tokens.night, phase.progress));
    case 'morningTransition':
      return cssVars(blendThroughAmber(tokens.night, tokens.amber, tokens.day, phase.progress));
  }
}

/** First half of a transition blends start->mid, second half blends mid->end. */
function blendThroughAmber(start: ThemeTokens, mid: ThemeTokens, end: ThemeTokens, progress: number): ThemeTokens {
  return progress <= 0.5 ? blendTokens(start, mid, progress / 0.5) : blendTokens(mid, end, (progress - 0.5) / 0.5);
}

function blendTokens(a: ThemeTokens, b: ThemeTokens, t: number): ThemeTokens {
  const keys = Object.keys(a) as (keyof ThemeTokens)[];
  const blended = {} as ThemeTokens;
  for (const key of keys) {
    blended[key] = lerpColor(a[key], b[key], t);
  }
  return blended;
}

function cssVars(t: ThemeTokens): Record<string, string> {
  return {
    '--bg': `linear-gradient(180deg, ${t.bgTop}, ${t.bgMid} 55%, ${t.bgBottom})`,
    '--card': t.card,
    '--outbg': t.outbg,
    '--ink': t.ink,
    '--muted': t.muted,
    '--line': t.line,
    '--gridc': t.gridc,
    '--comfort': t.comfort,
    '--warm': t.warm,
    '--cool': t.cool,
    '--band': t.band,
    '--warm-bg': t.warmBg,
    '--warm-ink': t.warmInk,
    '--ok-bg': t.okBg,
    '--ok-ink': t.okInk,
    '--cool-bg': t.coolBg,
    '--cool-ink': t.coolInk,
    '--skel': t.skel,
    '--tint-sky': t.tintSky,
    '--tint-moss': t.tintMoss,
    '--tint-clay': t.tintClay,
    '--tint-plum': t.tintPlum,
    '--tint-sun': t.tintSun,
    '--tint-slate': t.tintSlate,
    '--shadow': `0 12px 32px ${t.shadowTint}`,
  };
}

/** Linearly interpolates two `#hex` or `rgb(a)` colors, output as `rgba(...)`. */
export function lerpColor(a: string, b: string, t: number): string {
  if (t <= 0) return a;
  if (t >= 1) return b;
  const [r1, g1, b1, a1] = parseColor(a);
  const [r2, g2, b2, a2] = parseColor(b);
  const r = Math.round(lerp(r1, r2, t));
  const g = Math.round(lerp(g1, g2, t));
  const bl = Math.round(lerp(b1, b2, t));
  const al = Number(lerp(a1, a2, t).toFixed(3));
  return `rgba(${r}, ${g}, ${bl}, ${al})`;
}

function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}

function parseColor(color: string): [number, number, number, number] {
  const hex = color.match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
  if (hex) {
    const digits = hex[1].length === 3 ? hex[1].split('').map((c) => c + c).join('') : hex[1];
    const n = parseInt(digits, 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255, 1];
  }
  const rgb = color.match(/^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$/i);
  if (rgb) {
    return [Number(rgb[1]), Number(rgb[2]), Number(rgb[3]), rgb[4] === undefined ? 1 : Number(rgb[4])];
  }
  throw new Error(`circadianTheme: unsupported color format "${color}"`);
}
