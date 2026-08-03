import { Component } from 'react';
import type { ErrorInfo, ReactNode } from 'react';
import { clientLogger } from '../lib/clientLogger';

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  error: Error | null;
}

// A render-time throw would otherwise unmount the whole tree and leave a
// blank screen with no indication of what happened - this is the one place
// React still requires a class component (no hook equivalent exists yet).
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    clientLogger.error('React render error', {
      message: error.message,
      stack: error.stack,
      componentStack: info.componentStack,
    });
  }

  render() {
    if (this.state.error) {
      return (
        <div className="docs-crash-note" role="alert">
          Docs crashed — {this.state.error.message}
        </div>
      );
    }
    return this.props.children;
  }
}
