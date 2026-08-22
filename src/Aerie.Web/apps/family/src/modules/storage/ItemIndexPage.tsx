import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { EmptyNote, ErrorNote, Loading } from '../../components/Notices';
import { useDebounced, useResource } from '../../lib/useResource';
import { getItems, searchItems } from './api';
import { CodeChip } from './components';
import { cratePath, cratesPath } from './routes';
import type { ItemIndexRow } from './types';

/** Long enough to collapse a thumbed word into one query, short enough to feel like typing. */
const SEARCH_DEBOUNCE_MS = 200;

/**
 * The flat index across every crate - the "where is the drill" screen, and the
 * reason the app exists. Each row already carries its crate and location, so
 * finding something is one screen and one tap, never a drill-down.
 *
 * Searching goes to Postgres full-text (`GET /api/storage/search`), which buys
 * stemming ("lights" finds "light"), prefix matching while typing, and matching
 * across item, crate and location at once.
 *
 * The whole index is still loaded, for two reasons: it's the list with an empty
 * search box, and it's what search falls back to with no network. That fallback
 * is the honest half of this screen - it's a plain substring filter, so it can't
 * stem, but a garage is exactly where the signal goes and "no results" would be
 * a lie there.
 */
export function ItemIndexPage() {
  const [query, setQuery] = useState('');
  const term = useDebounced(query.trim(), SEARCH_DEBOUNCE_MS);

  const index = useResource<ItemIndexRow[]>('items', getItems);
  // Keyed on the term, so each settled query is one load and switching terms
  // aborts the one in flight. An empty box is not a search: it's the whole list.
  const results = useResource<ItemIndexRow[] | null>(`search:${term}`, (signal) =>
    term ? searchItems(term, signal) : Promise.resolve(null),
  );

  const all = useMemo(() => index.data ?? [], [index.data]);
  const offlineMatches = useMemo(() => filterItems(all, term), [all, term]);

  if (index.loading && !index.data) return <Loading />;
  if (index.error) return <ErrorNote message={index.error} onRetry={index.reload} />;

  const searching = term !== '';
  // Any search failure lands here, not in a red box: the index is already loaded,
  // so filtering it locally answers the question rather than reporting one.
  const offline = searching && results.error !== null;
  const rows = !searching ? all : offline ? offlineMatches : (results.data ?? []);
  // Only true before the first search of a session resolves - after that the
  // previous term's rows stay on screen rather than blinking through "no match".
  const pending = searching && !offline && results.data === null && results.loading;

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

      {all.length === 0 && !searching ? (
        <EmptyNote>
          Nothing stored yet. Start with a <Link to={cratesPath}>crate</Link>.
        </EmptyNote>
      ) : pending ? (
        <Loading />
      ) : rows.length === 0 ? (
        <EmptyNote>No match for “{term}”.</EmptyNote>
      ) : (
        <>
          <p className="storage-count text-muted">
            {searching ? `${rows.length} ${rows.length === 1 ? 'match' : 'matches'}` : `${all.length} items`}
            {offline && ' · offline, searching the loaded list'}
          </p>
          <ul className="storage-list">
            {rows.map((item) => (
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
 * The offline fallback: every whitespace-separated term has to match somewhere
 * on the row, across all of item, crate and location. Substring rather than
 * word-based, which is the one thing it does that the server doesn't - "rill"
 * finds the drill here and nothing there. The server's stemming and prefix
 * matching are the better trade when there's a network; this is the one that
 * still works when there isn't.
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
