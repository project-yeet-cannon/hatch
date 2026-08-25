/**
 * Pure day/night theme logic - no React, no DOM. Given "now" and the day's four
 * sun events (from DashboardData.sunEvents, ultimately SolarCalculator
 * .EventsForDay server-side), places the moment on a keyframed timeline and
 * generates every CSS custom property the dashboard paints with.
 *
 * ## What replaced the three-keyframe blend, and why
 *
 * The original version tweened between three hand-authored token sets -
 * day, amber, night - by interpolating each of ~25 hex strings channel-wise in
 * sRGB. Both halves of that were wrong, and the failure was not cosmetic:
 *
 * - **The day and night sets have opposite polarity.** Day is dark ink on white
 *   cards; night is light ink on near-black ones. Interpolating `ink` and
 *   `card` *independently* across that inversion sends them past each other,
 *   and at the crossing - a little before sunset, exactly when someone is
 *   standing in the kitchen - the text and the card it sits on were the same
 *   gray. Not "low contrast": zero. No choice of keyframe hexes fixes it,
 *   because the crossing is a property of tweening two inverted palettes.
 * - **sRGB tweening desaturates.** Every hue rotation ran through mud.
 *
 * So the model changed rather than the colors. Three things do the work:
 *
 * **1. Moods, not token sets.** A keyframe ([CircadianMood]) is eight numbers
 * and four colors describing the *light* at that hour - the sky's two ends, a
 * surface, an ink, and how bright/colorful accents should be. Every token is
 * *derived* from that by [paletteFor]. Nobody hand-picks a `--muted` again, and
 * the number of authored values per keyframe fell by two thirds.
 *
 * **2. Contrast is an invariant, not an intention.** [paletteFor] separates ink
 * from surface by at least [MIN_INK_GAP] in OKLab lightness, muted by
 * [MIN_MUTED_GAP], and every accent-as-text by [MIN_ACCENT_GAP] - *after*
 * interpolation, on the blended mood. Illegibility is now unreachable by
 * construction, including for keyframes nobody has written yet.
 *
 * **3. Accents keep their hue all day.** The nine semantic colors (comfort,
 * warm, cool, and the six Gather tints) are fixed hues rendered at the hour's
 * lightness and chroma. They are never mixed *toward* each other, so "the warm
 * one" is the same orange at noon and at 2am, only dimmer.
 *
 * ## The one discontinuity, and where it went
 *
 * Polarity still has to invert once each way - a white page at 3am is a lamp,
 * and a near-black one at noon is unreadable in sunlight. That inversion cannot
 * be tweened through; see above. So it is *scheduled* instead: it happens at
 * civil dawn and civil dusk, the two moments the room itself changes over, and
 * the timeline puts the last keyframe of one polarity and the first of the next
 * at the same instant so no segment ever spans it.
 *
 * The 1-2 seconds of it is covered by a dip to black in the page (see
 * `useCircadianFlip` in App.tsx) - a cut to dark and back reads as nightfall,
 * where a cross-dissolve between opposite palettes reads as a broken renderer.
 * `events.dusk` had been carried unused by both halves of this cycle for its
 * whole life; this is what it is for.
 */
import type { SunEvents } from '../types';
import { atLightness, clamp01, labToLch, lch, lchToLab, mixLab, toHex, toRgba, type Lch } from './oklab';

/** Minimum OKLab lightness between body text and the surface under it. */
const MIN_INK_GAP = 0.52;
/** Minimum for secondary text. Lower than [MIN_INK_GAP] on purpose - it is meant to recede. */
const MIN_MUTED_GAP = 0.26;
/** Minimum for an accent hue used as text or as a rail, rather than as a fill. */
const MIN_ACCENT_GAP = 0.34;
/** How far `--muted` sits from `--ink`, as a fraction of the ink/surface gap. */
const MUTED_FALLOFF = 0.47;

/** The fixed hues every accent is drawn at, in OKLCh degrees. */
const ROLE_HUE = { comfort: 160, warm: 62, cool: 245 } as const;

