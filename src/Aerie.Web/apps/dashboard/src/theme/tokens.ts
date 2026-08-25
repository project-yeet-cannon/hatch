import type { CircadianTimeline } from '../lib/circadianTheme';
import { lch } from '../lib/oklab';

/**
 * The day's keyframes, in order, as *light* rather than as widget colors.
 *
 * Each row describes the sky's two ends, the surface a card is made of, the ink
 * printed on it, and how bright and colorful accents should be at that hour.
 * Everything else the dashboard paints with is derived from those by
 * `paletteFor` in ../lib/circadianTheme.ts, under contrast floors that hold at
 * every point *between* two rows as well as on them. That is the whole reason
 * the table is this short: the previous version authored ~25 hexes per keyframe
 * and still produced an unreadable gray page an hour before sunset, because
 * nothing constrained the values in between.
 *
 * Numbers are OKLCh - `l` is perceived lightness 0..1, `c` is chroma (0.03 is a
 * faint tint, 0.13 is about as saturated as sRGB goes at these lightnesses),
 * and `h` is hue in degrees: ~60 amber, ~150 green, ~240 blue, ~320 plum.
 *
 * ## Reading the table
 *
 * Three rules make it hold together, and breaking any of them is what the tests
 * in ../lib/circadianTheme.test.ts are there to catch:
 *
 * 1. **The first and last rows are the same light.** The timeline covers one
 *    day's sun events, and the hours outside it clamp to the ends - so the small
 *    hours before `deepNight` and after `deepNightEnd` are only seamless if
 *    those two agree.
 * 2. **A polarity change happens at a shared instant.** `firstLight`/`dawnGlow`
 *    are both at civil dawn and `afterglow`/`duskFall` are both at civil dusk,
 *    so the inversion is a step between two hand-tuned palettes rather than a
 *    tween through the gray in the middle of them. Nothing else in the table
 *    may change `polarity` between neighbours.
 * 3. **The sky's hue may swing; the ink's may not.** Ink and surface stay near
 *    the ambient hue at low chroma all day. The one large hue swing in the
 *    table - the afternoon blue of `dayHold` to the gold of `goldenHour` -
 *    is deliberate and takes two hours, passing through the pale hazy horizon a
 *    real afternoon does (see the note on rectangular mixing in ../lib/oklab.ts).
 */
