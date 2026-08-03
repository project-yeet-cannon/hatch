import { NavLink } from 'react-router-dom';
import type { DocSummary } from '../types';

const linkClass = ({ isActive }: { isActive: boolean }) => `docs-nav-link${isActive ? ' active' : ''}`;

export function DocList({ docs }: { docs: DocSummary[] }) {
  if (docs.length === 0) {
    return <p className="text-muted">No documents found.</p>;
  }

  return (
    <ul className="docs-nav-list">
      {docs.map((doc) => (
        <li key={doc.slug}>
          <NavLink to={`/${doc.slug}`} className={linkClass}>
            {doc.title}
          </NavLink>
        </li>
      ))}
    </ul>
  );
}
