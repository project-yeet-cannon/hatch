import { Component } from 'react';
import type { ErrorInfo, ReactNode } from 'react';

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  error: Error | null;
}

/**
 * A render-time throw would otherwise unmount the whole tree and leave a blank
 * screen with no indication of what happened - this is the one place React
 * still requires a class component.
 *
 * It reports to the console rather than to /api/ui-logs, for the reason the
 * design gallery's does: the picker is a static bundle of links that makes one
 * API call, a HEAD it already ignores the failure of. A seventh copy of
 * clientLogger.ts here would add to the duplication this workspace exists to
 * reduce and buy nothing.
 *
 * The fallback names the apps rather than only apologising. This page is how
 * someone reaches everything else in the house, so a crash that left them with
 * no address at all would be the picker failing at the one job it has.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('App picker render error', error, info.componentStack);
  }

  render() {
    if (this.state.error) {
      return (
        <div className="home-crash-note" role="alert">
          <p>The app picker crashed — {this.state.error.message}</p>
          <p>
            The apps themselves are unaffected: <a href="/apps/dashboard/">dashboard</a>,{' '}
            <a href="/apps/family/">family</a>, <a href="/apps/admin/">admin</a>,{' '}
            <a href="/apps/docs/">docs</a>.
          </p>
        </div>
      );
    }
    return this.props.children;
  }
}
