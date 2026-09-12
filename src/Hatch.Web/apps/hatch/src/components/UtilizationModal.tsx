import { useState } from 'react';
import { Button, Modal, Text } from '@hatch/ui';
import { agePhrase, resetPhrase, toneClass } from '../lib/utilization';
import type { Utilization, UtilizationCredits } from '../types';

/**
 * Every window the account is enforcing, and what has been bought on top.
 *
 * `@hatch/ui`'s Modal, which is what makes Escape and a click outside the panel
 * true without this file writing a key handler.
 *
 * It renders the reading the nav is already holding rather than fetching its
 * own, so opening and closing it repeatedly issues no request at all - and the
 * refresh below updates the battery and these rows together, because they are
 * one piece of state.
 *
 * The rows are the server's list, in the server's order. Nothing is sorted,
 * nothing is filtered, and an inactive window is a row like any other: the
 * account decided what to enforce and in what order, and a tracker second-
 * guessing that would be a tracker showing an operator a different account
 * from the one they have.
 */
export function UtilizationModal({
  open,
  onClose,
  reading,
  now,
  onRefresh,
}: {
  open: boolean;
  onClose: () => void;
  reading: Utilization;
  now: Date;
  onRefresh: () => Promise<void>;
}) {
  const [refreshing, setRefreshing] = useState(false);

  async function refresh() {
    setRefreshing(true);
    try {
      await onRefresh();
    } finally {
      setRefreshing(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="Claude usage">
      <div className="hatch-usage">
        {reading.state === 'unknown' ? (
          <Text tone="muted">
            The account could not be reached, and there has never been a reading to fall back to.
          </Text>
        ) : (
          <ul className="hatch-usage-rows">
            {reading.limits.map((limit, at) => (
              /* Keyed by position: the account is the only thing that names
                 these rows and two scoped rows can share a label, so the index
                 is the one identifier that is actually unique here. */
              <li key={at} className={`hatch-usage-row ${toneClass(limit.tone)}`}>
                <span className="hatch-usage-label">{limit.label}</span>
                <span className="hatch-usage-percent">{Math.round(limit.percent)}%</span>
                <span className="hatch-usage-reset">{resetPhrase(limit.resetsAt, now)}</span>
              </li>
            ))}
          </ul>
        )}

        {reading.credits && <Credits credits={reading.credits} />}

        <div className="hatch-usage-foot">
          {/* The age of the number, always - the reading in front of somebody
              is the last good one and it is worth knowing how old it is even
              when nothing has gone wrong. */}
          <Text tone="muted">
            {agePhrase(reading.readAt, now)}
            {reading.state === 'stale' && ' — the account could not be reached since'}
          </Text>
          <Button variant="secondary" disabled={refreshing} onClick={refresh}>
            {refreshing ? 'Refreshing…' : 'Refresh'}
          </Button>
        </div>
      </div>
    </Modal>
  );
}

/**
 * Extra usage, when the account reports any block at all. When it does not,
 * nothing here renders and the modal says nothing about credits - an account
 * that has never bought any should not be shown a row of zeroes suggesting it
 * could.
 *
 * The four facts are `extra_usage`'s. The account also describes credits a
 * second time in a different unit, and that one is deliberately unread on the
 * server - two numbers for the same thing on one screen is a bug report.
 */
function Credits({ credits }: { credits: UtilizationCredits }) {
  const money = (amount: number) =>
    credits.currency ? `${amount.toFixed(2)} ${credits.currency}` : amount.toFixed(2);

  return (
    <dl className="hatch-usage-credits">
      <dt>Extra usage</dt>
      <dd>{credits.isEnabled ? 'Enabled' : 'Off'}</dd>

      <dt>Monthly limit</dt>
      <dd>{credits.monthlyLimit === null ? 'None set' : money(credits.monthlyLimit)}</dd>

      <dt>Credits used</dt>
      <dd>{money(credits.usedCredits)}</dd>

      <dt>Spend limit</dt>
      <dd>{credits.spendLimitReached ? 'Reached' : 'Not reached'}</dd>
    </dl>
  );
}