/**
 * The six Gather list colors as a hue and a chroma scale, rather than as hexes.
 *
 * A list's color is stored by *name* server-side
 * (src/Aerie.Web/apps/family/src/modules/gather/palette.ts) precisely so each
 * client can answer the name in its own light - the phone answers it twice, for
 * light and dark, and the kiosk answers it continuously, here. Slate is the odd
 * one out by design: it is the near-neutral of the set, so it takes a fraction
 * of the hour's chroma rather than all of it.
 */
const TINT = {
  sky: { hue: 240, chroma: 1 },
  moss: { hue: 162, chroma: 0.95 },
  clay: { hue: 50, chroma: 1.05 },
  plum: { hue: 315, chroma: 1 },
  sun: { hue: 87, chroma: 1.15 },
  slate: { hue: 236, chroma: 0.3 },
} as const;

/** Which way ink sits from the surface it is printed on. */
export type Polarity = 'light' | 'dark';

/** A sun event a keyframe can be anchored to. */
export type SunEventName = keyof SunEvents;

/**
 * One keyframe of the day: the light at a moment, not the widgets under it.
 *
 * Authored in OKLCh because that is the space these are *reasoned* about -
 * "same hue, less light" is one number here and three in hex.
 */
export interface CircadianMood {
  /** Stable id, shown in the dev scrubber and in logs. */
  name: string;
  /** The sun event this keyframe hangs off, and how far from it, in minutes. */
  at: { event: SunEventName; offsetMinutes: number };
  polarity: Polarity;
  /** Top of the background gradient - the zenith. */
  skyTop: Lch;
  /** Bottom of the background gradient - the horizon, where the sun is. */
  skyBottom: Lch;
  /** Card and panel fill. */
  surface: Lch;
  /** Body text. Its lightness is a request; [MIN_INK_GAP] is the rule. */
  ink: Lch;
  /** Lightness for the nine accent hues at this hour. */
  accentL: number;
  /** Chroma for the same. */
  accentC: number;
  /**
   * The tablet backlight at this keyframe, 0..1, and the same idle - read by
   * nothing in the web app.
   *
   * They live here so the panel's curve and the page's palette are authored as
   * one table rather than two that drift; CircadianBrightness.kt mirrors this
   * column and DisplayController.kt applies it. The dev scrubber plots it, so
   * "the wall at 4am" can be judged without standing in the hallway.
   */
  brightness: number;
  idleBrightness: number;
}

/** The keyframes in order. Times come from the sun; see [timelineTimes]. */
export type CircadianTimeline = readonly CircadianMood[];

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
   * The near-black the page dips through at a polarity inversion, taken from
   * the hour's own sky rather than stated as a fixed color - the architecture
   * rule is that nothing on this page names a color the phase did not supply.
   */
  veil: string;
  tintSky: string;
  tintMoss: string;
  tintClay: string;
  tintPlum: string;
  tintSun: string;
  tintSlate: string;
}

/** Where `now` falls on the timeline. */
export interface CircadianPhase {
  /** Index of the keyframe at or before `now`. */
  index: number;
  /** 0..1 from that keyframe to the next. */
  progress: number;
  /** Names of the two keyframes being blended, for readouts and logs. */
  from: string;
  to: string;
  /** The polarity in force. A change in this between two renders is a flip. */
  polarity: Polarity;
}

/**
 * Absolute times for each keyframe, in epoch ms.
 *
 * Clamped to be non-decreasing rather than sorted: at extreme latitudes the sun
 * events can bunch up or invert, and a *sort* would happily reorder a
 * polarity pair and put a tween across the inversion. Clamping can only
 * collapse a segment to zero length, which the timeline already handles - that
 * is exactly what the two flip pairs are.
 */
export function timelineTimes(events: SunEvents, timeline: CircadianTimeline): number[] {
  const times: number[] = [];
  for (const mood of timeline) {
    const anchor = Date.parse(events[mood.at.event]);
    const at = anchor + mood.at.offsetMinutes * 60_000;
    times.push(times.length === 0 ? at : Math.max(at, times[times.length - 1]));
  }
  return times;
}

