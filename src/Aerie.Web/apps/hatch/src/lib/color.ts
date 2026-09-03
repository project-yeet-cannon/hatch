/* A column's colour, and the two questions a stylesheet cannot answer about it.

   Statuses carry a hex value chosen by the operator (EfHatchStatus.Color), and
   the board paints it in two places that need different things: a swatch, which
   only needs the colour, and a filled pill with the status name inside it,
   which needs to know whether that name should be written in white or in black.
   CSS has no way to ask - `color: contrast()` is not shipping anywhere - so the
   answer is computed here and handed to the stylesheet as a custom property. */

/** The stored shape: `#rrggbb`, and nothing else. Mirrors EfHatchStatus.IsValidColor. */
const HEX = /^#[0-9a-f]{6}$/i;

/** What a colour this cannot read falls back to - the same grey the API defaults a column to. */
export const DEFAULT_STATUS_COLOR = '#6b7280';

export const isHexColor = (color: string | null | undefined): boolean => !!color && HEX.test(color);

/** A colour to paint with: the one given, or the default if it is not one. */
export const safeColor = (color: string | null | undefined): string =>
  isHexColor(color) ? color!.toLowerCase() : DEFAULT_STATUS_COLOR;

/** The three channels, 0-255. */
export function channels(color: string): [number, number, number] {
  const hex = safeColor(color).slice(1);
  return [0, 2, 4].map((at) => parseInt(hex.slice(at, at + 2), 16)) as [number, number, number];
}

/**
 * Relative luminance, per WCAG 2.x. Not the naive average of the three
 * channels: the eye is roughly six times more sensitive to green than to blue,
 * and averaging calls a saturated blue "light" and writes black on it.
 */
export function luminance(color: string): number {
  const [r, g, b] = channels(color).map((channel) => {
    const scaled = channel / 255;
    return scaled <= 0.03928 ? scaled / 12.92 : ((scaled + 0.055) / 1.055) ** 2.4;
  });

  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** Ink for text sitting directly on a filled swatch of this colour. */
export const INK_ON_LIGHT = '#1a1a1a';
export const INK_ON_DARK = '#ffffff';

/**
 * Which of the two inks to write on this colour.
 *
 * The threshold is 0.179, which is where white and black text cross over in
 * WCAG contrast against a background - not 0.5, which is where the arithmetic
 * midpoint is and which puts black text on a mid blue that badly needs white.
 */
export const contrastInk = (color: string): string =>
  luminance(color) > 0.179 ? INK_ON_LIGHT : INK_ON_DARK;

/**
 * The custom properties every status-coloured element is drawn from. Handed to
 * an element's `style`, read by App.css - so one component decides the colour
 * and the stylesheet decides what to do with it, rather than each site
 * inventing its own inline paint.
 */
export function statusVars(color: string | null | undefined): Record<string, string> {
  const safe = safeColor(color);
  return { '--status-color': safe, '--status-ink': contrastInk(safe) };
}
