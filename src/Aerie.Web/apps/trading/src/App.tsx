import { NavLink, Route, Routes } from 'react-router-dom';
import { TopBar } from '@aerie/ui';
import './App.css';
import { api } from './api/client';
import { pickerHref } from './lib/pickerHref';
import { useAsync, usePoll } from './lib/useAsync';
import { LeaderboardPage } from './pages/LeaderboardPage';
import { RunPage } from './pages/RunPage';
import { StrategiesPage } from './pages/StrategiesPage';
import { StrategyPage } from './pages/StrategyPage';
import { SweepPage } from './pages/SweepPage';

const navLinkClass = ({ isActive }: { isActive: boolean }) => `tr-nav-link${isActive ? ' active' : ''}`;

/**
 * The app frame: Aerie's bar, this app's nav, and the routed page.
 *
 * **The bar is `@aerie/ui`'s, not one of this app's own**, and that is the
 * point of it being here: the trading silo is served from its own host by its
 * own service, and without the shared bar it would look like a different
 * product that happened to be behind the same sign-in. The design system is
 * the entire mechanism keeping the suite looking like one thing, so this app
 * adds no palette and no chrome of its own.
 *
 * **`homeHref` is derived from the address bar, never configured.** This app
 * is on `trading.<domain>` and the picker is on `home.<domain>`; the base
 * domain is not a value this repository may hold (docs/ethos.md), so it is
 * read off the host the page was served from. An address with no domain to
 * borrow - an IP, `localhost` - falls back to `/`, which on this host is this
 * app itself: a link that goes nowhere useful is worse than one that goes
 * home.
 */
export function App() {
  const { data: queue, reload } = useAsync(() => api.queue(), []);
  const working = (queue?.queued ?? 0) + (queue?.running ?? 0) > 0;
  usePoll(reload, working);

  return (
    <div className="tr-app">
      <TopBar
        appName="Trading"
        homeHref={pickerHref(window.location.hostname)}
        trailing={
          working ? (
            <span className="tr-queue" title="Runs waiting or in flight across every sweep.">
              {(queue?.running ?? 0).toLocaleString()} running · {(queue?.queued ?? 0).toLocaleString()} queued
            </span>
          ) : null
        }
      />

      <nav className="tr-nav">
        <div className="tr-nav-content">
          <NavLink to="/" className={navLinkClass} end>
            Strategies
          </NavLink>
          <NavLink to="/leaderboard" className={navLinkClass}>
            Leaderboard
          </NavLink>
        </div>
      </nav>

      <main className="tr-content">
        <Routes>
          <Route path="/" element={<StrategiesPage />} />
          <Route path="/leaderboard" element={<LeaderboardPage />} />
          <Route path="/strategies/:name" element={<StrategyPage />} />
          <Route path="/runs/:id" element={<RunPage />} />
          <Route path="/sweeps/:id" element={<SweepPage />} />
          {/* An unknown deep link lands on the strategies list, which is the
              app. There is no page worth writing that says "that URL was
              wrong". */}
          <Route path="*" element={<StrategiesPage />} />
        </Routes>
      </main>
    </div>
  );
}

