/* A textarea as tall as what is in it.

   A fixed `rows` is a bet that nobody will write more than that, and the
   description editor is exactly where that bet loses: the box is where a brief
   gets written, and a brief is longer than a screen. The old answer was the
   resize handle, dragged once when the text passed the bottom and again when it
   passed the bottom of the new size.

   This is the ref-and-measure form rather than CSS `field-sizing: content`,
   which is three lines and no JavaScript but is not in every browser the house
   is read in yet - and the fallback there is silently the old fixed height, so
   the bug would look fixed on the machine it was written on.

   What lives here is the height. What lives in CSS (`.hatch-grows`) is the
   range: the cap the box stops growing at, expressed against the screen. The
   floor is measured rather than declared - see below. */

import { useLayoutEffect, useRef } from 'react';

/**
 * The height a box should take: what its content needs, but never less than
 * the height it opened at.
 *
 * `content` is a textarea's `scrollHeight`, which covers the text and the
 * padding but not the border; `chrome` is the border, added back because the
 * house is `box-sizing: border-box` and `style.height` is therefore the
 * outside of the box. Getting that wrong is a two-pixel scrollbar that never
 * goes away.
 */
export function grownHeight(floor: number, content: number, chrome: number): number {
  return Math.max(floor, content + chrome);
}

/** The box this one scrolls inside, whose position must survive a measurement. */
function scrollParent(el: HTMLElement): HTMLElement | null {
  for (let node = el.parentElement; node; node = node.parentElement) {
    const overflowY = getComputedStyle(node).overflowY;
    if (overflowY === 'auto' || overflowY === 'scroll') return node;
  }
  return null;
}

/**
 * Keeps a textarea's height matched to its value. Returns the ref to hang on
 * it; pass the value it is showing, which is what says when to measure again.
 *
 * Three things it is careful about, each of them a way this pattern is usually
 * got wrong:
 *
 * - **The floor is measured, not declared.** Every pass clears the height
 *   first, which puts the box back to what `rows` and the stylesheet say and is
 *   also the only state in which `scrollHeight` reports what the content
 *   actually needs. Whatever that comes to - after the webfont lands, at
 *   whatever zoom - is the height the box opened at, and it never goes under it.
 * - **The scroll position survives that.** Collapsing the box for a moment
 *   shortens the page it sits in, and a browser will clamp a scroll box that
 *   suddenly has less to scroll. Left alone, typing near the bottom of a long
 *   description would walk the page upwards. Nothing paints between the two, so
 *   putting the offset back is enough.
 * - **A dragged handle wins.** The height an operator chose by hand is an
 *   answer, not a stale measurement: once the inline height is not the one this
 *   wrote, it stops writing. Toggling back to preview and into the editor again
 *   is a new box, and starts over.
 */
export function useAutoGrow(value: string) {
  const ref = useRef<HTMLTextAreaElement>(null);
  // Per element rather than per hook: the editor unmounts on every preview
  // toggle, and what was true of the last box is not true of this one.
  const seen = useRef<{ el: HTMLTextAreaElement; applied: string; dragged: boolean } | null>(null);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;

    let state = seen.current;
    if (!state || state.el !== el) {
      state = { el, applied: '', dragged: false };
      seen.current = state;
    }
    if (state.dragged) return;
    if (state.applied && el.style.height !== state.applied) {
      state.dragged = true;
      return;
    }

    const scroller = scrollParent(el);
    const scrollTop = scroller?.scrollTop;

    el.style.height = '';
    const height = `${grownHeight(el.offsetHeight, el.scrollHeight, el.offsetHeight - el.clientHeight)}px`;
    el.style.height = height;
    state.applied = height;

    if (scroller && scrollTop !== undefined && scroller.scrollTop !== scrollTop) scroller.scrollTop = scrollTop;
  }, [value]);

  return ref;
}
