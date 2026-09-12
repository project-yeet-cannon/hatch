/* What an issue may be made to wait on.

   One rule, with an argument in it, and therefore a module rather than a
   handful of filters inside IssuePage - the reasoning lib/closeSubtree.ts
   gives applies verbatim. There is no DOM in the web test run, so a decision
   that lives in a component is a decision nobody tests; what is left at the
   call site is wiring with no branch in it.

   The server refuses each of these too, in its own sentences, and that is the
   guard - this is the picker not offering what would come back refused. Two
   statements of one rule is the usual risk and it is worth taking here: a list
   that offers a row and then rejects the press reads as broken. */

import type { IssueCard } from '../types';

/**
 * The cards this issue may be made to wait on: every card except itself, its
 * ancestors, its descendants, and whatever it already waits on.
 *
 * Ancestors and descendants both go, and for one reason: a parent is not done
 * until its work is, so an edge either way round between an issue and its own
 * line names a dependency that could never be satisfied.
 *
 * Not filtered by project. An edge may cross one where a parent may not -
 * containment and ordering are different claims, and two efforts routinely
 * have to land in order.
 */
export function dependencyCandidates(
  cards: IssueCard[],
  key: string,
  dependsOnKeys: string[],
): IssueCard[] {
  const excluded = new Set<string>([key, ...dependsOnKeys]);

  const byKey = new Map(cards.map((card) => [card.key, card]));

  // Up. The loop guard is not decoration: parenting refuses to close a loop,
  // but one that got in some other way - a restored backup, a hand-written
  // UPDATE - must produce a wrong answer rather than spin the browser.
  const climbed = new Set<string>([key]);
  let at = byKey.get(key)?.parentKey ?? null;
  while (at !== null && !climbed.has(at)) {
    climbed.add(at);
    excluded.add(at);
    at = byKey.get(at)?.parentKey ?? null;
  }

  // Down: closeSubtree's generation walk, with the same guard for the same
  // reason.
  const children = new Map<string, IssueCard[]>();
  for (const card of cards) {
    if (card.parentKey === null) continue;
    const siblings = children.get(card.parentKey);
    if (siblings) siblings.push(card);
    else children.set(card.parentKey, [card]);
  }

  const seen = new Set<string>([key]);
  let generation = [key];
  while (generation.length > 0) {
    const next: string[] = [];
    for (const parent of generation)
      for (const child of children.get(parent) ?? [])
        if (!seen.has(child.key)) {
          seen.add(child.key);
          excluded.add(child.key);
          next.push(child.key);
        }
    generation = next;
  }

  return cards.filter((card) => !excluded.has(card.key));
}
