import { useCallback, useEffect, useMemo, useState } from 'react';
import type { CarouselPhoto, PhotoSource } from '../types';
import { DWELL_MS, nextIndex, photoCaption } from '../lib/photoCarousel';

/** The cross-fade. Slow on purpose: a hard cut in the corner of your eye reads as something being wrong. */
const FADE_MS = 1400;

/**
 * The family photo frame, below the room cards.
 *
 * Four things about the way this draws are deliberate:
 *
 *   - **Contain, over a blurred copy of itself.** A library is full of
 *     portraits, and `cover` would crop the heads off half of them. Filling the
 *     leftover space with a blurred, scaled copy of the same photo - already in
 *     the browser's cache, so it costs no second fetch - keeps a fixed-height
 *     box without cropping anybody.
 *   - **No color of its own.** Every surface here is a circadian token, so the
 *     frame dims with the rest of the wall and the veil
 *     (useCircadianTheme.ts) passes over it like everything else. A photo frame
 *     that lit a dark hallway at 3am is the bug kiosk_brightness.md exists to
 *     prevent, and the fix is to not opt out of the mechanism that already
 *     handles it.
 *   - **It stops when nobody can see it.** A hidden tab keeps no timer, so a
 *     tablet that slept for six hours does not wake and rush through a thousand
 *     photos catching up.
 *   - **A photo that won't load leaves the deck.** Each one fails at most once
 *     per deck, which is what keeps a dead photo server from turning into a
 *     loop of failed requests as fast as the browser can make them.
 */
export function PhotoCarousel({
  photos,
  source,
  timeZone,
}: {
  photos: CarouselPhoto[];
  source: PhotoSource;
  timeZone: string;
}) {
  // One piece of state, because the two halves change together: the slot being
  // shown, and the photo on its way out of it. The outgoing one stays mounted
  // for the length of the fade - without it a change of photo is a cut through
  // the background, which is the one transition a wall display should not make.
  const [slide, setSlide] = useState<{ index: number; leaving: CarouselPhoto | null }>({ index: 0, leaving: null });

  // Photos whose bytes did not arrive: deleted in Immich since the deck was
  // drawn, or fetched while the API was down.
  const [failed, setFailed] = useState<ReadonlySet<string>>(() => new Set());

  // A new deck is a fresh start, failures included - the photo that 502'd a
  // minute ago is worth one more try now that the manifest came back.
  useEffect(() => {
    setFailed(new Set());
    setSlide({ index: 0, leaving: null });
  }, [photos]);

  const visible = useMemo(() => photos.filter((p) => !failed.has(p.assetId)), [photos, failed]);

  const advance = useCallback(() => {
    setSlide((current) => ({
      index: nextIndex(current.index, visible.length),
      leaving: visible[current.index] ?? null,
    }));
  }, [visible]);

  // The deck shrank under the index - a photo dropped out, or a refetch came
  // back shorter. Wrapping to the start beats indexing off the end.
  useEffect(() => {
    setSlide((current) => (current.index < visible.length ? current : { ...current, index: 0 }));
  }, [visible.length]);

  useEffect(() => {
    if (visible.length <= 1) return;

    let timer: ReturnType<typeof setInterval> | undefined;

    const start = () => {
      if (timer === undefined) timer = setInterval(advance, DWELL_MS);
    };
    const stop = () => {
      clearInterval(timer);
      timer = undefined;
    };

    // Nothing to look at, nothing to advance. The tablet's screen going off and
    // the browser being backgrounded both land here.
    const onVisibility = () => (document.hidden ? stop() : start());

    onVisibility();
    document.addEventListener('visibilitychange', onVisibility);
    return () => {
      stop();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [advance, visible.length]);

  // The outgoing layer is dropped once its animation is over. On a timer rather
  // than on animationend because an animation on an element the browser never
  // painted - a tab hidden mid-fade - fires no event, and a layer left mounted
  // forever would sit on top of every photo after it.
  useEffect(() => {
    if (slide.leaving === null) return;
    const timer = setTimeout(() => setSlide((current) => ({ ...current, leaving: null })), FADE_MS);
    return () => clearTimeout(timer);
  }, [slide.leaving]);

  const fail = useCallback((assetId: string) => {
    setFailed((previous) => {
      const next = new Set(previous);
      next.add(assetId);
      return next;
    });
  }, []);

  const photo = visible[slide.index];
  if (photo === undefined) return null;

  // Fetched at the browser's leisure so it is in cache before it is faded to.
  // Without it the first frame of every transition is the empty box underneath.
  const upcoming = visible[nextIndex(slide.index, visible.length)];

  return (
    <button
      type="button"
      className="hf-photo"
      // The caption below carries what the photo is, so an assistive reader
      // gets one description of it rather than two.
      aria-label="Show the next photo"
      onClick={advance}
    >
      {slide.leaving !== null && slide.leaving.assetId !== photo.assetId && (
        <PhotoLayer key={`${slide.leaving.assetId}-out`} photo={slide.leaving} source={source} leaving />
      )}
      <PhotoLayer key={photo.assetId} photo={photo} source={source} onFailed={() => fail(photo.assetId)} />

      <span className="hf-photo-caption">{photoCaption(photo, timeZone)}</span>

      {upcoming !== undefined && upcoming.assetId !== photo.assetId && (
        <img className="hf-photo-preload" src={source.imageUrl(upcoming.assetId)} alt="" aria-hidden />
      )}
    </button>
  );
}

/**
 * One photo: the blurred backdrop and the photo itself, faded in or out
 * together. Two <img> elements on one src, which the browser serves from cache
 * the second time - a single element cannot both fill the box and stay
 * uncropped.
 */
function PhotoLayer({
  photo,
  source,
  leaving = false,
  onFailed,
}: {
  photo: CarouselPhoto;
  source: PhotoSource;
  leaving?: boolean;
  onFailed?: () => void;
}) {
  const src = source.imageUrl(photo.assetId);

  return (
    <span className={`hf-photo-layer${leaving ? ' is-leaving' : ''}`} aria-hidden>
      <img className="hf-photo-blur" src={src} alt="" />
      <img className="hf-photo-img" src={src} alt="" onError={onFailed} />
    </span>
  );
}
