/**
 * The app-switcher's artwork, alone in a file on purpose.
 *
 * It replaces the `▦` text glyph admin used to render. That glyph was drawn by
 * whichever face the machine resolved, at whatever weight that face happened to
 * cut it, and it carried no meaning to a screen reader - it was a character
 * standing in for a picture. This is the same idea drawn as a picture: the
 * four-pane grid that every platform uses for "all apps", so the control reads
 * as one at a glance.
 *
 * `currentColor` throughout, so the mark takes the ink of whatever surface it
 * is placed on and needs no answer of its own for dark.
 *
 * **The artwork itself is Phase 6's.** It lives in its own file so that
 * replacing it is one file, with no hunting through a component that also
 * carries a hit target, a hover state and a focus ring.
 */
export function AppsMark({ className }: { className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 24 24"
      width="20"
      height="20"
      fill="currentColor"
      /* Decoration: the control around it carries the accessible name, and a
         mark that also announced itself would say the same thing twice.
         `focusable` is for IE-era Edge, which put SVGs in the tab order. */
      aria-hidden="true"
      focusable="false"
    >
      <rect x="3" y="3" width="8" height="8" rx="2" />
      <rect x="13" y="3" width="8" height="8" rx="2" />
      <rect x="3" y="13" width="8" height="8" rx="2" />
      <rect x="13" y="13" width="8" height="8" rx="2" />
    </svg>
  );
}
