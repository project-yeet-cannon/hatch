import { Badge, Button, Card } from '@hatch/ui';
import { agoPhrase, claimHealth } from '../lib/claim';
import type { IssueClaim } from '../types';

/**
 * Who is holding this ticket right now, from where, since when, and the last
 * thing they said.
 *
 * Above everything the page lets you change, for the reason `Waiting` sits
 * there: it is a fact about the ticket that something else is acting on it at
 * this moment, and knowing that before pressing anything is the point of
 * drawing it at all.
 *
 * Presentational - the busy flag, the error line and the re-read are the page's,
 * because a claim is one fact on a page made of them and its failures belong in
 * the one error line the page already has.
 *
 * Nothing at all where there is no claim, not an empty section. "A heading over
 * a blank space is a page saying 'this feature exists and has failed you'" is
 * WorkLog's comment and it applies unchanged, more so here: the overwhelming
 * majority of issues are held by nobody, so this section costs them nothing.
 *
 * Nothing here polls or ticks. The page re-reads on every write and on focus,
 * and this goes stale with it - which is the honest state of a fact fetched
 * once. A panel that ticked would be the first thing in Hatch that did.
 */
export function ClaimPanel({
  issueKey,
  claim,
  onClear,
}: {
  issueKey: string;
  claim: IssueClaim | null;
  /** Opens the speed bump. Not the clear itself - see ClearClaimDialog. */
  onClear: () => void;
}) {
  if (!claim) return null;

  // Once per render, as ClaimBadge does, and never read again.
  const now = new Date();
  const health = claimHealth(claim, now);

  return (
    <Card className={health === 'quiet' ? 'hatch-claim hatch-claim-quiet' : 'hatch-claim'}>
      <div className="hatch-claim-head">
        <h2 className="hatch-section-title">Claim</h2>
        {/* The whole block carries the state, so it reads as wanting attention
            rather than one word inside it doing. Said out loud as well, because
            an amber edge is not a sentence. */}
        {health === 'quiet' && <Badge tone="danger">not heard from lately</Badge>}
      </div>

      <p className="hatch-claim-holder">
        <strong>{claim.claimedBy}</strong> is working {issueKey} from <code>{claim.runner}</code>
      </p>

      {/* Elapsed words rather than timestamps: the question a claim raises is
          "is this still going", and an instant is an arithmetic problem
          somebody has to do before they can answer it. */}
      <div className="hatch-claim-facts">
        <span className="text-muted">taken {agoPhrase(claim.claimedAt, now)}</span>
        <span className={health === 'quiet' ? undefined : 'text-muted'}>
          last heard from {agoPhrase(claim.heartbeatAt, now)}
        </span>
      </div>

      {/* Absent entirely where the runner has not said anything - the same
          register as the section itself. A quoted blank is worse than silence. */}
      {claim.chatter && (
        <p className="hatch-claim-chatter">
          &ldquo;{claim.chatter}&rdquo; <span className="text-muted">{agoPhrase(claim.chatterAt, now)}</span>
        </p>
      )}

      <div className="hatch-form-actions">
        <Button onClick={onClear}>Clear the claim</Button>
      </div>
    </Card>
  );
}
