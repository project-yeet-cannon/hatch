import { Suspense } from 'react';
import { Link, NavLink, Route, Routes, useLocation } from 'react-router-dom';
import './App.css';
import { modules } from './modules/registry';
import { HomePage } from './HomePage';
import { useOnline } from './lib/useOnline';

/** The module whose routes are currently mounted, or null on the home screen. */
function useActiveModule() {
  const segment = useLocation().pathname.split('/')[1];
  return modules.find((m) => m.id === segment) ?? null;
}

export function App() {
  const active = useActiveModule();
  const online = useOnline();

  return (
    <div className="shell">
      <header className="shell-header">
        {active && (
          <Link to="/" className="shell-back" aria-label="All apps">
            ‹
          </Link>
        )}
        <span className="shell-title">{active?.title ?? 'Aerie Family'}</span>
      </header>

      {/* Worth saying out loud rather than letting writes fail silently: the
          service worker will still serve anything already read, so the app
          looks entirely normal until someone tries to save. */}
      {!online && (
        <div className="shell-offline" role="status">
          Offline — showing what was last loaded
        </div>
      )}

      <main className="shell-main">
        <Suspense fallback={<div className="shell-pending">Loading…</div>}>
          <Routes>
            <Route path="/" element={<HomePage />} />
            {modules.map((module) => (
              <Route key={module.id} path={`${module.id}/*`} element={<module.Component />} />
            ))}
            {/* A stale bookmark or a QR for a module that no longer exists
                lands somewhere explicable instead of on a blank screen. */}
            <Route path="*" element={<NotFound />} />
          </Routes>
        </Suspense>
      </main>

      <nav className="shell-tabs">
        <NavLink to="/" end className={tabClass}>
          <span className="shell-tab-icon" aria-hidden="true">
            ⌂
          </span>
          Home
        </NavLink>
        {modules.map((module) => (
          <NavLink key={module.id} to={`/${module.id}`} className={tabClass}>
            <span className="shell-tab-icon" aria-hidden="true">
              {module.icon}
            </span>
            {module.title}
          </NavLink>
        ))}
      </nav>
    </div>
  );
}

const tabClass = ({ isActive }: { isActive: boolean }) => `shell-tab${isActive ? ' active' : ''}`;

function NotFound() {
  return (
    <div className="shell-pending">
      <p>Nothing here.</p>
      <Link to="/">Back to all apps</Link>
    </div>
  );
}
