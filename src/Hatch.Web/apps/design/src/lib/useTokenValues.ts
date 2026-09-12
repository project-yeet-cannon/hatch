import { useEffect, useState } from 'react';
import { useTheme } from '@hatch/ui';

/**
 * The values the browser has actually resolved for a set of custom properties,
 * right now, in the theme currently on screen.
 *
 * Reading them rather than restating them is the whole point of the gallery: a
 * swatch labelled with a hard-coded hex is a swatch that can disagree with the
 * token it claims to show, and Phase 6 is a designer changing exactly these
 * values.
 *
 * Two things about the timing are load-bearing:
 *
 * - This is a passive effect, not a layout effect or a render-time read.
 *   ThemeProvider writes `data-theme` from a *layout* effect, and React runs
 *   every layout effect before any passive effect - so by the time this runs,
 *   the attribute is on <html> and getComputedStyle returns the new palette.
 *   Reading during render would return the outgoing theme's values.
 * - `resolved` is the dependency rather than `choice`, so the OS flipping
 *   while the switch sits on Auto re-reads too.
 *
 * The first paint therefore shows names with no values, for one frame. That is
 * the honest failure mode: the alternative is guessing a value before the
 * cascade has one.
 */
export function useTokenValues(names: readonly string[]): Record<string, string> {
  const { resolved } = useTheme();
  const [values, setValues] = useState<Record<string, string>>({});

  // The array is rebuilt on every render by every caller; its contents are what
  // matters, so the join is the dependency and the array itself is not.
  const key = names.join('|');

  useEffect(() => {
    const computed = getComputedStyle(document.documentElement);
    const next: Record<string, string> = {};
    for (const name of key.split('|')) {
      next[name] = computed.getPropertyValue(name).trim();
    }
    // The lint rule that fires here is aimed at state derivable during render.
    // This state is not: it is a reading taken from the CSS cascade, which is
    // exactly the "external system" the rule's own guidance carves out, and it
    // cannot be taken until the cascade has settled. The cost is one extra
    // render per theme change, on a page nobody is measuring.
    // oxlint-disable-next-line react/set-state-in-effect
    setValues(next);
  }, [key, resolved]);

  return values;
}