/** Which keyframes `now` sits between, and how far across. */
export function getCircadianPhase(
  now: Date,
  events: SunEvents,
  timeline: CircadianTimeline,
): CircadianPhase {
  const times = timelineTimes(events, timeline);
  const t = now.getTime();

  // The *last* keyframe at or before `now`, which is what makes a flip land on
  // the far side of itself: both moods of a flip pair share an instant, so at
  // that instant this picks the incoming polarity and no segment ever spans it.
  let index = 0;
  for (let i = 0; i < times.length; i += 1) {
    if (times[i] <= t) index = i;
  }

  const last = timeline.length - 1;
  if (index >= last) {
    return { index: last, progress: 0, from: timeline[last].name, to: timeline[last].name, polarity: timeline[last].polarity };
  }

  const from = timeline[index];
  const to = timeline[index + 1];
  return {
    index,
    progress: progressBetween(t, times[index], times[index + 1]),
    from: from.name,
    to: to.name,
    polarity: from.polarity,
  };
}

function progressBetween(t: number, start: number, end: number): number {
  if (end <= start) return 1;
  return Math.min(1, Math.max(0, (t - start) / (end - start)));
}

/** The blended mood at a phase - the interpolation happens here and nowhere else. */
export function moodAt(phase: CircadianPhase, timeline: CircadianTimeline): CircadianMood {
  const from = timeline[phase.index];
  const to = timeline[Math.min(phase.index + 1, timeline.length - 1)];
  if (from === to || phase.progress <= 0) return from;
  if (phase.progress >= 1) return to;
  return blendMoods(from, to, phase.progress);
}

/** The resolved CSS custom properties for a phase, ready to spread onto an inline `style`. */
export function resolveThemeStyle(phase: CircadianPhase, timeline: CircadianTimeline): Record<string, string> {
  return cssVars(paletteFor(moodAt(phase, timeline)));
}

/** The palette for one named keyframe - the pre-data fallback, and the dev page's swatches. */
export function styleForMood(timeline: CircadianTimeline, name: string): Record<string, string> {
  const mood = timeline.find((m) => m.name === name);
  if (!mood) throw new Error(`circadianTheme: no keyframe named "${name}"`);
  return cssVars(paletteFor(mood));
}

/** The tablet backlight at a phase, 0..1. Mirrored by CircadianBrightness.kt. */
export function backlightAt(phase: CircadianPhase, timeline: CircadianTimeline, idle: boolean): number {
  const mood = moodAt(phase, timeline);
  return idle ? mood.idleBrightness : mood.brightness;
}

function blendMoods(from: CircadianMood, to: CircadianMood, t: number): CircadianMood {
  return {
    ...to,
    name: `${from.name}->${to.name}`,
    skyTop: mixLch(from.skyTop, to.skyTop, t),
    skyBottom: mixLch(from.skyBottom, to.skyBottom, t),
    surface: mixLch(from.surface, to.surface, t),
    ink: mixLch(from.ink, to.ink, t),
    accentL: lerp(from.accentL, to.accentL, t),
    accentC: lerp(from.accentC, to.accentC, t),
    brightness: lerp(from.brightness, to.brightness, t),
    idleBrightness: lerp(from.idleBrightness, to.idleBrightness, t),
    polarity: from.polarity,
  };
}

function mixLch(a: Lch, b: Lch, t: number): Lch {
  return labToLch(mixLab(lchToLab(a), lchToLab(b), t));
}

function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}

/**
 * Every token, derived from one mood.
 *
 * The contrast floors are applied here rather than trusted to the keyframes,
 * because the input is usually a *blend* of two keyframes and no amount of care
 * in the table constrains what falls out of the middle of one.
 */
