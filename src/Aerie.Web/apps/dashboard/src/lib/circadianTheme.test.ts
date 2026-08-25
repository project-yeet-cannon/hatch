import { describe, expect, it } from 'vitest';
import type { SunEvents } from '../types';
import { parseToLch } from './oklab';
import {
  backlightAt,
  getCircadianPhase,
  moodAt,
  paletteFor,
  resolveThemeStyle,
  styleForMood,
  timelineTimes,
  type CircadianTimeline,
} from './circadianTheme';
import { circadianTimeline, FALLBACK_MOOD } from '../theme/tokens';

/** A plausible day: dawn 05:30, sunrise 06:00, sunset 20:00, dusk 20:30 (UTC for the arithmetic). */
const EVENTS: SunEvents = {
  dawn: '2026-06-21T05:30:00.000Z',
  sunrise: '2026-06-21T06:00:00.000Z',
  sunset: '2026-06-21T20:00:00.000Z',
  dusk: '2026-06-21T20:30:00.000Z',
};

/** A December day at a northern latitude - short, with the events bunched up. */
const WINTER: SunEvents = {
  dawn: '2026-12-21T15:10:00.000Z',
  sunrise: '2026-12-21T15:55:00.000Z',
  sunset: '2026-12-21T21:35:00.000Z',
  dusk: '2026-12-21T22:15:00.000Z',
};

/** Deliberately degenerate: every event at once, as polar latitudes can produce. */
const DEGENERATE: SunEvents = {
  dawn: '2026-06-21T12:00:00.000Z',
  sunrise: '2026-06-21T12:00:00.000Z',
  sunset: '2026-06-21T12:00:00.000Z',
  dusk: '2026-06-21T12:00:00.000Z',
};

function at(iso: string) {
  return getCircadianPhase(new Date(iso), EVENTS, circadianTimeline);
}

describe('the timeline table', () => {
  it('opens and closes on the same light, so midnight is not a seam', () => {
    const first = circadianTimeline[0];
    const last = circadianTimeline[circadianTimeline.length - 1];
    expect(paletteFor(last)).toEqual(paletteFor(first));
    expect(last.brightness).toBe(first.brightness);
    expect(last.idleBrightness).toBe(first.idleBrightness);
  });

  it('changes polarity only between two keyframes sharing an instant', () => {
    const times = timelineTimes(EVENTS, circadianTimeline);
    const flips = circadianTimeline.filter(
      (mood, i) => i > 0 && circadianTimeline[i - 1].polarity !== mood.polarity,
    );
    expect(flips.map((m) => m.name)).toEqual(['dawnGlow', 'duskFall']);
    for (let i = 1; i < circadianTimeline.length; i += 1) {
      if (circadianTimeline[i - 1].polarity === circadianTimeline[i].polarity) continue;
      expect(times[i]).toBe(times[i - 1]);
    }
  });

  it('anchors the two inversions to civil dawn and civil dusk', () => {
    const times = timelineTimes(EVENTS, circadianTimeline);
    const nameAt = (name: string) => times[circadianTimeline.findIndex((m) => m.name === name)];
    expect(nameAt('dawnGlow')).toBe(Date.parse(EVENTS.dawn));
    expect(nameAt('duskFall')).toBe(Date.parse(EVENTS.dusk));
  });

  it('holds keyframe times non-decreasing even when the sun events collapse', () => {
    const times = timelineTimes(DEGENERATE, circadianTimeline);
    for (let i = 1; i < times.length; i += 1) {
      expect(times[i]).toBeGreaterThanOrEqual(times[i - 1]);
    }
  });
});

describe('getCircadianPhase', () => {
  it('clamps to the first keyframe in the small hours before it', () => {
    expect(at('2026-06-21T00:30:00.000Z')).toMatchObject({ index: 0, progress: 0, polarity: 'dark' });
  });

  it('is mid-day and flat through the afternoon', () => {
    const noon = at('2026-06-21T13:00:00.000Z');
    expect(noon.from).toBe('day');
    expect(noon.to).toBe('dayHold');
    expect(noon.polarity).toBe('light');
    // The hold means the palette does not move at all across it.
    expect(paletteFor(moodAt(noon, circadianTimeline))).toEqual(
      paletteFor(moodAt(at('2026-06-21T16:00:00.000Z'), circadianTimeline)),
    );
  });

  it('is still dark-polarity in the last instant before civil dawn', () => {
    const phase = at('2026-06-21T05:29:59.000Z');
    expect(phase.polarity).toBe('dark');
    expect(phase.to).toBe('firstLight');
  });

  it('lands on the far side of the inversion at civil dawn itself', () => {
    const phase = at('2026-06-21T05:30:00.000Z');
    expect(phase.polarity).toBe('light');
    expect(phase.from).toBe('dawnGlow');
  });

  it('is still light-polarity in the last instant before civil dusk', () => {
    const phase = at('2026-06-21T20:29:59.000Z');
    expect(phase.polarity).toBe('light');
    expect(phase.to).toBe('afterglow');
  });

  it('lands on the far side of the inversion at civil dusk itself', () => {
    const phase = at('2026-06-21T20:30:00.000Z');
    expect(phase.polarity).toBe('dark');
    expect(phase.from).toBe('duskFall');
  });

  it('runs the golden hour into sunset over the 45 minutes before it', () => {
    const phase = at('2026-06-21T19:37:30.000Z');
    expect(phase.from).toBe('goldenHour');
    expect(phase.to).toBe('sunsetGlow');
    expect(phase.progress).toBeCloseTo(0.5, 5);
  });
});

