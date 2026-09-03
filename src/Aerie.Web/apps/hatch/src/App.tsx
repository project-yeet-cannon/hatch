import { NavLink, Route, Routes } from 'react-router-dom';
import { TopBar } from '@aerie/ui';
import './App.css';
import { BoardPage } from './pages/BoardPage';
import { IssuePage } from './pages/IssuePage';
import { ProjectsPage } from './pages/ProjectsPage';
import { StatusesPage } from './pages/StatusesPage';
import { ImportPage } from './pages/ImportPage';

const navLinkClass = ({ isActive }: { isActive: boolean }) => `hatch-nav-link${isActive ? ' active' : ''}`;

export function App() {
  return (
    <div className="hatch-app">
      <TopBar appName="Hatch" />

      <nav className="hatch-nav">
        <div className="hatch-nav-content">
          {/* `end` so the board link is only lit on the board itself - every
              other route is beneath "/" and would otherwise light it too. */}
          <NavLink to="/" className={navLinkClass} end>Board</NavLink>
          <NavLink to="/projects" className={navLinkClass}>Projects</NavLink>
          <NavLink to="/statuses" className={navLinkClass}>Statuses</NavLink>
          <NavLink to="/import" className={navLinkClass}>Import</NavLink>
        </div>
      </nav>

      <main className="hatch-content">
        <Routes>
          <Route path="/" element={<BoardPage />} />
          <Route path="/projects" element={<ProjectsPage />} />
          <Route path="/statuses" element={<StatusesPage />} />
          <Route path="/issues/:key" element={<IssuePage />} />
          <Route path="/import" element={<ImportPage />} />
          {/* An unknown deep link lands on the board rather than on nothing -
              the board is the app, and there is no page worth writing that
              says "that URL was wrong". */}
          <Route path="*" element={<BoardPage />} />
        </Routes>
      </main>
    </div>
  );
}
