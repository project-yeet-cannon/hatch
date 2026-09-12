import { createContext, useContext } from 'react';
import type { Issue } from '../types';

/**
 * Saying that an issue was just filed, from wherever it was filed.
 *
 * The context and its hook rather than the component that provides them, in a
 * file of their own: a module that exports both a component and a hook cannot
 * be hot-reloaded, and the stack lives in that provider's state - editing the
 * chicklet would empty the corner every time. The same split @hatch/ui's
 * ThemeProvider takes, for the same reason. The provider and the region are in
 * components/CreatedIssues.tsx.
 */
export interface CreatedIssues {
  /** Say that this issue was just filed. The created issue is what createIssue
      already returns, so nothing has to be fetched to draw the chicklet. */
  confirm: (issue: Pick<Issue, 'key' | 'title'>) => void;
}

export const CreatedIssuesContext = createContext<CreatedIssues | null>(null);

/** The one thing a filing surface needs. The stack itself is nobody else's
    business - it is drawn by the provider and closed by the operator. */
export function useIssueConfirmations(): CreatedIssues {
  const held = useContext(CreatedIssuesContext);
  if (!held) throw new Error('useIssueConfirmations outside CreatedIssuesProvider');
  return held;
}
