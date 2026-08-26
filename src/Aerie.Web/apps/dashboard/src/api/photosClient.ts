import { handledUnauthorized } from '../lib/signIn';
import type { PhotoCarousel, PhotoSource } from '../types';

/**
 * Photos over HTTP — the wall's half of src/Aerie.Api/Modules/Photos.
 *
 * Read-only, so this is the shape of routinesClient rather than gatherClient:
 * one GET for the manifest, and a URL builder for the bytes. The images are not
 * fetched here at all — they go into an <img src> on this same origin, which is
 * what lets the browser cache them and what keeps a photo that fails to load
 * from being a rejected promise anyone has to handle.
 */

const BASE = '/api/photos';

export class ApiPhotoSource implements PhotoSource {
  async getCarousel(count?: number): Promise<PhotoCarousel> {
    const query = count === undefined ? '' : `?count=${count}`;
    const res = await fetch(`${BASE}/carousel${query}`, { headers: { Accept: 'application/json' } });
    if (handledUnauthorized(res)) return await new Promise<PhotoCarousel>(() => {});
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
    return (await res.json()) as PhotoCarousel;
  }

  /**
   * The preview rendition, which is Immich's ~1440px JPEG — the right size for
   * a tablet, and not the original, which the API refuses to proxy anyway.
   */
  imageUrl(assetId: string): string {
    return `${BASE}/assets/${encodeURIComponent(assetId)}/image?size=preview`;
  }
}
