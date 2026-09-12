import { describe, expect, it } from 'vitest';
import {
  DEFAULT_STATUS_COLOR,
  INK_ON_DARK,
  INK_ON_LIGHT,
  channels,
  contrastInk,
  isHexColor,
  safeColor,
  statusVars,
} from './color';

describe('isHexColor', () => {
  it('takes the one shape the API stores', () => {
    expect(isHexColor('#6b7280')).toBe(true);
    expect(isHexColor('#6B7280')).toBe(true);
  });

  it('refuses everything else, including the shorthands CSS would take', () => {
    expect(isHexColor('#abc')).toBe(false);
    expect(isHexColor('6b7280')).toBe(false);
    expect(isHexColor('rebeccapurple')).toBe(false);
    expect(isHexColor('')).toBe(false);
    expect(isHexColor(null)).toBe(false);
    expect(isHexColor(undefined)).toBe(false);
  });
});

describe('safeColor', () => {
  it('lower-cases, so two spellings of one colour draw the same', () => {
    expect(safeColor('#AB12EF')).toBe('#ab12ef');
  });

  /* A status row written by something other than this app - a migration, a
     restored backup - must not be able to put arbitrary text into a style
     attribute. */
  it('falls back rather than passing nonsense into a stylesheet', () => {
    expect(safeColor('red; background: url(x)')).toBe(DEFAULT_STATUS_COLOR);
    expect(safeColor(null)).toBe(DEFAULT_STATUS_COLOR);
  });
});

describe('channels', () => {
  it('reads the three bytes', () => {
    expect(channels('#000000')).toEqual([0, 0, 0]);
    expect(channels('#ffffff')).toEqual([255, 255, 255]);
    expect(channels('#2a78d6')).toEqual([42, 120, 214]);
  });
});

describe('contrastInk', () => {
  it('writes white on a dark colour and black on a light one', () => {
    expect(contrastInk('#000000')).toBe(INK_ON_DARK);
    expect(contrastInk('#ffffff')).toBe(INK_ON_LIGHT);
  });

  /* The reason luminance is weighted rather than averaged: pure blue and pure
     yellow have channel averages of 85 and 170, and an eye reads the gap
     between them as far wider than that 2:1. */
  it('knows a saturated blue is dark and a saturated yellow is not', () => {
    expect(contrastInk('#0000ff')).toBe(INK_ON_DARK);
    expect(contrastInk('#ffff00')).toBe(INK_ON_LIGHT);
  });

  it('calls the shipped column colours the way they read', () => {
    expect(contrastInk('#6b7280')).toBe(INK_ON_DARK);
    expect(contrastInk('#008300')).toBe(INK_ON_DARK);
    expect(contrastInk('#eda100')).toBe(INK_ON_LIGHT);
  });

  /* Pinned because it is the surprising one and somebody will come to "fix" it:
     the shipped todo blue lands a hair on the light side of the crossover, and
     that is not a bug in the threshold. Black on it scores 4.77:1 against
     white's 4.40:1 - the darker ink is the one that reads better, however much
     the colour looks like it wants white. */
  it('puts dark ink on the shipped todo blue, which is the closer call', () => {
    expect(contrastInk('#2a78d6')).toBe(INK_ON_LIGHT);
  });
});

describe('statusVars', () => {
  it('hands the stylesheet a colour and the ink to write on it', () => {
    expect(statusVars('#008300')).toEqual({ '--status-color': '#008300', '--status-ink': INK_ON_DARK });
  });

  it('is still usable for a column whose colour is missing', () => {
    expect(statusVars(null)['--status-color']).toBe(DEFAULT_STATUS_COLOR);
  });
});
