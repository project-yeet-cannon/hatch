import { Badge, Card } from '@aerie/ui';
import { Command } from './Command';
import {
  compactTokens,
  durationPhrase,
  entryMark,
  entryTitle,
  errorPhrase,
  hasDescendantSpend,
  moneyPhrase,
  ownPhrase,
  resumeCommand,
  showsWorkLog,
  totalsPhrase,
} from '../lib/workLog';
import type { WorkLog as WorkLogData, WorkLogEntry } from '../types';

/**
 * What this ticket cost: one row per agent session, its tokens and its notional
 * dollars, what it did in a sentence, and how long it took.
 *
 * Its own section, between the thread and the trail and visibly part of
 * neither. A comment is somebody talking, an event is something happening, and
 * this is the meter - three different kinds of fact, and reading them as one
 * list is how none of them get read.
 *
 * **Nothing here writes.** There is no control for adding, editing or deleting
 * an entry, and `api/client.ts` has no function that could: an entry is written
 * by the dispatcher with an API key and the server refuses a browser outright.
 * The absence is the guarantee - do not add one for symmetry.
 */
export function WorkLog({ log, error }: { log: WorkLogData | null; error: string | null }) {
  // The section's own failure, said in the section. An issue somebody came here
  // to read should not blank because a meter could not be fetched.
  if (error) {
    return (
      <Card>
        <h2 className="hatch-section-title">Work log</h2>
        <p className="text-muted">{error}</p>
      </Card>
    );
  }

  // Nothing at all, not an empty section: a heading over a blank space is a
  // page saying "this feature exists and has failed you".
  if (!showsWorkLog(log)) return null;

  const errors = errorPhrase(log);

  return (
    <Card>
      <h2 className="hatch-section-title">Work log</h2>

      {/* Tokens are the headline and the dollars are secondary, because on a
          subscription the dollars are notional list price rather than money
          that left an account. */}
      <p className="hatch-worklog-totals">{totalsPhrase(log)}</p>

      {/* Said only when the two differ, so one number never passes for the
          other: an epic showing four sessions and one of its own is the whole
          feature, not a bug. */}
      {hasDescendantSpend(log) && <p className="text-muted">Everything beneath this issue, {ownPhrase(log)}.</p>}

      {errors && <p className="text-muted">{errors}.</p>}

      {log.entries.length === 0 ? (
        <p className="text-muted">No session has been run against this issue itself.</p>
      ) : (
        <ul className="hatch-worklog">
          {log.entries.map((entry) => (
            <Entry key={entry.id} entry={entry} />
          ))}
        </ul>
      )}
    </Card>
  );
}

/** One session. Newest first is the server's ordering and is not re-derived. */
function Entry({ entry }: { entry: WorkLogEntry }) {
  const mark = entryMark(entry);

  return (
    <li className={`hatch-worklog-entry${mark ? ` hatch-worklog-${mark}` : ''}`}>
      <div className="hatch-worklog-head">
        <strong className={entry.described ? undefined : 'text-muted'}>{entryTitle(entry)}</strong>
        {mark === 'error' && <Badge tone="danger">ended with an error</Badge>}
        {mark === 'undescribed' && <Badge>undescribed</Badge>}
      </div>

      {entry.summary && <p className="hatch-worklog-summary">{entry.summary}</p>}

      <div className="hatch-worklog-facts">
        {/* The headline first and the money after it, in that order every time
            it is drawn. */}
        <span>
          <strong>{compactTokens(entry.totalTokens)}</strong> tokens
        </span>
        <span className="text-muted">{moneyPhrase(entry.costUsd)}</span>
        <span className="text-muted">{durationPhrase(entry.durationMs)}</span>
        <span className="text-muted">
          {entry.turns} {entry.turns === 1 ? 'turn' : 'turns'}
        </span>
        <span className="text-muted">{new Date(entry.endedAt).toLocaleString()}</span>
      </div>

      {/* The session id in the form it is used in, rather than as an id
          somebody has to assemble a command around. */}
      <Command command={resumeCommand(entry.sessionId)} />
    </li>
  );
}
