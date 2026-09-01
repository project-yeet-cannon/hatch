import { Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { TopBar } from '@aerie/ui';
import './App.css';
import { ZonesPage } from './pages/ZonesPage';
import { RoutinesPage } from './pages/RoutinesPage';
import { PanelsPage } from './pages/PanelsPage';
import { CalendarsPage } from './pages/CalendarsPage';
import { PhotosPage } from './pages/PhotosPage';
import { DevicesPage } from './pages/DevicesPage';
import { DiscoveryPage } from './pages/DiscoveryPage';
import { SettingsPage } from './pages/SettingsPage';
import { ProvisioningPage } from './pages/ProvisioningPage';
import { PeoplePage } from './pages/PeoplePage';
import { SessionsPage } from './pages/SessionsPage';
import { RevisionsPage } from './pages/RevisionsPage';

const navLinkClass = ({ isActive }: { isActive: boolean }) => `admin-nav-link${isActive ? ' active' : ''}`;

export function App() {
  return (
    <div className="admin-app">
      <TopBar appName="Aerie Admin" />

      <nav className="admin-nav">
        <div className="admin-nav-content">
          <NavLink to="/zones" className={navLinkClass}>Zones</NavLink>
          <NavLink to="/routines" className={navLinkClass}>Routines</NavLink>
          <NavLink to="/panels" className={navLinkClass}>Panels</NavLink>
          <NavLink to="/calendars" className={navLinkClass}>Calendars</NavLink>
          <NavLink to="/photos" className={navLinkClass}>Photos</NavLink>
          <NavLink to="/devices" className={navLinkClass}>Devices</NavLink>
          <NavLink to="/discovery" className={navLinkClass}>Discovery</NavLink>
          <NavLink to="/settings" className={navLinkClass}>Settings</NavLink>
          <NavLink to="/provisioning" className={navLinkClass}>Provisioning</NavLink>
          <NavLink to="/people" className={navLinkClass}>People</NavLink>
          <NavLink to="/sessions" className={navLinkClass}>Sessions</NavLink>
          <NavLink to="/revisions" className={navLinkClass}>Revisions</NavLink>
        </div>
      </nav>

      <main className="admin-content">
        <Routes>
          <Route path="/" element={<Navigate to="/zones" replace />} />
          <Route path="/zones" element={<ZonesPage />} />
          <Route path="/routines" element={<RoutinesPage />} />
          <Route path="/panels" element={<PanelsPage />} />
          <Route path="/calendars" element={<CalendarsPage />} />
          <Route path="/photos" element={<PhotosPage />} />
          <Route path="/devices" element={<DevicesPage />} />
          <Route path="/discovery" element={<DiscoveryPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/provisioning" element={<ProvisioningPage />} />
          <Route path="/people" element={<PeoplePage />} />
          <Route path="/sessions" element={<SessionsPage />} />
          <Route path="/revisions" element={<RevisionsPage />} />
          <Route path="*" element={<Navigate to="/zones" replace />} />
        </Routes>
      </main>
    </div>
  );
}
