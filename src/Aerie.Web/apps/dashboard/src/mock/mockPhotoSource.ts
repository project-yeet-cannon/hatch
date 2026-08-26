import type { CarouselPhoto, PhotoCarousel, PhotoSource } from '../types';

/**
 * A photo library with no Immich behind it: a dozen synthetic "photos" drawn as
 * SVG data URIs, so `?source=mock` is a usable surface for working on the
 * carousel — the fade, the caption, the aspect handling — without a photo
 * server, an API key, or anybody's family pictures in a screenshot.
 *
 * They are deliberately not all the same shape. A wall carousel's whole
 * difficulty is that a library holds portraits and landscapes and the
 * occasional panorama, and a component only verified against 3:2 landscapes is
 * not verified at all.
 */

const LATENCY_MS = 120;

/** Album names long enough to make a caption wrap somewhere, which is the point of having them here. */
const ALBUMS = ['Christmas 2025', 'Summer at the lake', 'Hikes'];

const PLACES: [string | null, string | null][] = [
  ['Boulder', 'USA'],
  ['Reykjavík', 'Iceland'],
  [null, null],
];

/**
 * The synthetic picture. A gradient plus its index, large enough that the
 * browser scales it the way it would scale a photo, and in one of three
 * shapes - landscape, portrait, panorama.
 */
function svgPhoto(index: number): string {
  const [width, height] = SHAPES[index % SHAPES.length];
  const hue = (index * 47) % 360;
  const svg = `
    <svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">
      <defs>
        <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stop-color="hsl(${hue} 55% 62%)"/>
          <stop offset="100%" stop-color="hsl(${(hue + 60) % 360} 45% 32%)"/>
        </linearGradient>
      </defs>
      <rect width="${width}" height="${height}" fill="url(#g)"/>
      <text x="50%" y="50%" fill="rgba(255,255,255,0.82)" font-family="sans-serif"
            font-size="${Math.round(Math.min(width, height) / 5)}" font-weight="700"
            text-anchor="middle" dominant-baseline="central">${index + 1}</text>
    </svg>`;
  return `data:image/svg+xml;utf8,${encodeURIComponent(svg.trim())}`;
}

const SHAPES: [number, number][] = [
  [1440, 960],
  [960, 1440],
  [1920, 720],
];

const PHOTOS: CarouselPhoto[] = Array.from({ length: 12 }, (_, i) => {
  const [city, country] = PLACES[i % PLACES.length];
  return {
    assetId: `mock-photo-${i}`,
    albumName: ALBUMS[i % ALBUMS.length],
    // Spread over years, so the caption's date formatting is exercised rather
    // than every photo reading "today".
    takenAt: new Date(Date.UTC(2020 + (i % 6), (i * 2) % 12, (i % 27) + 1, 14, 30)).toISOString(),
    city,
    country,
  };
});

export class MockPhotoSource implements PhotoSource {
  async getCarousel(count?: number): Promise<PhotoCarousel> {
    await new Promise((resolve) => setTimeout(resolve, LATENCY_MS));
    // Shuffled here too, because the server shuffles: a carousel that is only
    // ever verified against a stable order can hide an effect keyed on index.
    const shuffled = [...PHOTOS].sort(() => Math.random() - 0.5).slice(0, count ?? PHOTOS.length);
    return {
      photos: shuffled,
      totalPhotos: PHOTOS.length,
      generatedAt: new Date().toISOString(),
      error: null,
    };
  }

  imageUrl(assetId: string): string {
    const index = Number(assetId.replace('mock-photo-', ''));
    return svgPhoto(Number.isFinite(index) ? index : 0);
  }
}
