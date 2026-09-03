import { Link } from 'react-router-dom';
import { Button, Modal } from '@aerie/ui';
import { MomentChip } from './MomentChip';
import { StatusPill } from './StatusPill';
import { TypeBadge } from './TypeBadge';
import { appHref } from '../lib/basename';
import type { IssueCard, Status } from '../types';

/**
 * What a card says when you click it, without asking the API anything.
 *
 * Everything here came down with the board (IssueCardDto) - key, type, title,
 * column, parent, dates - so opening one is instant and closing it costs
 * nothing. The description, the comments and the history are deliberately not
 * fetched: they are the reason the issue page exists, and a dialog that quietly
 * loaded them would turn every idle click on the board into three requests.
 *
 * So the dialog answers "which one is this" and hands over two ways to go
 * further: the full issue in this tab, or in a new one.
 */
export function IssuePeek({
  card,
  status,
  onClose,
}: {
  card: IssueCard | null;
  /** The column it is sitting in, or undefined if the board has moved underneath. */
  status?: Status;
  onClose: () => void;
}) {
  // Rendered unconditionally so the dialog's own open/closed handling - focus,
  // escape, the scrim - is the one that runs. Its title needs a card, though,
  // so a closed peek has nothing to say.
  if (!card) return <Modal open={false} onClose={onClose} title="" />;

  const terminal = status?.isTerminal ?? false;

  return (
    <Modal open onClose={onClose} title={card.key}>
      <div className="hatch-peek">
        <div className="hatch-peek-meta">
          <TypeBadge type={card.type} />
          {status && <StatusPill status={status} />}
          {card.parentKey && (
            <Link to={`/issues/${card.parentKey}`} onClick={onClose}>
              ↳ {card.parentKey}
            </Link>
          )}
        </div>

        <p className="hatch-peek-title">{card.title}</p>

        {(card.readyAt || card.dueAt) && (
          <div className="hatch-card-dates">
            <MomentChip kind="ready" value={card.readyAt} />
            <MomentChip kind="due" value={card.dueAt} muted={terminal} />
          </div>
        )}

        <p className="text-muted">
          The description, the comments and the history live on the issue itself - this is only what the board
          already knew.
        </p>

        <div className="hatch-form-actions">
          <Button onClick={onClose}>Close</Button>
          {/* An anchor rather than a <Link>: target="_blank" opens a second
              document, which React Router does not route. appHref is what keeps
              that second document on the right prefix - see lib/basename.ts. */}
          <Button as="a" href={appHref(`/issues/${card.key}`)} target="_blank" rel="noreferrer">
            New tab ↗
          </Button>
          <Button as={Link} variant="primary" to={`/issues/${card.key}`} onClick={onClose}>
            Open the issue
          </Button>
        </div>
      </div>
    </Modal>
  );
}
