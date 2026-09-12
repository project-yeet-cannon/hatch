import { Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { TopBar } from '@hatch/ui';
import './App.css';
import { DEFAULT_SLUG, SECTIONS, groupedSections } from './sections';

const navLinkClass = ({ isActive }: { isActive: boolean }) =>
  `design-nav-link${isActive ? ' active' : ''}`;

export function App() {
  const groups = groupedSections();

  return (
    <div className="design-app">
      {/* The gallery wears the bar it documents. That is not decoration: the
          one context a top bar is never shown in on a specimen page is a real
          app, and this app is the one that can show it in both at once. */}
      <TopBar appName="Hatch Design" />

      <div className="design-body">
        <nav className="design-rail" aria-label="Sections">
          <div className="design-rail-groups">
            {groups.map((group) => (
              <div key={group.group} className="design-nav-group">
                <h2 className="design-nav-group-name">{group.group}</h2>
                {group.sections.map((section) => (
                  <NavLink key={section.slug} to={`/${section.slug}`} className={navLinkClass}>
                    {section.title}
                  </NavLink>
                ))}
              </div>
            ))}
          </div>
        </nav>

        <main className="design-content">
          <Routes>
            <Route path="/" element={<Navigate to={`/${DEFAULT_SLUG}`} replace />} />
            {SECTIONS.map(({ slug, Page }) => (
              <Route key={slug} path={`/${slug}`} element={<Page />} />
            ))}
            <Route path="*" element={<Navigate to={`/${DEFAULT_SLUG}`} replace />} />
          </Routes>
        </main>
      </div>
    </div>
  );
}
