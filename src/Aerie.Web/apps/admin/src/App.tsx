import { Navigate, NavLink, Route, Routes } from 'react-router-dom';
import './App.css';
import { ZonesPage } from './pages/ZonesPage';
import { RoutinesPage } from './pages/RoutinesPage';
import { CalendarsPage } from './pages/CalendarsPage';
import { DevicesPage } from './pages/DevicesPage';
import { DiscoveryPage } from './pages/DiscoveryPage';
import { SettingsPage } from './pages/SettingsPage';
import { ProvisioningPage } from './pages/ProvisioningPage';
import { SessionsPage } from './pages/SessionsPage';

const navLinkClass = ({ isActive }: { isActive: boolean }) => `admin-nav-link${isActive ? ' active' : ''}`;

export function App() {
  return (
    <div className="admin-app">
      <header className="admin-header">
        <div className="admin-header-content">
          <a href="/" className="admin-back-link" title="Back to app picker">▦</a>
          <div className="admin-header-text">
            <h1>Aerie Admin</h1>
            <p>System configuration and data management</p>
          </div>
        </div>
      </header>

      <nav className="admin-nav">
        <div className="admin-nav-content">
          <NavLink to="/zones" className={navLinkClass}>Zones</NavLink>
          <NavLink to="/routines" className={navLinkClass}>Routines</NavLink>
          <NavLink to="/calendars" className={navLinkClass}>Calendars</NavLink>
          <NavLink to="/devices" className={navLinkClass}>Devices</NavLink>
          <NavLink to="/discovery" className={navLinkClass}>Discovery</NavLink>
          <NavLink to="/settings" className={navLinkClass}>Settings</NavLink>
          <NavLink to="/provisioning" className={navLinkClass}>Provisioning</NavLink>
          <NavLink to="/sessions" className={navLinkClass}>Sessions</NavLink>
        </div>
      </nav>

      <main className="admin-content">
        <Routes>
          <Route path="/" element={<Navigate to="/zones" replace />} />
          <Route path="/zones" element={<ZonesPage />} />
          <Route path="/routines" element={<RoutinesPage />} />
          <Route path="/calendars" element={<CalendarsPage />} />
          <Route path="/devices" element={<DevicesPage />} />
          <Route path="/discovery" element={<DiscoveryPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/provisioning" element={<ProvisioningPage />} />
          <Route path="/sessions" element={<SessionsPage />} />
          <Route path="*" element={<Navigate to="/zones" replace />} />
        </Routes>
      </main>
    </div>
  );
}
