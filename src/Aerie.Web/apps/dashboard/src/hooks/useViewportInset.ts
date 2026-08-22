import { useEffect, useState } from 'react';

/** Below this, it's rounding and toolbar chrome rather than a keyboard. */
const MEANINGFUL_INSET_PX = 40;

/**
 * How much of the layout viewport something is covering from the bottom - in
 * practice, the soft keyboard.
 *
 * The kiosk activity declares no `android:windowSoftInputMode` and runs
 * immersive, so whether GeckoView resizes the viewport for the IME is untested
 * (docs/kiosk-architecture.md, "Text entry on the wall"). This is deliberately written so the answer
 * doesn't matter: if the visual viewport shrinks, callers pad by that much and
 * the bottom of a scrolling list stays reachable; if it never shrinks - or the
 * API isn't there at all - the value stays 0 and nothing changes. It is a hedge
 * that costs nothing when it turns out to be unnecessary.
 *
 * The overlay's own layout does not depend on this: every control lives in the
 * top half regardless. This only buys back the tail of a long list.
 */
export function useViewportInset(): number {
  const [inset, setInset] = useState(0);

  useEffect(() => {
    const viewport = window.visualViewport;
    if (!viewport) return;

    const update = () => {
      const covered = window.innerHeight - (viewport.height + viewport.offsetTop);
      setInset(covered > MEANINGFUL_INSET_PX ? Math.round(covered) : 0);
    };

    update();
    viewport.addEventListener('resize', update);
    viewport.addEventListener('scroll', update);
    return () => {
      viewport.removeEventListener('resize', update);
      viewport.removeEventListener('scroll', update);
    };
  }, []);

  return inset;
}
