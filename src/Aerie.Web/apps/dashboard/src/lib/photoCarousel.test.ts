import { describe, expect, it } from 'vitest';
import { nextIndex, photoCaption, photoDate } from './photoCarousel';
import type { CarouselPhoto } from '../types';

const TZ = 'America/New_York';

function photo(overrides: Partial<CarouselPhoto> = {}): CarouselPhoto {
  return {
    assetId: 'p1',
    albumName: 'Christmas 2025',
    takenAt: '2025-12-25T15:00:00.000Z',
    city: 'Boulder',
    country: 'USA',
    ...overrides,
  };
}

describe('photoCaption', () => {
  it('reads album, place, then when', () => {
    expect(photoCaption(photo(), TZ)).toBe('Christmas 2025 · Boulder, USA · December 2025');
  });

  // Immich's knowledge of a photo is uneven, and a caption made of dashes
  // standing in for what it doesn't know is worse than a short one.
  it('drops a place it does not know', () => {
    expect(photoCaption(photo({ city: null, country: null }), TZ)).toBe('Christmas 2025 · December 2025');
  });

  it('uses whichever half of the place there is', () => {
    expect(photoCaption(photo({ city: null }), TZ)).toBe('Christmas 2025 · USA · December 2025');
    expect(photoCaption(photo({ country: null }), TZ)).toBe('Christmas 2025 · Boulder · December 2025');
  });

  // A scanned print: no date, no place, and still worth showing.
  it('is just the album when that is all there is', () => {
    expect(photoCaption(photo({ takenAt: null, city: null, country: null }), TZ)).toBe('Christmas 2025');
  });
});

describe('photoDate', () => {
  it('is month and year, in the house timezone', () => {
    expect(photoDate('2025-12-25T15:00:00.000Z', TZ)).toBe('December 2025');
  });

  /**
   * The hour that crosses a month boundary in one zone and not the other. A
   * caption saying "September" over a photo everyone remembers taking in August
   * is exactly the kind of small wrongness a wall display gets noticed for.
   */
  it('resolves the boundary in the house timezone, not UTC', () => {
    expect(photoDate('2025-09-01T02:00:00.000Z', TZ)).toBe('August 2025');
  });

  it('has nothing to say about a photo with no date', () => {
    expect(photoDate(null, TZ)).toBeNull();
  });

  it('treats an unparseable date as no date rather than throwing', () => {
    expect(photoDate('not a date', TZ)).toBeNull();
  });
});

describe('nextIndex', () => {
  it('advances', () => {
    expect(nextIndex(0, 3)).toBe(1);
  });

  it('wraps, because a photo frame comes round again', () => {
    expect(nextIndex(2, 3)).toBe(0);
  });

  // The deck emptying mid-cycle: an admin un-ticked the last album while the
  // wall was on photo 30 of 60.
  it('stays put on an empty deck', () => {
    expect(nextIndex(29, 0)).toBe(0);
  });
});
