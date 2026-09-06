import { useState } from 'react';

/**
 * A command in the shape it is pasted, with one press to take it.
 *
 * The text is a <code> and is always selectable, which is the fallback: where
 * the clipboard API is not there - an insecure origin, a browser that never
 * had it - the button is not drawn and the command is still there to be
 * selected and copied by hand. A copy button that silently does nothing is
 * worse than no button, because there is no way to tell from the outside that
 * it did nothing.
 *
 * A write that is refused after the press says so too, for the same reason:
 * the clipboard is permission-gated and a refusal is a thing that happens.
 */
export function Command({ command, label = 'Copy' }: { command: string; label?: string }) {
  const [said, setSaid] = useState<string | null>(null);
  const [known, setKnown] = useState(command);

  // "Copied" is about the string that was copied, so it goes when that string
  // changes underneath - a refetch that names a different ticket must not leave
  // a button claiming this one is already on the clipboard. Adjusted during
  // render rather than in an effect, the way the inline editors on the issue
  // page are, so the stale label is never painted.
  if (command !== known) {
    setKnown(command);
    setSaid(null);
  }

  // Read at render rather than held: whether this document may reach the
  // clipboard is a fact about the page, and it does not change under us.
  const canCopy = typeof navigator !== 'undefined' && typeof navigator.clipboard?.writeText === 'function';

  async function copy() {
    try {
      await navigator.clipboard.writeText(command);
      setSaid('Copied');
    } catch {
      setSaid('Select it and copy');
    }
  }

  return (
    <span className="hatch-command">
      <code>{command}</code>
      {canCopy && (
        <button type="button" className="hatch-command-copy" onClick={() => void copy()}>
          {said ?? label}
        </button>
      )}
    </span>
  );
}
