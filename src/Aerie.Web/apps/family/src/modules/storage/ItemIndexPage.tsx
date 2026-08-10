import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { getItems } from './api';
import { CodeChip, EmptyNote, ErrorNote, Loading } from './components';
import { cratePath, cratesPath } from './routes';
import type { ItemIndexRow } from './types';
import { useResource } from './useResource';

/**
 * The flat index across every crate - the "where is the drill" screen, and the
 * reason the app exists. Each row already carries its crate and location, so
 * finding something is one screen and one tap, never a drill-down.
 *
 * Filtering is client-side against the whole index. That's deliberate at this
 * size: a few hundred items is a small payload, it filters as fast as someone
 * can type, and it keeps working with no network once the service worker has
 * the list. TODO_APPS Phase 5 adds `GET /api/storage/search` with Postgres
 * full-text, which buys stemming and misspelling tolerance this can't do - at
 * which point this switches over and loses the offline half.
 */
export function ItemIndexPage() {
  const [query, setQuery] = useState('');
  const items = useResource<ItemIndexRow[]>('items', getItems);
  const matches = useMemo(() => filterItems(items.data ?? [], query), [items.data, query]);

  if (items.loading && !items.data) return <Loading />;
  if (items.error) return <ErrorNote message={items.error} onRetry={items.reload} />;

  const all = items.data ?? [];

  return (
    <>
      <input
        type="search"
        className="storage-search"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Search items, crates, locations"
        aria-label="Search"
        enterKeyHint="search"
      />

      {all.length === 0 ? (
        <EmptyNote>
          Nothing stored yet. Start with a <Link to={cratesPath}>crate</Link>.
        </EmptyNote>
      ) : matches.length === 0 ? (
        <EmptyNote>No match for “{query}”.</EmptyNote>
      ) : (
        <>
          <p className="storage-count text-muted">
            {matches.length} of {all.length} items
          </p>
          <ul className="storage-list">
            {matches.map((item) => (
              <li key={item.id}>
                <Link to={cratePath(item.crateId)} className="storage-row">
                  <span className="storage-row-text">
                    <span className="storage-row-title">
                      {item.name}
                      {item.quantity > 1 && <span className="storage-qty-badge">×{item.quantity}</span>}
                    </span>
                    <span className="storage-row-sub">
                      <CodeChip code={item.crateDisplayCode} />
                      {/* One text node, not several: .storage-row-sub is a flex
                          container, and loose strings each become their own
                          flex item with their own gap. */}
                      <span>{[item.crateLabel, item.locationName ?? 'no location'].filter(Boolean).join(' · ')}</span>
                    </span>
                  </span>
                  <span className="storage-row-chevron" aria-hidden="true">
                    ›
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        </>
      )}
    </>
  );
}

/**
 * Every whitespace-separated term has to match somewhere on the row, across all
 * of item, crate and location. That's what makes "drill garage" and "lights
 * attic" work, which is how people actually recall where a thing is - by the
 * item and the place, not by either alone.
 */
function filterItems(items: ItemIndexRow[], query: string): ItemIndexRow[] {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return items;

  return items.filter((item) => {
    const haystack = [
      item.name,
      item.notes,
      item.crateLabel,
      item.crateDisplayCode,
      item.crateCode,
      item.locationName,
    ]
      .filter(Boolean)
      .join(' ')
      .toLowerCase();
    return terms.every((term) => haystack.includes(term));
  });
}
