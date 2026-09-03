import { useCallback, useEffect, useState } from 'react';
import {
  DndContext,
  KeyboardSensor,
  PointerSensor,
  closestCorners,
  useDroppable,
  useSensor,
  useSensors,
} from '@dnd-kit/core';
import type { DragEndEvent } from '@dnd-kit/core';
import { SortableContext, arrayMove, sortableKeyboardCoordinates, verticalListSortingStrategy } from '@dnd-kit/sortable';
import { Button, EmptyState, PageHeader } from '@aerie/ui';
import { getBoard, getProjects, moveIssue } from '../api/client';
import { BoardCard } from '../components/BoardCard';
import { NewIssueDialog } from '../components/NewIssueDialog';
import { message } from '../lib/errors';
import { isWaiting } from '../lib/schedule';
import { useLoaded } from '../lib/useLoaded';
import type { Board, IssueCard, Project, Status } from '../types';

/** Prefixes a column's droppable id, so an empty column is still a drop target. */
const COLUMN = 'column:';

export function BoardPage() {
  const { data: board, setData: setBoard, error, setError, reload } = useLoaded<Board>(getBoard);
  const [projects, setProjects] = useState<Project[]>([]);
  const [filing, setFiling] = useState(false);

  useEffect(() => {
    getProjects().then(setProjects).catch(() => {
      // The board is readable without the project list; only the New issue
      // dialog needs it, and it says so itself when there is nothing to pick.
    });
  }, []);

  // A few pixels before a drag begins, so the same element can be a link and a
  // card - tapping one opens the issue, dragging one moves it.
  const sensors = useSensors(
    useSensor(PointerSensor, { activationConstraint: { distance: 5 } }),
    useSensor(KeyboardSensor, { coordinateGetter: sortableKeyboardCoordinates }),
  );

  const onDragEnd = useCallback(
    async (event: DragEndEvent) => {
      if (!board) return;

      const placed = place(board.issues, event);
      if (!placed) return;

      // Applied before the request so the card does not spring back under the
      // cursor for a round trip. A refusal reloads, which is the honest
      // correction: whatever the server thinks is what the board shows.
      setBoard({ ...board, issues: placed.issues });

      try {
        await moveIssue(placed.key, {
          statusId: placed.statusId,
          afterKey: placed.afterKey,
          beforeKey: placed.beforeKey,
        });
        await reload();
      } catch (err) {
        setError(message(err));
        await reload();
      }
    },
    [board, setBoard, setError, reload],
  );

  if (error && !board) return <p className="text-danger">{error}</p>;
  if (!board) return <p className="text-muted">Loading…</p>;

  return (
    <div className="hatch-board-page">
      <PageHeader
        title="Board"
        actions={
          <Button variant="primary" onClick={() => setFiling(true)}>
            New issue
          </Button>
        }
      />

      {error && <p className="text-danger">{error}</p>}

      {board.statuses.length === 0 ? (
        <EmptyState message="This board has no columns yet." />
      ) : (
        <DndContext sensors={sensors} collisionDetection={closestCorners} onDragEnd={(e) => void onDragEnd(e)}>
          <div className="hatch-board">
            {board.statuses.map((status) => (
              <Column key={status.id} status={status} cards={board.issues.filter((i) => i.statusId === status.id)} />
            ))}
          </div>
        </DndContext>
      )}

      <NewIssueDialog
        open={filing}
        projects={projects}
        onClose={() => setFiling(false)}
        onCreated={() => void reload()}
      />
    </div>
  );
}

/**
 * One column, and the fold that keeps it honest.
 *
 * An issue whose ready date has not arrived is real work that cannot be started
 * yet - next August's certificate renewal, filed the day the certificate was
 * bought. Left in place it pushes this week's work off the screen; deleted from
 * the view entirely it becomes a ticket nobody can find, which is a ticket
 * somebody files twice. So it is folded: counted in the header, one click away.
 *
 * The fold is the browser's alone. The API hands over every card (BoardDto), so
 * a script - or Claude - sees the whole board and does its own filtering.
 */
