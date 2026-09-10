/**
 * The stack of "you filed this" chicklets in the corner, as data.
 *
 * A confirmation is about what *this* visit did, so the stack is state in the
 * page and nothing else: nothing here reads or writes localStorage or
 * sessionStorage, which is what makes a reload - and a second tab, and a
 * restarted browser - start on an empty corner without a line of code spent
 * clearing anything.
 */

export interface Confirmation {
  /** Unique per raise, so React has a stable key and filing under a key that
      is already on screen cannot collide with the older chicklet. */
  id: number;
  issueKey: string;
  title: string;
}

/**
 * A newly filed issue, on the front of the stack.
 *
 * Newest first, because the region paints in `column-reverse`: index 0 is the
 * chicklet nearest the bottom-left corner, and the older ones run upward from
 * it. Nothing is dropped and there is no cap - a stack too tall for the screen
 * is answered with a scrollbar rather than by quietly discarding the
 * confirmation somebody has not read yet.
 */
export function raise(stack: Confirmation[], issue: { key: string; title: string }, id: number): Confirmation[] {
  return [{ id, issueKey: issue.key, title: issue.title }, ...stack];
}

/** One chicklet closed, and every other one left exactly where it was. An id
    that is not in the stack takes nothing off it. */
export function dismiss(stack: Confirmation[], id: number): Confirmation[] {
  return stack.filter((c) => c.id !== id);
}
