import { Button, Modal } from '@aerie/ui';
import type { CloseOffer } from '../lib/closeSubtree';
import type { IssueBulkFailure } from '../types';

/**
 * The offer to carry a subtree along with the issue above it - into a terminal
 * column, or onto the shelf.
 *
 * One dialog for both because it is one decision with two words for it, and the
 * words are the whole difference: closing says the work is finished, deferring
 * says it is parked. `offer.column.isDeferred` picks between them, so a board
 * with three deferred columns needs nothing added here.
 *
 * It is asked *after* the parent has already moved, and it decides only what
 * happens to what is under it - which is why the two answers are "move them
 * too" and "leave them", and why neither of them is "undo that". Dismissing it
 * with escape, the scrim or the ✕ means the same thing as pressing Leave them.
 *
 * Taking the subtree with it is the press the dialog leads with, and that is
 * the recommendation rather than a default that fires on its own: a stack of
 * tickets is one piece of work, and the ordinary answer for both flavours is
 * that all of it moves together.
 *
 * Everything it needs was already on screen: `closeOffer` read the subtree off
 * the board the browser was holding, so the dialog opens without a request and
 * lists exactly the keys the cascade will name.
 *
 * Presentational, like IssuePeek - the fetching, the errors and the busy flag
 * are lib/useCloseSubtree.ts's, because two screens open this dialog.
 */
export function CloseSubtreeDialog({
  offer,
  busy,
  error,
  failures,
  onConfirm,
  onClose,
}: {
  offer: CloseOffer | null;
  /** The cascade is in flight: the confirm stops taking presses and says so. */
  busy: boolean;
  error: string | null;
  /** The keys the server refused, each with its reason. */
  failures: IssueBulkFailure[];
  onConfirm: () => void;
  onClose: () => void;
}) {
  // Rendered unconditionally so the modal's own focus, escape and scrim
  // handling is the one that runs - see IssuePeek for the same shape.
  if (!offer) return <Modal open={false} onClose={onClose} title="" />;

  const count = offer.cards.length;
  const deferring = offer.column.isDeferred;
  const verb = deferring ? 'Defer' : 'Close';
  const them = count === 1 ? 'it' : 'them';

  return (
    <Modal open onClose={onClose} title={`${verb} what is under ${offer.key}?`}>
      <div className="hatch-cascade">
        <p>
          <code>{offer.key}</code> is in {offer.column.name}.{' '}
          {count === 1 ? 'One issue under it is' : `${count} issues under it are`} still open.{' '}
          {deferring ? 'Shelve' : 'Close'} {them} too?
        </p>

        <ul className="hatch-cascade-list">
          {offer.cards.map((card) => (
            <li key={card.key}>
              <code>{card.key}</code> — {card.title}
            </li>
          ))}
        </ul>

        {error && <p className="text-danger">{error}</p>}

        {failures.length > 0 && (
          <ul className="hatch-bulk-failures">
            {failures.map((failure) => (
              <li key={failure.key}>
                <code>{failure.key}</code> — {failure.reason}
              </li>
            ))}
          </ul>
        )}

        <div className="hatch-form-actions">
          <Button onClick={onClose}>Leave {them} on the board</Button>
          <Button variant="primary" loading={busy} onClick={onConfirm}>
            {verb} {them} too
          </Button>
        </div>
      </div>
    </Modal>
  );
}
