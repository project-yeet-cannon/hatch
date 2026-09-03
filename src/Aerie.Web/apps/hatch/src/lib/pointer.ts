/* Which clicks are the app's, and which belong to the browser.

   A board card is an <a> as well as a draggable, because an issue key is meant
   to be passed around and copy-link, middle-click and open-in-new-tab are how
   that is done. Left-clicking one opens a summary instead of navigating - so
   this is the line between the two, in one place, testable, rather than a
   condition written slightly differently at each call site. */

/** The parts of a mouse event this decision reads. A plain object in a test, a React event in the app. */
export interface ClickIntent {
  button?: number;
  metaKey?: boolean;
  ctrlKey?: boolean;
  shiftKey?: boolean;
  altKey?: boolean;
}

/**
 * Whether this click is the plain one the app may take over.
 *
 * Anything with a modifier held, or from a button other than the primary one,
 * is a gesture aimed at the link itself - cmd/ctrl for a new tab, shift for a
 * new window, middle-click for either depending on the platform - and stays the
 * browser's to handle.
 */
export const isPlainClick = (event: ClickIntent): boolean =>
  (event.button ?? 0) === 0 && !event.metaKey && !event.ctrlKey && !event.shiftKey && !event.altKey;
