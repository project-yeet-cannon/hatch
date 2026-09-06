import { Button, Modal } from '@aerie/ui';
import type { CloseOffer } from '../lib/closeSubtree';
import type { IssueBulkFailure } from '../types';

/**
 * The offer to close everything under an issue that was just closed.
 *
 * It is asked *after* the parent has already moved, and it decides only what
 * happens to what is under it - which is why the two answers are "close them
 * too" and "leave them", and why neither of them is "undo that". Dismissing it
 * with escape, the scrim or the ✕ means the same thing as pressing Leave them.
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

  return (
    <Modal open onClose={onClose} title={`Close what is under ${offer.key}?`}>
      <div className="hatch-cascade">
        <p>
          <code>{offer.key}</code> is in {offer.column.name}.{' '}
          {count === 1 ? 'One issue under it is' : `${count} issues under it are`} still open. Close{' '}
          {count === 1 ? 'it' : 'them'} too?
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
          <Button onClick={onClose}>Leave them</Button>
          <Button variant="primary" loading={busy} onClick={onConfirm}>
            Close {count === 1 ? 'it' : 'them'} too
          </Button>
        </div>
      </div>
    </Modal>
  );
}
