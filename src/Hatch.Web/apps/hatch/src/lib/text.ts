/* Cutting text to a length a card can hold. */

/**
 * How much of a title a board card draws before it stops. A few hundred
 * characters: long enough that an ordinary title arrives whole, short enough
 * that one issue somebody pasted a paragraph into cannot make its column twice
 * the height of every other.
 *
 * The card also clamps its height in CSS, which is what keeps the drawn card
 * uniform. This is the belt to that pair of braces: a clamp hides overflow, and
 * an unbounded string still ends up in the DOM, in the tooltip, and in the drag
 * preview.
 */
export const CARD_TEXT_MAX = 240;

/**
 * `text`, no longer than `max`, ending in an ellipsis if anything was dropped.
 *
 * Cut at the last space before the limit when there is one reasonably close, so
 * the result ends on a word rather than mid-syllable; hard-cut otherwise,
 * because a "word" 200 characters long is a URL or a hash and there is nothing
 * to be polite about.
 */
export function truncate(text: string, max: number = CARD_TEXT_MAX): string {
  if (text.length <= max) return text;

  const cut = text.slice(0, max);
  const space = cut.lastIndexOf(' ');

  // Two thirds: far enough back to still be most of the allowance, close enough
  // that a run of long tokens does not throw away half the text to find a gap.
  return `${(space > max * 0.667 ? cut.slice(0, space) : cut).trimEnd()}…`;
}
