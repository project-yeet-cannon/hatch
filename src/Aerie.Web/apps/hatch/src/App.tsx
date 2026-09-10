import { NavLink, Route, Routes } from 'react-router-dom';
import { TopBar } from '@aerie/ui';
import './App.css';
import { CreatedIssuesProvider } from './components/CreatedIssues';
import { NavLocalPerson } from './components/NavLocalPerson';
import { NavUtilization } from './components/NavUtilization';
import { BoardPage } from './pages/BoardPage';
import { PlanPage } from './pages/PlanPage';
import { LeaderboardPage } from './pages/LeaderboardPage';
import { IssuePage } from './pages/IssuePage';
import { ProjectsPage } from './pages/ProjectsPage';
import { StatusesPage } from './pages/StatusesPage';
import { PlaybooksPage } from './pages/PlaybooksPage';
import { RunnersPage } from './pages/RunnersPage';
import { BulkPage } from './pages/BulkPage';
import { ImportPage } from './pages/ImportPage';
import { RunnerPage } from './pages/RunnerPage';
import { SettingsPage } from './pages/SettingsPage';

const navLinkClass = ({ isActive }: { isActive: boolean }) => `hatch-nav-link${isActive ? ' active' : ''}`;

export function App() {
  return (
    /* Above <Routes> and inside the router: a confirmation chicklet is raised
       on the board and then read on the issue page it links to, so the stack
       has to outlive the navigation between them. */
    <CreatedIssuesProvider>
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
            {/* Beside Playbooks: the two pages that are about the loop rather
                than about the board. One says what an agent is told, the other
                says which agents are running and what they may spend. */}
            <NavLink to="/runners" className={navLinkClass}>Runners</NavLink>
            <NavLink to="/import" className={navLinkClass}>Import</NavLink>
            {/* Where the runner comes from: this image publishes it, so a friend
                who has the stack up needs nothing else to join the loop. Beside
                Settings because both are about the installation rather than
                about the board. */}
            <NavLink to="/runner" className={navLinkClass}>Runner</NavLink>
            {/* A page like any other rather than a strip element: what it holds
                is this installation's own configuration, and it is the only place
                a Hatch with no admin app beside it can be configured at all. */}
            <NavLink to="/settings" className={navLinkClass}>Settings</NavLink>
            {/* Out of this app and into the docs one, so a plain anchor and a
                real page navigation - a NavLink would hand the path to this
                app's router, which owns none of it. It is never "active" the way
                a client route is, so it wears the resting class outright rather
                than the callback the others need. Aimed at one page rather than
                the docs index: a fresh install's question is "how do I use this",
                and that page answers it. The path resolves on either host - a
                local stack serves every app on one origin, and on a cluster the
                `hatch-direct` Ingress claims every /apps path ahead of the
                rewrite. */}
            <a href="/apps/docs/hatch-at-home" className="hatch-nav-link">Docs</a>

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
            <Route path="/runners" element={<RunnersPage />} />
            <Route path="/issues/:key" element={<IssuePage />} />
            <Route path="/import" element={<ImportPage />} />
            <Route path="/runner" element={<RunnerPage />} />
            <Route path="/settings" element={<SettingsPage />} />
            {/* An unknown deep link lands on the board rather than on nothing -
                the board is the app, and there is no page worth writing that
                says "that URL was wrong". */}
            <Route path="*" element={<BoardPage />} />
          </Routes>
        </main>
      </div>
    </CreatedIssuesProvider>
  );
}
