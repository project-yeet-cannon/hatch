/**
 * A list's colour, as a name rather than a hex.
 *
 * The API stores Color as free text, so a hex would fit - but a hex chosen in
 * daylight is wrong in dark mode forever, and the kiosk overlay would have to
 * re-derive a readable version of whatever a phone picked. A name defers the
 * actual colour to CSS, where the theme already knows how to answer twice.
 *
 * The kiosk (Phase 3) ships the same six names. Anything unrecognised - an
 * older row, a hand-edited value - falls back to the first rather than
 * rendering untinted, so a list is never the odd one out by accident.
 */
export const LIST_COLORS = ['sky', 'moss', 'clay', 'plum', 'sun', 'slate'] as const;

export type ListColor = (typeof LIST_COLORS)[number];

const DEFAULT_COLOR: ListColor = 'sky';

export function colorOf(value: string | null | undefined): ListColor {
  return LIST_COLORS.includes(value as ListColor) ? (value as ListColor) : DEFAULT_COLOR;
}

/** What the pickers offer, and what an empty-state starter is created with. */
export const LIST_ICONS = ['🛒', '🔧', '💊', '📦', '🥕', '🍞', '🧻', '🐾', '🎁', '🧰', '🏡', '🧺'] as const;

export const DEFAULT_ICON = '🧺';
