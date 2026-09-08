import { NavLink, Route, Routes } from 'react-router-dom';
import { TopBar } from '@aerie/ui';
import './App.css';
import { NavLocalPerson } from './components/NavLocalPerson';
import { NavUtilization } from './components/NavUtilization';
import { BoardPage } from './pages/BoardPage';
import { PlanPage } from './pages/PlanPage';
import { LeaderboardPage } from './pages/LeaderboardPage';
import { IssuePage } from './pages/IssuePage';
import { ProjectsPage } from './pages/ProjectsPage';
import { StatusesPage } from './pages/StatusesPage';
import { PlaybooksPage } from './pages/PlaybooksPage';
import { BulkPage } from './pages/BulkPage';
import { ImportPage } from './pages/ImportPage';

const navLinkClass = ({ isActive }: { isActive: boolean }) => `hatch-nav-link${isActive ? ' active' : ''}`;

export function App() {
  return (
    <div className="hatch-app">
      {/* Named rather than left to default to "/", because on
          hatch.${DOMAIN} "/" is this app's own board and the switcher would
          be a button that goes nowhere. /apps/home/ is the picker on either
          host - it is what "/" redirects to on the house, and it is reachable
          on this one through the `hatch-direct` Ingress. */}
      <TopBar appName="Hatch" homeHref="/apps/home/" />

      <nav className="hatch-nav">
        <div className="hatch-nav-content">
          {/* `end` so the board link is only lit on the board itself - every
              other route is beneath "/" and would otherwise light it too. */}
          <NavLink to="/" className={navLinkClass} end>Board</NavLink>
          <NavLink to="/plan" className={navLinkClass}>Plan</NavLink>
          {/* Beside Plan: the two pages that read across the whole board rather
              than about one ticket belong together. */}
          <NavLink to="/leaderboard" className={navLinkClass}>Leaderboard</NavLink>
          <NavLink to="/bulk" className={navLinkClass}>Bulk edit</NavLink>
          <NavLink to="/projects" className={navLinkClass}>Projects</NavLink>
          <NavLink to="/statuses" className={navLinkClass}>Statuses</NavLink>
          <NavLink to="/playbooks" className={navLinkClass}>Playbooks</NavLink>
          <NavLink to="/import" className={navLinkClass}>Import</NavLink>

          {/* The right-hand group: what the strip says about this session
              rather than about the board. One box because two elements each
              pushed right by their own auto margin would share the free space
              between them and land apart. Both draw nothing on an installation
              that has no answer for them - a cluster install has neither - so
              the strip is a row of links and no gap where something used to
              be. */}
          <div className="hatch-nav-aside">
            <NavLocalPerson />
            <NavUtilization />
          </div>
        </div>
      </nav>

      <main className="hatch-content">
        <Routes>
          <Route path="/" element={<BoardPage />} />
          <Route path="/plan" element={<PlanPage />} />
          <Route path="/leaderboard" element={<LeaderboardPage />} />
          <Route path="/bulk" element={<BulkPage />} />
          <Route path="/projects" element={<ProjectsPage />} />
          <Route path="/statuses" element={<StatusesPage />} />
          <Route path="/playbooks" element={<PlaybooksPage />} />
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
