import type { ReactNode } from 'react';

/*
  The small pieces every Storage screen needs: the three states a list can be
  in, and the code chip. Nothing here is storage-specific enough to be clever -
  it exists so a screen file is its own logic and not four copies of a spinner.
*/

export function Loading() {
  return <div className="storage-note">Loading…</div>;
}

/**
 * A failed read, with the way out. Retry rather than "reload the page": the
 * common cause is a phone that wandered off the tailnet at the back of the
 * garage, and by the time someone reads this, walking two steps has often
 * already fixed it.
 */
export function ErrorNote({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div className="storage-note storage-error" role="alert">
      <p>{message}</p>
      {onRetry && <button onClick={onRetry}>Try again</button>}
    </div>
  );
}

/** The message a write failed with - inline, next to the button that failed. */
export function InlineError({ message }: { message: string }) {
  return (
    <p className="storage-inline-error" role="alert">
      {message}
    </p>
  );
}

export function EmptyNote({ children }: { children: ReactNode }) {
  return <div className="storage-note">{children}</div>;
}

/**
 * The crate code, in the shape it's printed on the label. Tabular figures and a
 * mono face because this is a string people compare character by character
 * against a box, standing up, in bad light.
 */
export function CodeChip({ code }: { code: string }) {
  return <span className="crate-code">{code}</span>;
}