export function paletteFor(mood: CircadianMood): ThemeTokens {
  // +1 when ink is lighter than what it sits on, -1 when darker.
  const sign = mood.polarity === 'dark' ? 1 : -1;
  const [surface, ink] = separate(mood.surface, mood.ink, MIN_INK_GAP, sign);

  const gap = Math.abs(ink.l - surface.l);
  const muted = clampAgainst(
    { ...ink, c: Math.min(Math.max(ink.c, 0.014) * 1.7, 0.055), l: ink.l - sign * gap * MUTED_FALLOFF },
    surface,
    MIN_MUTED_GAP,
    sign,
  );

  // Hairlines are the ink itself at a few percent, so they pick up the hour's
  // tint and sit correctly on both a card and the sky behind it.
  const hairline = mood.polarity === 'dark' ? 0.08 : 0.1;
  const grid = mood.polarity === 'dark' ? 0.11 : 0.14;
  const skeleton = mood.polarity === 'dark' ? 0.09 : 0.13;

  const accent = (hue: number, chromaScale = 1, lightnessShift = 0): Lch =>
    lch(clamp01(mood.accentL + lightnessShift), mood.accentC * chromaScale, hue);
  /** The same hue, pulled far enough from the surface to be read as text. */
  const asText = (color: Lch): Lch => clampAgainst(color, surface, MIN_ACCENT_GAP, sign);

  const comfort = accent(ROLE_HUE.comfort);
  const warm = accent(ROLE_HUE.warm, 1.15, 0.035);
  const cool = accent(ROLE_HUE.cool);
  const tint = (spec: { hue: number; chroma: number }): string =>
    toHex(asText(accent(spec.hue, spec.chroma)));

  return {
    bgTop: toHex(mood.skyTop),
    bgMid: toHex(mixLch(mood.skyTop, mood.skyBottom, 0.5)),
    bgBottom: toHex(mood.skyBottom),
    card: toHex(surface),
    outbg: toHex(surface),
    ink: toHex(ink),
    muted: toHex(muted),
    line: toRgba(ink, hairline),
    gridc: toRgba(ink, grid),
    skel: toRgba(ink, skeleton),
    comfort: toHex(comfort),
    warm: toHex(warm),
    cool: toHex(cool),
    band: toRgba(comfort, 0.12),
    okBg: toRgba(comfort, 0.16),
    okInk: toHex(asText(comfort)),
    warmBg: toRgba(warm, 0.18),
    warmInk: toHex(asText(warm)),
    coolBg: toRgba(cool, 0.16),
    coolInk: toHex(asText(cool)),
    // A cast shadow is the sky's color arriving from above, so it takes the
    // zenith's hue - dark and heavy at night, a soft blue-gray at noon.
    shadowTint: toRgba(
      lch(mood.polarity === 'dark' ? 0.05 : 0.45, Math.min(mood.skyTop.c, 0.05), mood.skyTop.h),
      mood.polarity === 'dark' ? 0.42 : 0.14,
    ),
    veil: toHex(lch(0.045, Math.min(mood.skyTop.c, 0.03), mood.skyTop.h)),
    tintSky: tint(TINT.sky),
    tintMoss: tint(TINT.moss),
    tintClay: tint(TINT.clay),
    tintPlum: tint(TINT.plum),
    tintSun: tint(TINT.sun),
    tintSlate: tint(TINT.slate),
  };
}

/**
 * Pushes `ink` and `surface` apart until they clear `minGap`, splitting the
 * correction between them and then re-deriving one from the other so the floor
 * survives a clamp at either end of the lightness range.
 */
function separate(surface: Lch, ink: Lch, minGap: number, sign: number): [Lch, Lch] {
  const gap = (ink.l - surface.l) * sign;
  if (gap >= minGap) return [surface, ink];

  const deficit = minGap - gap;
  const surfaceL = clamp01(surface.l - sign * deficit * 0.5);
  const inkL = clamp01(surfaceL + sign * minGap);
  return [atLightness(surface, clamp01(inkL - sign * minGap)), atLightness(ink, inkL)];
}

/** Moves one color far enough from a fixed surface, leaving the surface alone. */
function clampAgainst(color: Lch, surface: Lch, minGap: number, sign: number): Lch {
  const gap = (color.l - surface.l) * sign;
  return gap >= minGap ? color : atLightness(color, surface.l + sign * minGap);
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
    '--veil': t.veil,
    '--tint-sky': t.tintSky,
    '--tint-moss': t.tintMoss,
    '--tint-clay': t.tintClay,
    '--tint-plum': t.tintPlum,
    '--tint-sun': t.tintSun,
    '--tint-slate': t.tintSlate,
    '--shadow': `0 12px 32px ${t.shadowTint}`,
  };
}
