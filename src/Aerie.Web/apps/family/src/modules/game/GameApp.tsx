import { Navigate, Route, Routes } from 'react-router-dom';
import { PlayPage } from './PlayPage';
import { WorldsPage } from './WorldsPage';
import { playPath } from './routes';
import './game.css';

/**
 * The game module's slice of the shell.
 *
 * The index route is the game itself, not a list of games - opening this app
 * should land on something playable, the same way a console does. The list is a
 * detour off it, reached from the frame's own toolbar, because choosing between
 * games is the rarer thing by a wide margin.
 */
export default function GameApp() {
  return (
    <Routes>
      <Route index element={<PlayPage />} />
      <Route path="w/:worldId" element={<PlayPage />} />
      <Route path="worlds" element={<WorldsPage />} />
      <Route path="*" element={<Navigate to={playPath} replace />} />
    </Routes>
  );
}