/**
 * The regression this whole rewrite exists for.
 *
 * The palette that shipped before this went to a flat gray - body text the same
 * color as the card under it - about half an hour before sunset, because it
 * tweened an inverted pair of palettes token by token. These sweep every minute
 * of three very different days and assert the floors hold at all of them, so no
 * future keyframe can reintroduce a moment the wall cannot be read from across
 * the room.
 */
describe('legibility, every minute of the day', () => {
  const days: [string, SunEvents][] = [
    ['midsummer', EVENTS],
    ['midwinter', WINTER],
    ['degenerate', DEGENERATE],
  ];

  for (const [label, events] of days) {
    it(`holds every contrast floor across ${label}`, () => {
      const worst = { ink: 1, muted: 1, accent: 1, chroma: 1, minute: -1 };
      const day = Date.parse(events.sunrise.slice(0, 10) + 'T00:00:00.000Z');

      for (let minute = 0; minute < 24 * 60; minute += 1) {
        const phase = getCircadianPhase(new Date(day + minute * 60_000), events, circadianTimeline);
        const palette = paletteFor(moodAt(phase, circadianTimeline));
        const surface = parseToLch(palette.card).l;

        const inkGap = Math.abs(parseToLch(palette.ink).l - surface);
        const mutedGap = Math.abs(parseToLch(palette.muted).l - surface);
        const accentGap = Math.min(
          ...[palette.warmInk, palette.okInk, palette.coolInk, palette.tintSky, palette.tintSlate].map(
            (color) => Math.abs(parseToLch(color).l - surface),
          ),
        );
        // Accents must stay colored, not just contrasty - the old palette's
        // other failure was every hue converging on the same gray.
        const chroma = Math.min(
          ...[palette.comfort, palette.warm, palette.cool].map((color) => parseToLch(color).c),
        );

        if (inkGap < worst.ink) Object.assign(worst, { ink: inkGap, minute });
        worst.muted = Math.min(worst.muted, mutedGap);
        worst.accent = Math.min(worst.accent, accentGap);
        worst.chroma = Math.min(worst.chroma, chroma);
      }

      expect(worst.ink).toBeGreaterThanOrEqual(0.5);
      expect(worst.muted).toBeGreaterThanOrEqual(0.25);
      expect(worst.accent).toBeGreaterThanOrEqual(0.32);
      expect(worst.chroma).toBeGreaterThanOrEqual(0.04);
    });
  }

  it('never emits a token the browser cannot parse', () => {
    const day = Date.parse('2026-06-21T00:00:00.000Z');
    for (let minute = 0; minute < 24 * 60; minute += 7) {
      const style = resolveThemeStyle(
        getCircadianPhase(new Date(day + minute * 60_000), EVENTS, circadianTimeline),
        circadianTimeline,
      );
      for (const [name, value] of Object.entries(style)) {
        expect(value, `${name} at minute ${minute}`).not.toMatch(/NaN|undefined/);
      }
      expect(style['--bg']).toMatch(/^linear-gradient\(180deg, #[0-9a-f]{6}, #[0-9a-f]{6} 55%, #[0-9a-f]{6}\)$/);
    }
  });
});

describe('paletteFor', () => {
  it('enforces the ink floor even on a keyframe that ignores it', () => {
    const broken: CircadianTimeline = [
      { ...circadianTimeline[6], ink: { l: 0.98, c: 0.01, h: 240 }, surface: { l: 1, c: 0, h: 240 } },
    ];
    const palette = paletteFor(broken[0]);
    const gap = parseToLch(palette.card).l - parseToLch(palette.ink).l;
    expect(gap).toBeGreaterThanOrEqual(0.5);
  });

  it('draws the accent hues at the same angle at noon and at 3am', () => {
    const noon = paletteFor(circadianTimeline[6]);
    const small = paletteFor(circadianTimeline[0]);
    for (const key of ['comfort', 'warm', 'cool', 'tintMoss', 'tintPlum'] as const) {
      // Within a couple of degrees: the residue is 8-bit quantisation, not drift.
      expect(Math.abs(parseToLch(small[key]).h - parseToLch(noon[key]).h)).toBeLessThan(2);
    }
    // ...and dimmer, not grayer, at night: night accents sit above the surface.
    expect(parseToLch(small.warm).l).toBeGreaterThan(parseToLch(small.card).l);
  });
});

describe('styleForMood', () => {
  it('resolves the pre-snapshot fallback', () => {
    expect(styleForMood(circadianTimeline, FALLBACK_MOOD)['--card']).toBe('#ffffff');
  });

  it('throws on a keyframe name that is not in the table', () => {
    expect(() => styleForMood(circadianTimeline, 'twilight')).toThrow(/twilight/);
  });
});

describe('backlightAt', () => {
  it('runs the panel at full through the middle of the day and near off at 3am', () => {
    expect(backlightAt(at('2026-06-21T13:00:00.000Z'), circadianTimeline, false)).toBe(1);
    expect(backlightAt(at('2026-06-21T02:00:00.000Z'), circadianTimeline, false)).toBeLessThan(0.06);
    expect(backlightAt(at('2026-06-21T02:00:00.000Z'), circadianTimeline, true)).toBe(0);
  });
});
