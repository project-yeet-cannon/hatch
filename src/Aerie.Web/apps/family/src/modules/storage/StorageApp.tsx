import { Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { CrateListPage } from './CrateListPage';
import { CratePage } from './CratePage';
import { ItemIndexPage } from './ItemIndexPage';
import { LocationsPage } from './LocationsPage';
import { cratesPath, itemsPath, locationsPath } from './routes';
import './storage.css';

/**
 * Storage Helper's slice of the shell. Everything under /apps/family/storage/
 * is routed here and nowhere else - the shell mounts this one component and
 * knows nothing about the screens below.
 *
 * The URLs were settled in TODO_APPS Phase 2, before there were screens behind
 * them, because `c/:code` is what gets printed onto a label and taped to a box
 * for a decade - it can't be renamed afterwards.
 */
export default function StorageApp() {
  return (
    <>
      <nav className="storage-subnav">
        <NavLink to={itemsPath} className={subnavClass}>
          Items
        </NavLink>
        <NavLink to={cratesPath} className={subnavClass}>
          Crates
        </NavLink>
        <NavLink to={locationsPath} className={subnavClass}>
          Locations
        </NavLink>
      </nav>

      <Routes>
        {/* Opening the app is nearly always someone looking for something, not
            browsing their own boxes - so the module's front door is the index.
            It redirects rather than rendering in place, so the URL and the
            highlighted tab agree however you arrived. */}
        <Route index element={<Navigate to={itemsPath} replace />} />
        <Route path="items" element={<ItemIndexPage />} />
        <Route path="crates" element={<CrateListPage />} />
        <Route path="crates/:id" element={<CratePage />} />
        <Route path="locations" element={<LocationsPage />} />
        {/* The scan destination: what a QR label resolves to. */}
        <Route path="c/:code" element={<CratePage />} />
        <Route path="*" element={<Navigate to={itemsPath} replace />} />
      </Routes>
    </>
  );
}

const subnavClass = ({ isActive }: { isActive: boolean }) =>
  `storage-subnav-link${isActive ? ' active' : ''}`;
