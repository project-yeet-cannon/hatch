import { Link } from 'react-router-dom';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { TypeBadge } from './TypeBadge';
import { MomentChip } from './MomentChip';
import type { IssueCard } from '../types';

/**
 * One card. A <Link> as well as a draggable, so middle-click, copy-link and
 * open-in-new-tab all work - an issue key is meant to be passed around, and a
 * div with an onClick would make the one gesture that matters impossible.
 *
 * The pointer sensor on the board requires a few pixels of movement before a
 * drag starts, which is what lets the same element be both.
 */
export function BoardCard({
  card,
  waiting = false,
  terminal = false,
}: {
  card: IssueCard;
  /** Its ready date has not arrived. Shown only when a column's fold is open. */
  waiting?: boolean;
  /** In a column that means the work shipped, where a due date has nothing left to warn about. */
  terminal?: boolean;
}) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: card.key });

  return (
    <Link
      ref={setNodeRef}
      to={`/issues/${card.key}`}
      className={`hatch-card${isDragging ? ' dragging' : ''}${waiting ? ' waiting' : ''}`}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      {...attributes}
      {...listeners}
    >
      <div className="hatch-card-head">
        <span className="hatch-card-key">{card.key}</span>
        <TypeBadge type={card.type} />
      </div>
      <div className="hatch-card-title">{card.title}</div>
      {card.parentKey && <div className="hatch-card-parent">↳ {card.parentKey}</div>}
      {(card.dueAt || waiting) && (
        <div className="hatch-card-dates">
          {/* Only a waiting card shows its ready date. Once it has passed it has
              nothing left to say, and every card on the board would carry one. */}
          {waiting && <MomentChip kind="ready" value={card.readyAt} />}
          <MomentChip kind="due" value={card.dueAt} muted={terminal} />
        </div>
      )}
    </Link>
  );
}
