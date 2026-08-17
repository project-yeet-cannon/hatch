import { NavLink } from 'react-router-dom';
import type { DocSummary } from '../types';

const linkClass = ({ isActive }: { isActive: boolean }) => `docs-nav-link${isActive ? ' active' : ''}`;

/** "plans/swarm" -> "Plans / Swarm". Group paths are directory names, so this is all the labelling they need. */
function groupLabel(group: string): string {
  return group
    .split('/')
    .map((segment) => segment.charAt(0).toUpperCase() + segment.slice(1))
    .join(' / ');
}

/** A directory's own index doc leads its section; everything else keeps the server's title order. */
function indexFirst(docs: DocSummary[]): DocSummary[] {
  const isIndex = (doc: DocSummary) => /\/(readme|index)$/i.test(doc.slug);
  return [...docs.filter(isIndex), ...docs.filter((d) => !isIndex(d))];
}

export function DocList({ docs }: { docs: DocSummary[] }) {
  if (docs.length === 0) {
    return <p className="text-muted">No documents found.</p>;
  }

  // Docs arrive sorted by group then title, so first-seen order is already the
  // order to render the sections in - root ("") first, then each subdirectory.
  const groups: string[] = [];
  for (const doc of docs) {
    if (!groups.includes(doc.group)) groups.push(doc.group);
  }

  return (
    <>
      {groups.map((group) => (
        <section key={group} className="docs-nav-group">
          {group && <h2 className="docs-nav-group-title">{groupLabel(group)}</h2>}
          <ul className="docs-nav-list">
            {indexFirst(docs.filter((d) => d.group === group)).map((doc) => (
              <li key={doc.slug}>
                <NavLink to={`/${doc.slug}`} className={linkClass}>
                  {doc.title}
                </NavLink>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </>
  );
}
