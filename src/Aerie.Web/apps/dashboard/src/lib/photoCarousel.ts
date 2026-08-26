import type { CarouselPhoto } from '../types';

/**
 * The carousel's decisions that aren't rendering: what a photo is captioned
 * with, and which photo comes next.
 *
 * Here rather than in the component for the reason the rest of lib/ is: this is
 * the part with edge cases - a scan with no date, a photo with a country and no
 * city, a deck that ran out - and they are worth asserting on directly rather
 * than through a component that also happens to fade.
 */

/**
 * One line under the photo, or an empty string when there is nothing to say
 * beyond the album.
 *
 * Everything past the album name is optional because Immich's knowledge of it
 * is: a scanned print has no date and no place, and the wall shows what there
 * is rather than a row of dashes standing in for what there isn't.
 */
export function photoCaption(photo: CarouselPhoto, timeZone: string): string {
  return [photo.albumName, placeOf(photo), photoDate(photo.takenAt, timeZone)]
    .filter((part): part is string => part !== null && part.length > 0)
    .join(' · ');
}

/** City, country, both, or neither — in the order someone reads a place out loud. */
function placeOf(photo: CarouselPhoto): string | null {
  if (photo.city && photo.country) return `${photo.city}, ${photo.country}`;
  return photo.city ?? photo.country ?? null;
}

/**
 * Month and year. Not the day: on a wall, "August 2019" is what tells you which
 * summer you are looking at, and the 14th tells you nothing you were asking.
 */
export function photoDate(takenAt: string | null, timeZone: string): string | null {
  if (takenAt === null) return null;

  const at = new Date(takenAt);
  if (Number.isNaN(at.getTime())) return null;

  return new Intl.DateTimeFormat(undefined, { month: 'long', year: 'numeric', timeZone }).format(at);
}

/**
 * The next slot in a deck that wraps. Wrapping rather than stopping is the
 * whole behaviour of a photo frame: the manifest is a shuffled sample, so
 * coming round again is coming round to a different order the next time the
 * hook refetches, not to the same loop forever.
 *
 * A deck of nothing stays at 0, so a carousel that lost its photos mid-cycle
 * doesn't index into an empty array.
 */
export function nextIndex(current: number, length: number): number {
  if (length <= 0) return 0;
  return (current + 1) % length;
}
