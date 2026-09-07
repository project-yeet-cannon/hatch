import { Link } from 'react-router-dom';
import { useSortable } from '@dnd-kit/sortable';
import { CSS } from '@dnd-kit/utilities';
import { TypeBadge } from './TypeBadge';
import { MomentChip } from './MomentChip';
import { isPlainClick } from '../lib/pointer';
import { truncate } from '../lib/text';
import type { IssueCard } from '../types';

export interface CardProps {
  card: IssueCard;
  /** Its ready date has not arrived. Shown only when a column's fold is open. */
  waiting?: boolean;
  /** In a column that means the work shipped, where a due date has nothing left to warn about. */
  terminal?: boolean;
}

/** Owed an answer, and drawn as such wherever the card is drawn. */
const askingClass = (card: IssueCard) => (card.openQuestions > 0 ? ' asking' : '');

/**
 * One card. A <Link> as well as a draggable, so middle-click, copy-link and
 * open-in-new-tab all work - an issue key is meant to be passed around, and a
 * div with an onClick would make the one gesture that matters impossible.
 *
 * A plain left click opens the summary instead of navigating (see
 * lib/pointer.ts for where that line is drawn), because the question a board
 * raises is usually "what is this one" rather than "take me to it" - and
 * answering it without leaving the board is the difference between a glance and
 * a round trip.
 *
 * The pointer sensor on the board requires a few pixels of movement before a
 * drag starts, which is what lets the same element be a link, a card and a
 * handle at once.
 */
export function BoardCard({
  card,
  waiting = false,
  terminal = false,
  onPeek,
}: CardProps & { onPeek: (card: IssueCard) => void }) {
  const { attributes, listeners, setNodeRef, transform, transition, isDragging } = useSortable({ id: card.key });

  return (
    <Link
      ref={setNodeRef}
      to={`/issues/${card.key}`}
      className={`hatch-card${isDragging ? ' dragging' : ''}${waiting ? ' waiting' : ''}${askingClass(card)}`}
      style={{ transform: CSS.Transform.toString(transform), transition }}
      onClick={(e) => {
        if (!isPlainClick(e)) return;
        e.preventDefault();
        onPeek(card);
      }}
      {...attributes}
      {...listeners}
    >
      <CardFace card={card} waiting={waiting} terminal={terminal} />
    </Link>
  );
}

/**
 * The card that follows the cursor during a drag.
 *
 * Without one, dnd-kit leaves the original card in place at 40% opacity and
 * nothing moves under the hand - the board looked, correctly, like it had not
 * understood the gesture. This is the same face, drawn in an overlay layer that
 * is positioned by the cursor rather than by the column it came from.
 */
export function CardPreview({ card, waiting = false, terminal = false }: CardProps) {
  return (
    <div className={`hatch-card hatch-card-preview${askingClass(card)}`}>
      <CardFace card={card} waiting={waiting} terminal={terminal} />
    </div>
  );
}

/**
 * What is drawn on a card, wherever it is drawn.
 *
 * The title is cut twice over: to a fixed number of characters here, and to a
 * fixed number of lines in CSS. The second is what makes every card the same
 * height; the first is what keeps a pasted paragraph out of the DOM, the
 * tooltip, and the drag preview even though the clamp would have hidden it.
 */
function CardFace({ card, waiting, terminal }: Required<Omit<CardProps, 'card'>> & { card: IssueCard }) {
  return (
    <>
      <div className="hatch-card-head">
        <span className="hatch-card-key">{card.key}</span>
        <TypeBadge type={card.type} />
        {/* Drawn on the card and not only on the issue page, because a question
            nobody can see is a question nobody answers - and this card is the
            reason the column below it has stopped moving. */}
        {card.openQuestions > 0 && (
          <span
            className="hatch-card-asking"
            title={`${card.openQuestions} unanswered question${card.openQuestions === 1 ? '' : 's'}`}
          >
            ?{card.openQuestions > 1 && <span className="hatch-card-asking-count">{card.openQuestions}</span>}
          </span>
        )}
        {/* Nothing at all where there is no assignee - not an empty chip and
            not a dash. Most of the board owns nothing, and a placeholder on
            every card would be noise the eye has to skip past to find the
            three that do. */}
        {card.assignee && (
          <span
            className="hatch-card-assignee"
            title={
              card.assignee.kind === 'person'
                ? `Assigned to ${card.assignee.name} - an unattended pass leaves it alone`
                : `Assigned to the API key ${card.assignee.name}`
            }
          >
            {card.assignee.name}
          </span>
        )}
      </div>
      <div className="hatch-card-title" title={card.title}>
        {truncate(card.title)}
      </div>
      {card.parentKey && <div className="hatch-card-parent">↳ {card.parentKey}</div>}
      {(card.dueAt || waiting) && (
        <div className="hatch-card-dates">
          {/* Only a waiting card shows its ready date. Once it has passed it has
              nothing left to say, and every card on the board would carry one. */}
          {waiting && <MomentChip kind="ready" value={card.readyAt} />}
          <MomentChip kind="due" value={card.dueAt} muted={terminal} />
        </div>
      )}
    </>
  );
}
