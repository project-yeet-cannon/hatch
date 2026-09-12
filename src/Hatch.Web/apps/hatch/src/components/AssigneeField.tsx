import { assigneeToken, sameAssignee } from '../lib/assignee';
import type { Assignee, AssigneeDirectory, AssigneeRequest } from '../types';

/**
 * Who owns this ticket, and the two presses that change it.
 *
 * Presentational and fetching nothing: the page loads the directory once
 * beside its other reads and hands it down, so this can be drawn from props
 * alone and a refusal lands in the page's own error line in the server's words.
 *
 * **Assign to me** appears whenever somebody is signed in and is not already
 * the assignee - which is one condition covering both "nobody owns this" and
 * "somebody else does". **Unassign** appears whenever there is an assignee at
 * all, whoever it is. Both may show at once; that is the honest state of a
 * ticket assigned to somebody else, and it is one press to either.
 */
export function AssigneeField({
  assignee,
  directory,
  onChange,
}: {
  /** Who owns it now, or null. Already resolved by the server - an assignee whose
      person has been deleted or whose key has been revoked arrives as null. */
  assignee: Assignee | null;
  /** Everybody who could own it, and who the caller is. Null while it is still loading. */
  directory: AssigneeDirectory | null;
  onChange: (request: AssigneeRequest) => void;
}) {
  const me = directory?.me ?? null;
  const rows = directory?.assignees ?? [];

  /* The directory is what a row can be picked from, and the current assignee
     may not be in it - a board read and a directory read are two requests, and
     somebody can be deleted between them. Offering the value the select is
     holding keeps it from silently redrawing as "nobody" before anybody has
     pressed anything. */
  const known = assignee !== null && rows.some((row) => sameAssignee(row, assignee));

  return (
    <div className="hatch-assignee-field">
      <select
        aria-label="Assignee"
        value={assignee ? assigneeToken(assignee) : ''}
        disabled={directory === null}
        onChange={(e) => {
          const token = e.target.value;
          if (token === '') return onChange({ kind: null, id: null });

          const picked = rows.find((row) => assigneeToken(row) === token);
          if (picked) onChange({ kind: picked.kind, id: picked.id });
        }}
      >
        <option value="">— nobody —</option>
        {!known && assignee && <option value={assigneeToken(assignee)}>{assignee.name}</option>}
        {rows.map((row) => (
          <option key={assigneeToken(row)} value={assigneeToken(row)}>
            {row.name}
          </option>
        ))}
      </select>

      {/* `kind === 'person'` as well as "somebody is here": the write is closed
          to an API key, so a key holding this page would be offered a press
          that could only be refused. A browser never presents one, which is
          why this is a guard rather than a branch worth explaining on screen. */}
      {me?.kind === 'person' && !sameAssignee(me, assignee) && (
        <button
          type="button"
          className="hatch-assignee-mine"
          onClick={() => onChange({ kind: me.kind, id: me.id })}
        >
          Assign to me
        </button>
      )}

      {assignee && (
        <button type="button" className="hatch-assignee-clear" onClick={() => onChange({ kind: null, id: null })}>
          Unassign
        </button>
      )}
    </div>
  );
}
