import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './theme.css';
import { App } from './App';
import { ErrorBoundary } from './components/ErrorBoundary';
import { clientLogger } from './lib/clientLogger';

declare global {
  interface Window {
    /** Set by index.html's fallback screen once React has mounted. */
    __aerieMounted?: boolean;
    /** Tears down the fallback screen and its timers. Defined in index.html. */
    __aerieDismissFallback?: () => void;
  }
}

// Importing clientLogger (above) has already registered the window error/
// unhandledrejection listeners as a side effect - this is the earliest
// "lifecycle" log line main.tsx itself can produce.
clientLogger.info('kiosk main.tsx module evaluated');

// Before the first render rather than after: index.html paints a clock and a
// message into #root so a bundle that never runs is not a white wall (see the
// comment there and docs/kiosk-architecture.md). Calling this
// rather than relying on createRoot() to clear the container is deliberate -
// the fallback owns a 15s interval and a mount watchdog that have to be torn
// down, and the flag is what tells the watchdog it lost the race.
window.__aerieMounted = true;
window.__aerieDismissFallback?.();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <App />
    </ErrorBoundary>
  </StrictMode>,
);

clientLogger.info('kiosk root render invoked');
