import type { RedeemFailure } from '../api/auth';
import { FAILURE_MESSAGES } from '../lib/messages';

interface FailureNoteProps {
  reason: RedeemFailure;
}

/**
 * The one place a refusal is rendered, shared by the form and the deep link so
 * the two cannot end up saying different things about the same `error` string.
 *
 * `role="alert"` because this appears after a submit the person is watching for
 * - without it, a screen reader announces nothing at all and the page simply
 * seems not to have responded.
 */
export function FailureNote({ reason }: FailureNoteProps) {
  const { title, detail } = FAILURE_MESSAGES[reason];

  return (
    <div className="failure" role="alert">
      <strong className="failure-title">{title}</strong>
      <span className="failure-detail">{detail}</span>
    </div>
  );
}
