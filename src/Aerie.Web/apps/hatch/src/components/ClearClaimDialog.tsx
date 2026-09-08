import { Button, Modal } from '@aerie/ui';
import { agoPhrase } from '../lib/claim';
import type { IssueClaim } from '../types';

/**
 * The speed bump in front of taking a ticket back off a runner.
 *
 * It exists for one sentence, and the sentence is the consequence rather than
 * "are you sure": **clearing the claim does not stop the runner.** The lease is
 * a lock on the board, not a handle on a process - the session keeps running,
 * keeps pushing, and may still move this ticket, because a move is not gated on
 * a token. What clearing does is make the ticket claimable again, so the next
 * pass is free to spawn a second session at it, and then two of them are
 * writing to one branch. That is worth knowing before pressing, and nowhere
 * else on the page says it.
 *
 * Presentational, like CloseSubtreeDialog, and rendered unconditionally for the
 * same reason: the modal's own focus, escape and scrim handling is the one that
 * should run. Dismissing by escape, the scrim or the ✕ means Leave it.
 */
export function ClearClaimDialog({
  issueKey,
  claim,
  busy,
  onConfirm,
  onClose,
}: {
  issueKey: string;
  /** The claim being cut off, or null when the dialog is shut. */
  claim: IssueClaim | null;
  /** The clear is in flight: the confirm stops taking presses and says so. */
  busy: boolean;
  onConfirm: () => void;
  onClose: () => void;
}) {
  if (!claim) return <Modal open={false} onClose={onClose} title="" />;

  return (
    <Modal open onClose={onClose} title={`Clear the claim on ${issueKey}?`}>
      <div className="hatch-claim-clear">
        <p>
          <strong>{claim.claimedBy}</strong> is holding it from <code>{claim.runner}</code>, last heard from{' '}
          {agoPhrase(claim.heartbeatAt, new Date())}.
        </p>

        <p>
          Clearing the claim <strong>does not stop the runner</strong>. That session keeps going, keeps pushing,
          and may still move this ticket. It only makes {issueKey} claimable again — so the next pass may start a
          second session on it, and the two would then both be writing to it.
        </p>

        <p className="text-muted">Clear it when the runner is known to be gone or known to be wrong.</p>

        <div className="hatch-form-actions">
          <Button onClick={onClose}>Leave it</Button>
          <Button variant="danger" loading={busy} onClick={onConfirm}>
            Clear the claim
          </Button>
        </div>
      </div>
    </Modal>
  );
}
