import { useCallback, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { appHref } from '../lib/basename';
import { dismiss, raise } from '../lib/confirmations';
import type { Confirmation } from '../lib/confirmations';
import { CreatedIssuesContext } from '../lib/useIssueConfirmations';
import type { Issue } from '../types';

/**
 * What the corner of the window says about issues filed in this tab.
 *
 * Filing used to end in silence: the dialog closed, the board reloaded, and the
 * card was somewhere in the leftmost column among the rest - no key to copy,
 * nothing to open, and no record at all of the second one once the third was
 * filed. So every filing raises a chicklet with the key, the title and a link
 * that opens the issue in a new tab, and they stack rather than replace one
 * another.
 *
 * They stay until they are closed. Nothing times out, because a timeout is a
 * confirmation that expires while the operator is looking at something else.
 *
 * Local to this app rather than in @hatch/ui: that package's barrel puts every
 * component's CSS in every consuming app's bundle, and there is exactly one
 * consumer of this. It moves the way Modal did - when a second app wants it.
 *
 * The context itself and the hook that reads it are in
 * lib/useIssueConfirmations.ts - see the note there on why they are not here.
 *
 * The provider sits above <Routes> rather than inside a page, because a
 * chicklet held in BoardPage's state would vanish the moment the operator
 * clicked through to the issue they just filed - which is the first thing
 * anybody does with it.
 */
export function CreatedIssuesProvider({ children }: { children: ReactNode }) {
  const [stack, setStack] = useState<Confirmation[]>([]);
  // Monotonic and never reused, so React's key is stable and closing one
  // chicklet can never take a later one filed under the same issue key.
  const nextId = useRef(0);

  const confirm = useCallback((issue: Pick<Issue, 'key' | 'title'>) => {
    nextId.current += 1;
    const id = nextId.current;
    setStack((prev) => raise(prev, issue, id));
  }, []);

  // Steady across renders, so a filing surface holding `confirm` in a
  // dependency list is not re-running on every keystroke elsewhere.
  const value = useMemo(() => ({ confirm }), [confirm]);

  return (
    <CreatedIssuesContext.Provider value={value}>
      {children}
      <ConfirmationStack
        stack={stack}
        onDismiss={(id) => setStack((prev) => dismiss(prev, id))}
        onDismissAll={() => setStack([])}
      />
    </CreatedIssuesContext.Provider>
  );
}

/**
 * The bottom-left corner.
 *
 * Drawn whether or not there is anything in it: a live region has to be in the
 * document *before* its content changes for a screen reader to announce the
 * change, so a region that appeared along with the first chicklet would
 * announce nothing. Empty it draws no box and takes no clicks.
 */
function ConfirmationStack({
  stack,
  onDismiss,
  onDismissAll,
}: {
  stack: Confirmation[];
  onDismiss: (id: number) => void;
  onDismissAll: () => void;
}) {
  return (
    <div className="hatch-confirmations" role="status" aria-live="polite" aria-label="Issues filed">
      {/* Only worth drawing where there is more than one to dismiss: with one
          chicklet its own close control is already the whole of the job. */}
      {stack.length > 1 && (
        <button type="button" className="hatch-confirmations-clear" onClick={onDismissAll}>
          Dismiss all
        </button>
      )}

      <ul className="hatch-confirmations-list">
        {stack.map((c) => (
          <li key={c.id} className="hatch-confirmation">
            {/* An anchor rather than a <Link>: target="_blank" opens a second
                document, which React Router does not route. appHref is what
                keeps that second document on the right prefix - this bundle
                answers at two addresses and only one of them names it. See
                lib/basename.ts. */}
            <a
              className="hatch-confirmation-key"
              href={appHref(`/issues/${c.issueKey}`)}
              target="_blank"
              rel="noreferrer"
            >
              {c.issueKey} ↗
            </a>
            <span className="hatch-confirmation-title">{c.title}</span>
            {/* Named after what it closes, because "Dismiss" eight times over
                tells a screen reader nothing about which one is which. */}
            <button
              type="button"
              className="hatch-confirmation-close"
              aria-label={`Dismiss ${c.issueKey}`}
              onClick={() => onDismiss(c.id)}
            >
              ×
            </button>
          </li>
        ))}
      </ul>
    </div>
  );
}