function Column({ status, cards }: { status: Status; cards: IssueCard[] }) {
  const [showWaiting, setShowWaiting] = useState(false);

  // Its own droppable as well as a sortable context: a column with nothing in
  // it has no card to drop onto, and "move this to done" is exactly the drag
  // where done is empty.
  const { setNodeRef, isOver } = useDroppable({ id: `${COLUMN}${status.id}` });

  // Read once per render rather than per card, so a column cannot straddle
  // midnight and draw two different todays.
  const now = new Date();

  // Nothing is folded away in a terminal column. Work that shipped shipped,
  // whatever date was once on it, and hiding a done card behind "not ready yet"
  // would be the board arguing with what the operator just did.
  const waiting = status.isTerminal ? [] : cards.filter((c) => isWaiting(c.readyAt, now));
  const workable = cards.filter((c) => !waiting.includes(c));
  const shown = showWaiting ? [...workable, ...waiting] : workable;

  return (
    <section className={`hatch-column${isOver ? ' over' : ''}`}>
      <header className="hatch-column-head">
        <span className="hatch-column-name">{status.name}</span>
        <span className="hatch-column-count">{workable.length}</span>
      </header>

      <div className="hatch-column-cards" ref={setNodeRef}>
        {/* Only the cards actually drawn: dnd-kit sorts the ids it is given, and
            an id with nothing on screen behind it is a gap a drag falls into. */}
        <SortableContext items={shown.map((c) => c.key)} strategy={verticalListSortingStrategy}>
          {shown.map((card) => (
            <BoardCard
              key={card.key}
              card={card}
              waiting={waiting.includes(card)}
              terminal={status.isTerminal}
            />
          ))}
        </SortableContext>

        {waiting.length > 0 && (
          <button type="button" className="hatch-column-fold" onClick={() => setShowWaiting(!showWaiting)}>
            {showWaiting ? 'Hide' : `+ ${waiting.length}`} waiting
          </button>
        )}
      </div>
    </section>
  );
}

/**
 * Where the drop landed: the moving card's new column, its two neighbours, and
 * the reordered card list to paint immediately.
 *
 * The neighbours are what goes to the server - it owns the rank
 * (docs/plans/pjm.md, "Rank computation") - so this function's whole job is to
 * turn "dropped on that card" into "between these two".
 */
function place(
  issues: IssueCard[],
  { active, over }: DragEndEvent,
): { key: string; statusId: number; afterKey: string | null; beforeKey: string | null; issues: IssueCard[] } | null {
  if (!over) return null;

  const key = String(active.id);
  const card = issues.find((i) => i.key === key);
  if (!card) return null;

  const overId = String(over.id);
  const overCard = overId.startsWith(COLUMN) ? undefined : issues.find((i) => i.key === overId);
  const statusId = overId.startsWith(COLUMN) ? Number(overId.slice(COLUMN.length)) : overCard?.statusId;
  if (statusId === undefined || Number.isNaN(statusId)) return null;

  const column = issues.filter((i) => i.statusId === statusId);

  let next: IssueCard[];
  if (statusId === card.statusId) {
    const from = column.findIndex((i) => i.key === key);
    const to = overCard ? column.findIndex((i) => i.key === overCard.key) : column.length - 1;
    if (from < 0 || to < 0 || from === to) return null;
    next = arrayMove(column, from, to);
  } else {
    const at = overCard ? column.findIndex((i) => i.key === overCard.key) : column.length;
    const moved = { ...card, statusId };
    next = [...column.slice(0, at < 0 ? column.length : at), moved, ...column.slice(at < 0 ? column.length : at)];
  }

  const at = next.findIndex((i) => i.key === key);
  const rest = issues.filter((i) => i.statusId !== statusId && i.key !== key);

  return {
    key,
    statusId,
    afterKey: at > 0 ? next[at - 1].key : null,
    beforeKey: at < next.length - 1 ? next[at + 1].key : null,
    issues: [...rest, ...next],
  };
}
