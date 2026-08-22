/**
 * The kiosk half of Gather's list colours and icons — the same six names and
 * the same default icon the family shell uses
 * (src/Aerie.Web/apps/family/src/modules/gather/palette.ts). Duplicated rather
 * than shared because the two apps build independently and there is no shared
 * package between them; the list is six strings and a fallback, and the comment
 * on each side names the other.
 *
 * The colour resolves to a `--tint-*` custom property, which the circadian
 * theme blends through the day (see theme/tokens.ts). That is the whole reason
 * the API stores a name: a hex picked on a phone in daylight would sit on the
 * kitchen wall at 3am unchanged.
 */

const LIST_COLORS = ['sky', 'moss', 'clay', 'plum', 'sun', 'slate'] as const;

type ListColor = (typeof LIST_COLORS)[number];

/** Anything unrecognised falls back to the first, so a list is never untinted by accident. */
function colorOf(value: string | null): ListColor {
  return LIST_COLORS.includes(value as ListColor) ? (value as ListColor) : 'sky';
}

/** The CSS colour for a list, ready for an inline style. */
export function tintOf(value: string | null): string {
  return `var(--tint-${colorOf(value)})`;
}

/**
 * The tint at card strength, for the square an icon sits in. An emoji renders
 * in its own colours whatever `color` says, so the tint has to be the
 * background here - the same thing the family shell does with `--tint-bg`.
 */
export function tintBackgroundOf(value: string | null): string {
  return `color-mix(in srgb, ${tintOf(value)} 16%, transparent)`;
}

/** Icons are emoji, chosen on a phone from a fixed set; this is that set's default. */
export const DEFAULT_LIST_ICON = '🧺';
