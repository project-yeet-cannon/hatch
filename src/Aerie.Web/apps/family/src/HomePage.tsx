import { Link } from 'react-router-dom';
import { modules } from './modules/registry';

/** The app picker. Same shape as the Aerie landing page, one card per module. */
export function HomePage() {
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
