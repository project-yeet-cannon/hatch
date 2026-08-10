import { NavLink, Route, Routes, useParams } from 'react-router-dom';
import './storage.css';

/**
 * Storage Helper's slice of the shell. Everything under /apps/family/storage/
 * is routed here and nowhere else - the shell mounts this one component and
 * knows nothing about the screens below.
 *
 * TODO_APPS Phase 2 puts the module seam and these routes in place; Phase 3
 * fills the screens in and Phase 4 adds QR labels. The URLs are settled now
 * rather than later because `c/:code` is what gets printed onto a label and
 * taped to a box for a decade - it can't be renamed afterwards.
 */
export default function StorageApp() {
  return (
    <>
      <nav className="storage-subnav">
        <NavLink to="items" className={subnavClass}>
          Items
        </NavLink>
        <NavLink to="crates" className={subnavClass}>
          Crates
        </NavLink>
        <NavLink to="locations" className={subnavClass}>
          Locations
        </NavLink>
      </nav>

      <Routes>
        <Route index element={<Pending screen="Item index" />} />
        <Route path="items" element={<Pending screen="Item index" />} />
        <Route path="crates" element={<Pending screen="Crate list" />} />
        <Route path="locations" element={<Pending screen="Locations" />} />
        {/* The scan destination: what a QR label resolves to. */}
        <Route path="c/:code" element={<CratePlaceholder />} />
        <Route path="*" element={<Pending screen="Item index" />} />
      </Routes>
    </>
  );
}

const subnavClass = ({ isActive }: { isActive: boolean }) =>
  `storage-subnav-link${isActive ? ' active' : ''}`;

function Pending({ screen }: { screen: string }) {
  return (
    <div className="card storage-pending">
      <h3>{screen}</h3>
      <p className="text-muted">Built in Phase 3.</p>
    </div>
  );
}

/**
 * Proves the scan path end to end before there's a crate view to show: a hard
 * refresh of /apps/family/storage/c/ABC-123 has to reach this component, which
 * is the whole reason Program.cs grew a fallback route for /apps/family.
 */
function CratePlaceholder() {
  const { code } = useParams<{ code: string }>();
  return (
    <div className="card storage-pending">
      <h3>Crate {code}</h3>
      <p className="text-muted">Scanned. The crate view is built in Phase 3.</p>
    </div>
  );
}
