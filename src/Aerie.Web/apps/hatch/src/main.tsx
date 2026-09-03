import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { ThemeProvider } from '@aerie/ui';
import './theme.css';
import { App } from './App';
import { ErrorBoundary } from './components/ErrorBoundary';
import { clientLogger } from './lib/clientLogger';
import { routerBasename } from './lib/basename';

// Importing clientLogger (above) has already registered the window error/
// unhandledrejection listeners as a side effect - this is the earliest
// "lifecycle" log line main.tsx itself can produce.
clientLogger.info('hatch main.tsx module evaluated');

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <ThemeProvider>
        {/* Read from the URL rather than fixed, because this bundle answers
            at two addresses and only one of them names the prefix - see
            lib/basename.ts. */}
        <BrowserRouter basename={routerBasename(window.location.pathname)}>
          <App />
        </BrowserRouter>
      </ThemeProvider>
    </ErrorBoundary>
  </StrictMode>,
);

clientLogger.info('hatch root render invoked');
