import { Navigate, NavLink, Route, Routes } from 'react-router-dom';
import './App.css';
import { ZonesPage } from './pages/ZonesPage';
import { DevicesPage } from './pages/DevicesPage';
import { DiscoveryPage } from './pages/DiscoveryPage';
import { SettingsPage } from './pages/SettingsPage';

const navLinkClass = ({ isActive }: { isActive: boolean }) => `admin-nav-link${isActive ? ' active' : ''}`;

export function App() {
  return (
    <div className="admin-app">
      <header className="admin-header">
        <div className="admin-header-content">
          <h1>Aerie Admin</h1>
          <p>System configuration and data management</p>
        </div>
      </header>

      <nav className="admin-nav">
        <div className="admin-nav-content">
          <NavLink to="/zones" className={navLinkClass}>Zones</NavLink>
          <NavLink to="/devices" className={navLinkClass}>Devices</NavLink>
          <NavLink to="/discovery" className={navLinkClass}>Discovery</NavLink>
          <NavLink to="/settings" className={navLinkClass}>Settings</NavLink>
        </div>
      </nav>

      <main className="admin-content">
        <Routes>
          <Route path="/" element={<Navigate to="/zones" replace />} />
          <Route path="/zones" element={<ZonesPage />} />
          <Route path="/devices" element={<DevicesPage />} />
          <Route path="/discovery" element={<DiscoveryPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="*" element={<Navigate to="/zones" replace />} />
        </Routes>
      </main>
    </div>
  );
}
