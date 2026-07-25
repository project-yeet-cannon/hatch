import { describe, expect, it } from 'vitest';
import type { SunEvents } from '../types';
import { getCircadianPhase, lerpColor, resolveThemeStyle } from './circadianTheme';
import { circadianTokens } from '../theme/tokens';

const EVENTS: SunEvents = {
  dawn: '2026-06-21T10:00:00.000Z',
  sunrise: '2026-06-21T10:30:00.000Z',
  sunset: '2026-06-22T00:00:00.000Z',
  dusk: '2026-06-22T00:30:00.000Z',
};

describe('getCircadianPhase', () => {
  it('is night before dawn', () => {
    expect(getCircadianPhase(new Date('2026-06-21T09:00:00.000Z'), EVENTS)).toEqual({ kind: 'night' });
  });

  it('is night at/after dusk', () => {
    expect(getCircadianPhase(new Date('2026-06-22T00:30:00.000Z'), EVENTS)).toEqual({ kind: 'night' });
    expect(getCircadianPhase(new Date('2026-06-22T05:00:00.000Z'), EVENTS)).toEqual({ kind: 'night' });
  });

  it('is day between sunrise and sunset', () => {
    expect(getCircadianPhase(new Date('2026-06-21T15:00:00.000Z'), EVENTS)).toEqual({ kind: 'day' });
  });

  it('computes morning transition progress between dawn and sunrise', () => {
    // Halfway between 10:00 and 10:30.
    const phase = getCircadianPhase(new Date('2026-06-21T10:15:00.000Z'), EVENTS);
    expect(phase.kind).toBe('morningTransition');
    expect(phase.kind === 'morningTransition' && phase.progress).toBeCloseTo(0.5, 5);
  });

  it('computes evening transition progress between sunset and dusk', () => {
    // A quarter of the way between 00:00 and 00:30.
    const phase = getCircadianPhase(new Date('2026-06-22T00:07:30.000Z'), EVENTS);
    expect(phase.kind).toBe('eveningTransition');
    expect(phase.kind === 'eveningTransition' && phase.progress).toBeCloseTo(0.25, 5);
  });
});

describe('lerpColor', () => {
  it('returns the start color unchanged at t=0 and end color unchanged at t=1', () => {
    expect(lerpColor('#000000', '#ffffff', 0)).toBe('#000000');
    expect(lerpColor('#000000', '#ffffff', 1)).toBe('#ffffff');
  });

  it('interpolates the midpoint of hex colors', () => {
    expect(lerpColor('#000000', '#ffffff', 0.5)).toBe('rgba(128, 128, 128, 1)');
  });

  it('interpolates alpha for rgba colors', () => {
    expect(lerpColor('rgba(10, 20, 30, 0)', 'rgba(10, 20, 30, 1)', 0.5)).toBe('rgba(10, 20, 30, 0.5)');
  });
});

describe('resolveThemeStyle', () => {
  it('uses the day token set unblended in full day', () => {
    const style = resolveThemeStyle({ kind: 'day' }, circadianTokens);
    expect(style['--ink']).toBe(circadianTokens.day.ink);
    expect(style['--card']).toBe(circadianTokens.day.card);
  });

  it('uses the night token set unblended in full night', () => {
    const style = resolveThemeStyle({ kind: 'night' }, circadianTokens);
    expect(style['--ink']).toBe(circadianTokens.night.ink);
  });

  it('lands exactly on the amber token set at the midpoint of a transition', () => {
    const style = resolveThemeStyle({ kind: 'eveningTransition', progress: 0.5 }, circadianTokens);
    expect(style['--card']).toBe(circadianTokens.amber.card);
  });

  it('starts a transition at the "from" phase and ends at the "to" phase', () => {
    const start = resolveThemeStyle({ kind: 'eveningTransition', progress: 0 }, circadianTokens);
    const end = resolveThemeStyle({ kind: 'eveningTransition', progress: 1 }, circadianTokens);
    expect(start['--card']).toBe(circadianTokens.day.card);
    expect(end['--card']).toBe(circadianTokens.night.card);
  });
});
