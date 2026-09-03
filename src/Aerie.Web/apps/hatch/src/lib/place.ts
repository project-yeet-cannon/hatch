/* Where a dragged card landed.

   The board hands the server two neighbours and never a rank - the server owns
   the number (docs/plans/pjm.md, "Rank computation") - so this module's whole
   job is to turn "dropped on that card" into "between these two", and to
   produce the reordered list to paint before the request comes back.

   It is a module rather than a function inside BoardPage for one reason: the
   board can be filtered now, and a drop has to be read against the cards the
   operator can actually see. "Put it under that one" means under the card on
   screen, not under whatever hidden row happens to sit at the same index. That
   distinction has no visible symptom when it is wrong - the card simply lands
   somewhere slightly surprising - which is exactly the kind of thing worth
   having tests for. */

import type { IssueCard } from '../types';

/** Prefixes a column's droppable id, so an empty column is still a drop target. */
export const COLUMN = 'column:';

export const columnDroppableId = (statusId: number): string => `${COLUMN}${statusId}`;

export interface Placement {
  key: string;
  statusId: number;
  /** The card immediately above the drop, or null at the top of the column. */
  afterKey: string | null;
  /** The card immediately below it, or null at the bottom. */
  beforeKey: string | null;
  /** Every card on the board, reordered - what the browser paints while the request is in flight. */
  issues: IssueCard[];
}

/**
 * Which column something is being dragged over: the column itself when the
 * cursor is over empty space in one, or the column of the card under it.
 *
 * Also what the board highlights during a drag, which is the other reason this
 * is separate from `place` - the highlight has to update on every move, and
 * nothing about it should compute a placement.
 */
export function targetStatusId(overId: string | null | undefined, cards: IssueCard[]): number | null {
  if (!overId) return null;

  if (overId.startsWith(COLUMN)) {
    const id = Number(overId.slice(COLUMN.length));
    return Number.isFinite(id) ? id : null;
  }

  return cards.find((card) => card.key === overId)?.statusId ?? null;
}

/**
 * Reads a drop.
 *
 * @param all Every card on the board, in board order (grouped by column, ranked within it).
 * @param visible The cards the filter is currently drawing - the ones a drop is aimed at.
 * @returns The placement, or null when the drop changed nothing.
 */
export function place(
  all: IssueCard[],
  visible: IssueCard[],
  activeId: string,
  overId: string | null | undefined,
): Placement | null {
  const card = all.find((i) => i.key === activeId);
  if (!card) return null;

  const statusId = targetStatusId(overId, all);
  if (statusId === null) return null;

  const onCard = !!overId && !overId.startsWith(COLUMN);
  const overCard = onCard ? all.find((i) => i.key === overId) ?? null : null;
  if (onCard && !overCard) return null;

  // Dropped on itself, which is what dnd-kit reports for most of a drag that
  // has not left home yet. Nothing moved.
  if (overCard?.key === activeId) return null;

  // The target column as the operator sees it, with the moving card lifted out
  // of it - every index below is an index into this list.
  const column = visible.filter((i) => i.statusId === statusId && i.key !== activeId);
  const from = visible.filter((i) => i.statusId === statusId).findIndex((i) => i.key === activeId);

  let at: number;
  if (overCard) {
    const over = column.findIndex((i) => i.key === overCard.key);
    if (over < 0) return null;

    // Dropped on a card: it takes that card's place, which means landing below
    // it when it came from above and above it otherwise - the gesture reads as
    // pushing the other card out of the way in the direction of travel.
    at = card.statusId === statusId && from >= 0 && from < over + 1 ? over + 1 : over;
  } else {
    // Dropped on the column rather than on a card: the bottom of it.
    at = column.length;
  }

  // Same column, same slot. Nothing moved, and reporting a move would cost a
  // request and a repaint to arrive back where it started.
  if (card.statusId === statusId && at === from) return null;

  const afterKey = at > 0 ? column[at - 1].key : null;
  const beforeKey = at < column.length ? column[at].key : null;

  return { key: activeId, statusId, afterKey, beforeKey, issues: reorder(all, card, statusId, afterKey, beforeKey) };
}

/**
 * The board as it will look, applied optimistically so the card does not spring
 * back under the cursor for a round trip.
 *
 * Positioned by its neighbours' keys rather than by an index, which is what
 * keeps it right while a filter is on: the index the operator dropped at is an
 * index into what they could see, and the array being rebuilt here holds
 * everything.
 */
function reorder(
  all: IssueCard[],
  card: IssueCard,
  statusId: number,
  afterKey: string | null,
  beforeKey: string | null,
): IssueCard[] {
  const moved = { ...card, statusId };
  const rest = all.filter((i) => i.key !== card.key);

  const anchor = afterKey ?? beforeKey;
  if (!anchor) return [...rest, moved];

  const at = rest.findIndex((i) => i.key === anchor);
  if (at < 0) return [...rest, moved];

  const insert = afterKey ? at + 1 : at;
  return [...rest.slice(0, insert), moved, ...rest.slice(insert)];
}
