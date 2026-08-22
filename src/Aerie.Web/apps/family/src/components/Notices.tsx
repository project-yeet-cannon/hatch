import type { ReactNode } from 'react';

/*
  The three states any screen in any module can be in, plus the inline write
  failure. Storage Helper wrote these first and Gather wanted the same four
  verbatim, which per App.css is when a thing stops being a module's business.

  Styled by .note / .note-error / .inline-error in App.css - shell chrome, so
  that a module's stylesheet never has to restate what "loading" looks like.
*/

export function Loading() {
  return <div className="note">Loading…</div>;
}

/**
 * A failed read, with the way out. Retry rather than "reload the page": the
 * common cause is a phone that wandered off the tailnet at the back of the
 * garage, and by the time someone reads this, walking two steps has often
 * already fixed it.
 */
export function ErrorNote({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div className="note note-error" role="alert">
      <p>{message}</p>
      {onRetry && <button onClick={onRetry}>Try again</button>}
    </div>
  );
}

/** The message a write failed with - inline, next to the button that failed. */
export function InlineError({ message }: { message: string }) {
  return (
    <p className="inline-error" role="alert">
      {message}
    </p>
  );
}

export function EmptyNote({ children }: { children: ReactNode }) {
  return <div className="note">{children}</div>;
}
