import { Navigate, NavLink, Route, Routes } from 'react-router-dom';
import { ThemeSwitch } from '@aerie/ui';
import './App.css';
import { DEFAULT_SLUG, SECTIONS, groupedSections } from './sections';

const navLinkClass = ({ isActive }: { isActive: boolean }) =>
  `design-nav-link${isActive ? ' active' : ''}`;

export function App() {
  const groups = groupedSections();

  return (
    <div className="design-app">
      <nav className="design-rail" aria-label="Sections">
        <div className="design-rail-head">
          <span className="design-rail-title">Aerie Design</span>
          <span className="design-rail-sub">The shared vocabulary</span>
        </div>

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

        {/* The most useful thing the gallery does: every page is one click from
            being read in the other theme, without leaving the page. It lives in
            the rail rather than on each page so the comparison is always one
            click away and never scrolled off. Phase 3 moves this control into
            the shared <TopBar>; the component is already the shared one. */}
        <div className="design-rail-foot">
          <ThemeSwitch />
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
  );
}
