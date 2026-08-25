import { useEffect, useRef, useState } from 'react';
import type { SunEvents } from '../types';
import { getCircadianPhase, resolveThemeStyle, styleForMood } from '../lib/circadianTheme';
import { circadianTimeline, FALLBACK_MOOD } from '../theme/tokens';

/** How long the page takes to go dark at an inversion. */
const FLIP_COVER_MS = 700;
/** And how long the incoming palette takes to come back up out of it. */
const FLIP_REVEAL_MS = 900;

export interface CircadianTheme {
  /** Custom properties for the page root. */
  style: Record<string, string>;
  /** 0 normally; 1 while the veil is drawn over an inversion. */
  veilOpacity: number;
  /** What the veil takes to cross, in ms - the two directions differ. */
  veilDurationMs: number;
}

/**
 * The page's palette for a moment, and the cover for the two moments a day it
 * cannot simply fade through.
 *
 * Twice a day - civil dawn and civil dusk - the palette inverts: dark ink on
 * light cards becomes light ink on dark ones. No tween between those two stays
 * readable, because every path from "ink darker than its card" to "ink lighter
 * than its card" passes through "ink the same as its card". That is a property
 * of the inversion and not a tuning problem, and the palette this replaced
 * spent half an hour a day inside it (see ../lib/circadianTheme.ts).
 *
 * So the page dips to black instead. The veil fades in over [FLIP_COVER_MS],
 * the palette swaps behind it, and it fades back out over [FLIP_REVEAL_MS] - a
 * cut to dark and back, which reads as nightfall. A cross-dissolve between two
 * opposite palettes reads as a broken screen, and that is the difference worth
 * paying two seconds a day for: nobody who glances up at a wall display is
 * going to wait and see whether it recovers.
 *
 * The held palette is looked up rather than remembered. The keyframe before an
 * inversion *is* the outgoing palette - the table puts both sides of the
 * inversion at the same instant - so there is no need to cache what the last
 * render painted, and no chance of holding the wrong thing if a render is
 * dropped or replayed.
 */
export function useCircadianTheme(now: Date, events: SunEvents | undefined): CircadianTheme {
  const phase = events ? getCircadianPhase(now, events, circadianTimeline) : null;
  const [heldMood, setHeldMood] = useState<string | null>(null);
  const lastPolarity = useRef(phase?.polarity);

  useEffect(() => {
    const polarity = phase?.polarity;
    if (!polarity) return;
    // The first render that has sun events is an arrival, not an inversion.
    if (lastPolarity.current === undefined || lastPolarity.current === polarity) {
      lastPolarity.current = polarity;
      return;
    }
    lastPolarity.current = polarity;

    const outgoing = circadianTimeline[Math.max(0, (phase?.index ?? 0) - 1)];
    setHeldMood(outgoing.name);
    const swap = setTimeout(() => setHeldMood(null), FLIP_COVER_MS);
    return () => clearTimeout(swap);
    // phase.index is deliberately not a dependency: only a change of polarity
    // starts a dip, and re-running this on every keyframe crossing would
    // restart one mid-flight.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase?.polarity]);

  const style = heldMood
    ? styleForMood(circadianTimeline, heldMood)
    : phase
      ? resolveThemeStyle(phase, circadianTimeline)
      : styleForMood(circadianTimeline, FALLBACK_MOOD);

  return {
    style,
    veilOpacity: heldMood ? 1 : 0,
    veilDurationMs: heldMood ? FLIP_COVER_MS : FLIP_REVEAL_MS,
  };
}
