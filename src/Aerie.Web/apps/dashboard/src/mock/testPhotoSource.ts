import type { PhotoCarousel, PhotoSource } from '../types';

/**
 * Photos' half of the all-X / all-9999 source (see testDataSource.ts). Two
 * things are being exposed here, and neither is the picture:
 *
 *   - Hardcoded caption text. Every album name is X, so anything the carousel
 *     draws from its own strings stands out.
 *   - A photo that will not load. The second entry's URL is deliberately
 *     nothing, because "the bytes did not arrive" is a state a wall carousel
 *     spends real time in - a deleted asset, a photo server mid-restart - and a
 *     component that only draws correctly when every image resolves will show
 *     an empty black rectangle for twenty seconds on a kitchen wall.
 */

const X = 'XXXX';
const X_LONGEST = 'XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX';

/** A 2x2 transparent GIF - a real image, so one entry is known to succeed. */
const PIXEL = 'data:image/gif;base64,R0lGODlhAgACAIAAAP///wAAACH5BAEAAAAALAAAAAACAAIAAAICRAEAOw==';

const BROKEN = 'data:image/gif;base64,not-an-image';

export class TestPhotoSource implements PhotoSource {
  getCarousel(): Promise<PhotoCarousel> {
    return Promise.resolve({
      photos: [
        { assetId: 'x-1', albumName: X_LONGEST, takenAt: new Date().toISOString(), city: X, country: X },
        { assetId: 'x-broken', albumName: X, takenAt: null, city: null, country: null },
        { assetId: 'x-2', albumName: X, takenAt: new Date().toISOString(), city: X_LONGEST, country: X },
      ],
      totalPhotos: 9999,
      generatedAt: new Date().toISOString(),
      error: null,
    });
  }

  imageUrl(assetId: string): string {
    return assetId === 'x-broken' ? BROKEN : PIXEL;
  }
}
