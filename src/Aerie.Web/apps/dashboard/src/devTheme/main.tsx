import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '../theme.css';
import './devTheme.css';
import { DevThemePage } from './DevThemePage';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <DevThemePage />
  </StrictMode>,
);