export const circadianTimeline: CircadianTimeline = [
  {
    // 3am. The dimmest the wall ever gets while still showing something.
    name: 'deepNight',
    at: { event: 'dawn', offsetMinutes: -210 },
    polarity: 'dark',
    skyTop: lch(0.13, 0.028, 272),
    skyBottom: lch(0.185, 0.05, 292),
    surface: lch(0.18, 0.026, 272),
    ink: lch(0.855, 0.012, 265),
    accentL: 0.7,
    accentC: 0.068,
    brightness: 0.04,
    idleBrightness: 0,
  },
  {
    name: 'lateNight',
    at: { event: 'dawn', offsetMinutes: -45 },
    polarity: 'dark',
    skyTop: lch(0.16, 0.032, 270),
    skyBottom: lch(0.225, 0.052, 288),
    surface: lch(0.21, 0.028, 271),
    ink: lch(0.885, 0.012, 265),
    accentL: 0.73,
    accentC: 0.078,
    brightness: 0.05,
    idleBrightness: 0,
  },
  {
    // Civil dawn, the last dark-polarity keyframe: the horizon has gone rose
    // but the room has not, so this is still light ink on a dark page.
    name: 'firstLight',
    at: { event: 'dawn', offsetMinutes: 0 },
    polarity: 'dark',
    skyTop: lch(0.225, 0.048, 288),
    skyBottom: lch(0.33, 0.07, 350),
    surface: lch(0.265, 0.032, 300),
    ink: lch(0.91, 0.016, 60),
    accentL: 0.76,
    accentC: 0.086,
    brightness: 0.1,
    idleBrightness: 0.04,
  },
  {
    // Civil dawn again, one instant later and the other way up. Deliberately a
    // *dim* light palette - dusty rose paper, not a white page - because the
    // backlight is still near its floor and the house is still dark.
    name: 'dawnGlow',
    at: { event: 'dawn', offsetMinutes: 0 },
    polarity: 'light',
    skyTop: lch(0.7, 0.048, 275),
    skyBottom: lch(0.79, 0.07, 35),
    surface: lch(0.78, 0.018, 40),
    ink: lch(0.2, 0.032, 300),
    accentL: 0.5,
    accentC: 0.092,
    brightness: 0.12,
    idleBrightness: 0.05,
  },
  {
    name: 'sunriseGlow',
    at: { event: 'sunrise', offsetMinutes: 0 },
    polarity: 'light',
    skyTop: lch(0.82, 0.048, 250),
    skyBottom: lch(0.88, 0.08, 55),
    surface: lch(0.93, 0.012, 55),
    ink: lch(0.3, 0.03, 45),
    accentL: 0.55,
    accentC: 0.098,
    brightness: 0.3,
    idleBrightness: 0.12,
  },
  {
    name: 'morning',
    at: { event: 'sunrise', offsetMinutes: 80 },
    polarity: 'light',
    skyTop: lch(0.9, 0.032, 245),
    skyBottom: lch(0.93, 0.038, 235),
    surface: lch(0.985, 0.005, 240),
    ink: lch(0.345, 0.026, 242),
    accentL: 0.62,
    accentC: 0.1,
    brightness: 0.62,
    idleBrightness: 0.28,
  },
  {
    // The original daylight palette, measured off the hexes it replaced:
    // #d8ecfb sky, white cards, #33404a ink.
    name: 'day',
    at: { event: 'sunrise', offsetMinutes: 200 },
    polarity: 'light',
    skyTop: lch(0.933, 0.03, 240),
    skyBottom: lch(0.945, 0.028, 232),
    surface: lch(1, 0.002, 240),
    ink: lch(0.364, 0.024, 241),
    accentL: 0.66,
    accentC: 0.1,
    brightness: 1,
    idleBrightness: 0.45,
  },
  {
    // A hold, not a keyframe: identical light to `day`, so the middle of the
    // day is flat and the whole blue-to-gold swing happens in the two hours
    // where the sun is actually doing it.
    name: 'dayHold',
    at: { event: 'sunset', offsetMinutes: -165 },
    polarity: 'light',
    skyTop: lch(0.933, 0.03, 240),
    skyBottom: lch(0.945, 0.028, 232),
    surface: lch(1, 0.002, 240),
    ink: lch(0.364, 0.024, 241),
    accentL: 0.66,
    accentC: 0.1,
    brightness: 1,
    idleBrightness: 0.45,
  },
  {
    name: 'goldenHour',
    at: { event: 'sunset', offsetMinutes: -45 },
    polarity: 'light',
    skyTop: lch(0.8, 0.055, 260),
    skyBottom: lch(0.845, 0.105, 62),
    surface: lch(0.93, 0.02, 65),
    ink: lch(0.32, 0.035, 55),
    accentL: 0.62,
    accentC: 0.105,
    brightness: 0.68,
    idleBrightness: 0.3,
  },
  {
    // Sunset itself: violet overhead, the sun on the horizon, cards still warm
    // parchment. The brightest chroma the table reaches.
    name: 'sunsetGlow',
    at: { event: 'sunset', offsetMinutes: 0 },
    polarity: 'light',
    skyTop: lch(0.62, 0.075, 285),
    skyBottom: lch(0.72, 0.13, 48),
    surface: lch(0.855, 0.028, 55),
    ink: lch(0.255, 0.04, 40),
    accentL: 0.53,
    accentC: 0.105,
    brightness: 0.4,
    idleBrightness: 0.17,
  },
  {
    // Civil dusk, the last light-polarity keyframe. Dim paper under a lamp: the
    // page has dropped most of the way to the night's brightness *before* the
    // inversion, which is what keeps the step at dusk small.
    name: 'afterglow',
    at: { event: 'dusk', offsetMinutes: 0 },
    polarity: 'light',
    skyTop: lch(0.4, 0.07, 295),
    skyBottom: lch(0.52, 0.095, 25),
    surface: lch(0.74, 0.03, 40),
    ink: lch(0.19, 0.038, 330),
    accentL: 0.46,
    accentC: 0.098,
    brightness: 0.2,
    idleBrightness: 0.08,
  },
  {
    // Civil dusk, the other way up. The lamps came on.
    name: 'duskFall',
    at: { event: 'dusk', offsetMinutes: 0 },
    polarity: 'dark',
    skyTop: lch(0.215, 0.052, 288),
    skyBottom: lch(0.29, 0.078, 330),
    surface: lch(0.27, 0.036, 305),
    ink: lch(0.905, 0.018, 62),
    accentL: 0.76,
    accentC: 0.09,
    brightness: 0.16,
    idleBrightness: 0.06,
  },
  {
    // The original night palette, measured off #0d101c / #171b29 / #e7ebf3.
    name: 'night',
    at: { event: 'dusk', offsetMinutes: 75 },
    polarity: 'dark',
    skyTop: lch(0.176, 0.026, 273),
    skyBottom: lch(0.219, 0.052, 292),
    surface: lch(0.225, 0.028, 272),
    ink: lch(0.9, 0.012, 265),
    accentL: 0.74,
    accentC: 0.085,
    brightness: 0.06,
    idleBrightness: 0,
  },
  {
    // Rule 1: the same light as `deepNight`, so midnight is not a seam.
    name: 'deepNightEnd',
    at: { event: 'dusk', offsetMinutes: 225 },
    polarity: 'dark',
    skyTop: lch(0.13, 0.028, 272),
    skyBottom: lch(0.185, 0.05, 292),
    surface: lch(0.18, 0.026, 272),
    ink: lch(0.855, 0.012, 265),
    accentL: 0.7,
    accentC: 0.068,
    brightness: 0.04,
    idleBrightness: 0,
  },
];

/** The keyframe the page falls back to before the first snapshot names a sunset. */
export const FALLBACK_MOOD = 'day';
