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
clientLogger.info('admin main.tsx module evaluated');

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ErrorBoundary>
      <ThemeProvider>
        <BrowserRouter basename="/apps/admin">
          <App />
        </BrowserRouter>
      </ThemeProvider>
    </ErrorBoundary>
  </StrictMode>,
);

clientLogger.info('admin root render invoked');
