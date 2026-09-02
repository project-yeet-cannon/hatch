import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { ThemeProvider } from '@aerie/ui';
import './theme.css';
import { App } from './App';
import { ErrorBoundary } from './components/ErrorBoundary';
import { clientLogger } from './lib/clientLogger';

// Importing clientLogger (above) has already registered the window error/
// unhandledrejection listeners as a side effect - this is the earliest
// "lifecycle" log line main.tsx itself can produce.
clientLogger.info('modeler main.tsx module evaluated');

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      {/* <TopBar> renders the theme switch unconditionally and useTheme throws
          without a provider, so the bar needs one above it. */}
      <ThemeProvider>
        <App />
      </ThemeProvider>
    </ErrorBoundary>
  </StrictMode>,
);

clientLogger.info('modeler root render invoked');
