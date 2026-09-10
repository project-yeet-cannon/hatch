import type { AssigneeDirectory } from '../types';

/**
 * *This one first.* One control that marks an issue expedited and, pressed
 * again, unmarks it - drawn the same on the issue page and on the board's
 * summary, because it is the same fact and the same press.
 *
 * A toggle button rather than a checkbox and rather than a pair of buttons: the
 * state it is in *is* the answer to "is this expedited", so the control says so
 * without the reader pressing anything, and `aria-pressed` says the same thing
 * to a screen reader. What it sends is the state it wants and never "the other
 * one" - see ExpediteRequest for why a toggle on the wire would race two
 * browsers looking at the same card.
 *
 * Presentational and fetching nothing: the page loads the directory once beside
 * its other reads and hands it down, so a refusal lands in the page's own error
 * line in the server's own words and this never has an opinion about whether a
 * press worked.
 */
export function ExpediteControl({
  expedited,
  directory,
  busy = false,
  onChange,
}: {
  expedited: boolean;
  /** Everybody who could own an issue, and who the caller is. Null while it is still loading. */
  directory: AssigneeDirectory | null;
  /** A press is in flight. The button stays where it is and stops answering. */
  busy?: boolean;
  onChange: (expedited: boolean) => void;
}) {
  const me = directory?.me ?? null;

  /* `kind === 'person'` as well as "somebody is here": the write is closed to
     an API key, so a key holding this page would be offered a press that could
     only be refused. It still gets the state, drawn as a word rather than as a
     dead button - an agent reading this page is entitled to know why its ticket
     was reached first, and a control it cannot use is not how to tell it. */
  if (me?.kind !== 'person') {
    return (
      <span className={`hatch-expedite-said${expedited ? ' on' : ''}`}>
        {expedited ? 'Expedited' : 'Not expedited'}
      </span>
    );
  }

  return (
    <button
      type="button"
      className={`hatch-expedite${expedited ? ' on' : ''}`}
      aria-pressed={expedited}
      disabled={busy}
      title={
        expedited
          ? 'This one goes first: it floats to the top of its column and the dispatcher reaches for it before anything else. Press to unmark it.'
          : 'Mark this one first: it floats to the top of its column and the dispatcher reaches for it before anything else.'
      }
      onClick={() => onChange(!expedited)}
    >
      {expedited ? '↑ Expedited' : 'Expedite'}
    </button>
  );
}
