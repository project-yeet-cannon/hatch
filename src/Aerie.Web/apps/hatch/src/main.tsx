import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { ThemeProvider } from '@aerie/ui';
import './theme.css';
import { App } from './App';
import { ErrorBoundary } from './components/ErrorBoundary';
import { clientLogger } from './lib/clientLogger';

// Importing clientLogger (above) has already registered the window error/
// unhandledrejection listeners as a side effect - this is the earliest
// "lifecycle" log line main.tsx itself can produce.
clientLogger.info('hatch main.tsx module evaluated');

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <ThemeProvider>
        <BrowserRouter basename="/apps/hatch">
          <App />
        </BrowserRouter>
      </ThemeProvider>
    </ErrorBoundary>
  </StrictMode>,
);

clientLogger.info('hatch root render invoked');
