import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ErrorNote, InlineError, Loading } from '../../components/Notices';
import { useMutation, useResource } from '../../lib/useResource';
import { useRefreshOnVisible } from '../../lib/usePolling';
import { createList, getLists } from './api';
import { ListForm, ListIcon } from './components';
import { listPath } from './routes';
import type { ListSummary } from './types';

/**
 * Every list in the house. Refreshed when the tab comes back into view rather
 * than on a timer: these counts are glanceable, not live, and the screen
 * remounts anyway on the way back from a list.
 */
export function ListsPage() {
  const lists = useResource<ListSummary[]>('gather:lists', getLists);
  const [creating, setCreating] = useState(false);
  const navigate = useNavigate();

  useRefreshOnVisible(lists.refresh);

  if (lists.loading && !lists.data) return <Loading />;
  if (lists.error) return <ErrorNote message={lists.error} onRetry={lists.reload} />;

  const rows = lists.data ?? [];

  if (rows.length === 0 && !creating) {
    return <EmptyLists onCreated={(list) => navigate(listPath(list.id))} onNew={() => setCreating(true)} />;
  }

  return (
    <>
      <div className="gather-cards">
        {rows.map((list) => (
          <Link key={list.id} to={listPath(list.id)} className="card gather-card">
            <ListIcon icon={list.icon} color={list.color} />
            <span className="gather-card-text">
              <span className="gather-card-title">{list.name}</span>
              <span className="gather-card-sub">{describe(list)}</span>
            </span>
            <span className="gather-card-chevron" aria-hidden="true">
              ›
            </span>
          </Link>
        ))}
      </div>

      {creating ? (
        <ListForm
          onSaved={(list) => navigate(listPath(list.id))}
          onCancel={() => setCreating(false)}
        />
      ) : (
        <button type="button" className="gather-new" onClick={() => setCreating(true)}>
          ＋ New list
        </button>
      )}

      {lists.stale && <p className="gather-stale">Couldn’t refresh — showing what was last loaded.</p>}
    </>
  );
}

/** "4 to get", and what's already in the cart only when there is any. */
function describe(list: ListSummary): string {
  const open = list.openCount === 0 ? 'Nothing to get' : `${list.openCount} to get`;
  return list.checkedCount > 0 ? `${open} · ${list.checkedCount} in the cart` : open;
}

/**
 * First run. The starters are suggestions, not seed rows - nothing ships in the
 * migration assuming a household, because this product gets deployed into
 * someone else's (docs/ethos.md). Tapping one creates it and opens it, so the
 * first list costs one tap rather than a form.
 */
function EmptyLists({ onCreated, onNew }: { onCreated: (list: ListSummary) => void; onNew: () => void }) {
  const create = useMutation();

  const start = (name: string, icon: string, color: string) =>
    create.run(async () => onCreated(await createList({ name, icon, color })));

  return (
    <div className="gather-empty">
      <h2>No lists yet</h2>
      <p className="text-muted">Start with one of these, or name your own.</p>

      <div className="gather-starters">
        {STARTERS.map(([name, icon, color]) => (
          <button
            type="button"
            key={name}
            className="card gather-starter"
            onClick={() => start(name, icon, color)}
            disabled={create.busy}
          >
            <ListIcon icon={icon} color={color} />
            <span>{name}</span>
          </button>
        ))}
      </div>

      {create.error && <InlineError message={create.error} />}

      <button type="button" className="gather-new" onClick={onNew}>
        ＋ New list
      </button>
    </div>
  );
}

const STARTERS: [name: string, icon: string, color: string][] = [
  ['Grocery', '🛒', 'sky'],
  ['Hardware', '🔧', 'clay'],
  ['Pharmacy', '💊', 'moss'],
  ['Warehouse', '📦', 'plum'],
];
