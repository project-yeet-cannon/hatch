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
 * Unlike the other apps' boundaries this reports to the console rather than to
 * /api/ui-logs. The gallery makes no API calls at all: it is a static bundle a
 * designer browses, so shipping a seventh copy of clientLogger.ts to it would
 * add to the duplication Phase 4 is chartered to reduce and buy nothing that
 * the open devtools of the person looking at the page do not already give.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Design gallery render error', error, info.componentStack);
  }

  render() {
    if (this.state.error) {
      return (
        <div className="design-crash-note" role="alert">
          The gallery crashed — {this.state.error.message}
        </div>
      );
    }
    return this.props.children;
  }
}
