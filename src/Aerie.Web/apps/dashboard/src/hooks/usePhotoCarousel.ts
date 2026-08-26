import { useEffect, useState } from 'react';
import { getPhotoSource } from '../dataSource';
import { clientLogger } from '../lib/clientLogger';
import type { CarouselPhoto, PhotoSource } from '../types';

/**
 * How often a new deck is drawn. Long, and deliberately much longer than the
 * dashboard's own minute: the manifest is a shuffled sample of a library that
 * changes on the timescale of an afternoon, and refetching it often would mean
 * cutting away from a photo somebody was looking at to start a different
 * shuffle.
 */
const REFRESH_INTERVAL_MS = 30 * 60_000;

/** Enough photos that a deck outlasts its refresh interval at any sane dwell time, and small enough to be one modest JSON response. */
const DECK_SIZE = 60;

/**
 * The photos behind the wall carousel, plus where to fetch one's bytes.
 *
 * Its own data path rather than a field on the dashboard snapshot, for the
 * reason GatherTile has one: a dashboard poll that fails should not take the
 * photo frame down with it, and a photo manifest has no business being rebuilt
 * every sixty seconds alongside temperatures.
 *
 * A failed load keeps the last deck. The images are cached in the browser by
 * then, so a wall whose API is briefly unreachable keeps cycling through photos
 * rather than going black - which is the whole reason this is a manifest plus
 * an image URL rather than a stream.
 */
export function usePhotoCarousel(): { photos: CarouselPhoto[]; source: PhotoSource } {
  const [photos, setPhotos] = useState<CarouselPhoto[]>([]);
  const [source] = useState(getPhotoSource);

  useEffect(() => {
    let cancelled = false;

    const load = () => {
      source
        .getCarousel(DECK_SIZE)
        .then((carousel) => {
          if (cancelled) return;
          // An empty deck is a real answer - nobody has included an album - and
          // it must be allowed to empty the carousel, which is what takes the
          // section off the screen.
          setPhotos(carousel.photos);
          if (carousel.error !== null) {
            clientLogger.warn('Photo library is stale', {
              reason: carousel.error,
              photos: carousel.photos.length,
            });
          }
        })
        .catch((err: unknown) => {
          if (cancelled) return;
          // Kept, not cleared: the photos already on screen are still photos.
          clientLogger.error('Photo carousel load failed', {
            reason: err instanceof Error ? err.message : String(err),
          });
        });
    };

    load();
    const refresh = setInterval(load, REFRESH_INTERVAL_MS);
    return () => {
      cancelled = true;
      clearInterval(refresh);
    };
  }, [source]);

  return { photos, source };
}
