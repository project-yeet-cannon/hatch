import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { ThemeProvider } from '@aerie/ui';
import './theme.css';
import { App } from './App';

// No `basename`, unlike every app Aerie.Api serves at /apps/<name>/. This one
// is served at the root of its own host by the trading control plane
// (aerie_trading/control/spa.py), which also serves index.html for any path it
// has no file for - so a deep link like /runs/42 reaches the router.
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </ThemeProvider>
  </StrictMode>,
);
