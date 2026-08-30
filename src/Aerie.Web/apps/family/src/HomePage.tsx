import { Link } from 'react-router-dom';
import type { FamilyModule } from './modules/registry';

/**
 * The app picker. Same shape as the Aerie landing page, one card per module.
 *
 * The list arrives as a prop rather than being read from the registry here: it
 * depends on the session, and two screens deciding for themselves which modules
 * exist is two chances to disagree about it.
 */
export function HomePage({ modules }: { modules: FamilyModule[] }) {
  return (
    <>
      <div className="home-intro">
        <h1>Aerie Family</h1>
        <p>Household apps</p>
      </div>
      <div className="module-list">
        {modules.map((module) => (
          <Link key={module.id} to={`/${module.id}`} className="card module-card">
            <span className="module-card-icon" aria-hidden="true">
              {module.icon}
            </span>
            <span className="module-card-text">
              <span className="module-card-title">{module.title}</span>
              <span className="module-card-tagline">{module.tagline}</span>
            </span>
            <span className="module-card-chevron" aria-hidden="true">
              ›
            </span>
          </Link>
        ))}
      </div>
    </>
  );
}
