import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { EmptyNote, ErrorNote, InlineError, Loading } from '../../components/Notices';
import { useMutation, useResource } from '../../lib/useResource';
import { usePolling } from '../../lib/usePolling';
import { addItem, clearChecked, deleteItem, getList, setItemChecked, updateItem } from './api';
import { ItemRow, ListForm } from './components';
import { listsPath } from './routes';
import type { Item, ListDetail } from './types';

/** How often the open list re-reads while it's on screen. Seconds, per the aisle case - not milliseconds. */
const POLL_MS = 10_000;

/** How long after a toggle the server's ordering is fetched, coalescing a run of taps into one read. */
const RESORT_MS = 500;

/**
 * An optimistic check is defended against incoming reads for this long, then
 * abandoned. It has to expire: if another device unchecks the thing you just
 * checked, the server is right and the two must converge. Three poll cycles is
 * long enough that a slow write is never overruled and short enough that a real
 * disagreement resolves while you're still looking at it.
 */
const OVERRIDE_TTL_MS = 30_000;

interface Override {
  isChecked: boolean;
  since: number;
}

export function ListPage() {
  const { listId = '' } = useParams<{ listId: string }>();
  const detail = useResource<ListDetail>(`gather:list:${listId}`, (signal) => getList(listId, signal));
  const navigate = useNavigate();

  const [editingId, setEditingId] = useState<string | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);

  /*
    Optimistic check state. Checking things off is the one interaction that
    happens while walking, so it cannot wait on a round trip - but the poll it
    races is not trustworthy on its own: the service worker serves the last good
    body when a fetch fails (see src/sw.js), so a read can arrive stale and
    un-check something the server already accepted. These overrides sit on top
    of whatever a read brings back, and clear when the server agrees or the TTL
    runs out - whichever comes first.
  */
  const [overrides, setOverrides] = useState<Record<string, Override>>({});
  const [inflight, setInflight] = useState<string[]>([]);
  const toggleError = useMutation();
  const resortTimer = useRef<number | undefined>(undefined);

  const serverItems = detail.data?.items;

  // A read landed: drop every override the server has caught up with, and every
  // one that has been arguing with it for too long.
  useEffect(() => {
    if (!serverItems) return;

    setOverrides((prev) => {
      const now = Date.now();
      const next: Record<string, Override> = {};
      let changed = false;

      for (const [id, override] of Object.entries(prev)) {
        const item = serverItems.find((i) => i.id === id);
        const settled = !item || item.isChecked === override.isChecked || now - override.since > OVERRIDE_TTL_MS;
        if (settled) changed = true;
        else next[id] = override;
      }

      return changed ? next : prev;
    });
  }, [serverItems]);

  useEffect(() => () => clearTimeout(resortTimer.current), []);

  usePolling(POLL_MS, detail.refresh);

  const items = useMemo(() => {
    if (!serverItems) return [];
    if (Object.keys(overrides).length === 0) return serverItems;

    // State is overridden; order is not. The row stays where it is until a read
    // brings back the server's ordering, so a run of taps doesn't shuffle the
    // list out from under the finger making them.
    return serverItems.map((item) => {
      const override = overrides[item.id];
      return override ? { ...item, isChecked: override.isChecked } : item;
    });
  }, [serverItems, overrides]);

  const toggle = useCallback(
    async (item: Item, isChecked: boolean) => {
      setOverrides((prev) => ({ ...prev, [item.id]: { isChecked, since: Date.now() } }));
      setInflight((prev) => [...prev, item.id]);

      const ok = await toggleError.run(() => setItemChecked(item.listId, item.id, isChecked));

      setInflight((prev) => prev.filter((id) => id !== item.id));
      if (!ok) {
        // The guess was wrong, so stop defending it and let the next read say so.
        setOverrides((prev) => {
          const { [item.id]: _dropped, ...rest } = prev;
          return rest;
        });
        return;
      }

      clearTimeout(resortTimer.current);
      resortTimer.current = window.setTimeout(detail.refresh, RESORT_MS);
    },
    [detail.refresh, toggleError],
  );

  if (detail.loading && !detail.data) return <Loading />;
  if (detail.status === 404) {
    return (
      <div className="note">
        <p>That list is gone.</p>
        <Link to={listsPath}>All lists</Link>
      </div>
    );
  }
  if (detail.error) return <ErrorNote message={detail.error} onRetry={detail.reload} />;
  if (!detail.data) return null;

  const { list } = detail.data;
  const checkedCount = items.filter((i) => i.isChecked).length;

  return (
    <>
      <div className="gather-head">
        <Link to={listsPath} className="gather-back">
          ‹ Lists
        </Link>
        <h2>{list.name}</h2>
        <button type="button" className="gather-link-btn" onClick={() => setSettingsOpen((on) => !on)}>
          {settingsOpen ? 'Done' : 'Edit'}
        </button>
      </div>

      {settingsOpen && (
        <ListForm
          list={list}
          onSaved={(saved) => {
            detail.set({ ...detail.data!, list: saved });
            setSettingsOpen(false);
          }}
          onCancel={() => setSettingsOpen(false)}
          onDeleted={() => navigate(listsPath, { replace: true })}
        />
      )}

      {toggleError.error && <InlineError message={toggleError.error} />}
      {detail.stale && <p className="gather-stale">Couldn’t refresh — showing what was last loaded.</p>}

      {items.length === 0 ? (
        <EmptyNote>Nothing on this list. Add the first thing below.</EmptyNote>
      ) : (
        <ul className="gather-items">
          {items.map((item) =>
            item.id === editingId ? (
              <ItemEditor
                key={item.id}
                item={item}
                onDone={() => {
                  setEditingId(null);
                  detail.refresh();
                }}
                onCancel={() => setEditingId(null)}
              />
            ) : (
              <ItemRow
                key={item.id}
                item={item}
                pending={inflight.includes(item.id)}
                onToggle={(isChecked) => void toggle(item, isChecked)}
                onEdit={() => setEditingId(item.id)}
              />
            ),
          )}
        </ul>
      )}

      {checkedCount > 0 && <ClearChecked listId={list.id} count={checkedCount} onCleared={detail.refresh} />}

      <AddItemForm listId={list.id} onAdded={detail.refresh} />
    </>
  );
}

/**
 * The add field, pinned to the bottom above the tab bar.
 *
 * Deliberately the opposite of the kiosk overlay, which puts its field at the
 * top: a phone browser lifts a focused input above the keyboard, and on the
 * wall tablet whether the viewport resizes at all is untested
 * (docs/kiosk-architecture.md, "Text entry on the wall"). Same field, two right answers.
 */
function AddItemForm({ listId, onAdded }: { listId: string; onAdded: () => void }) {
  const [name, setName] = useState('');
  const field = useRef<HTMLInputElement>(null);
  const add = useMutation();

  async function submit(event: FormEvent) {
    event.preventDefault();
    const trimmed = name.trim();
    if (!trimmed) return;

    if (await add.run(() => addItem(listId, { name: trimmed, quantity: null, note: null }))) {
      setName('');
      onAdded();
      // Adding to a list is a run, not a single act - the field keeps the caret
      // so the next thing goes in without re-tapping it.
      field.current?.focus();
    }
  }

  return (
    <form className="gather-add" onSubmit={submit}>
      <div className="gather-add-row">
        <input
          ref={field}
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Add an item"
          aria-label="Item name"
          enterKeyHint="done"
          autoCapitalize="words"
          // Autocorrect mangling a brand name is worse than a lowercase one.
          autoCorrect="off"
          spellCheck={false}
        />
        <button type="submit" className="btn-primary" disabled={add.busy || !name.trim()}>
          Add
        </button>
      </div>
      {add.error && <InlineError message={add.error} />}
    </form>
  );
}

/**
 * Name, quantity and note. Never touches whether the item is checked - that is
 * the row itself, and keeping the two apart is what stops a phone in an aisle
 * from clobbering a rename typed in the kitchen a second earlier.
 */
function ItemEditor({ item, onDone, onCancel }: { item: Item; onDone: () => void; onCancel: () => void }) {
  const [name, setName] = useState(item.name);
  const [quantity, setQuantity] = useState(item.quantity ?? '');
  const [note, setNote] = useState(item.note ?? '');
  const save = useMutation();
  const remove = useMutation();

  async function submit(event: FormEvent) {
    event.preventDefault();
    const ok = await save.run(() =>
      updateItem(item.listId, item.id, {
        name: name.trim(),
        quantity: quantity.trim() || null,
        note: note.trim() || null,
      }),
    );
    // A 409 - renaming onto something already on the list - leaves the form
    // open with the text intact, because the answer is to change the word, and
    // retyping it is the one thing that shouldn't be necessary.
    if (ok) onDone();
  }

  async function destroy() {
    if (await remove.run(() => deleteItem(item.listId, item.id))) onDone();
  }

  return (
    <li>
      <form className="card gather-form" onSubmit={submit}>
        <label className="gather-field">
          <span>Item</span>
          <input
            type="text"
            value={name}
            onChange={(e) => setName(e.target.value)}
            autoCapitalize="words"
            autoCorrect="off"
            spellCheck={false}
            autoFocus
          />
        </label>

        <label className="gather-field">
          <span>Quantity</span>
          <input
            type="text"
            value={quantity}
            onChange={(e) => setQuantity(e.target.value)}
            placeholder="2 lbs, a dozen, x2"
          />
        </label>

        <label className="gather-field">
          <span>Note</span>
          <input type="text" value={note} onChange={(e) => setNote(e.target.value)} placeholder="The blue one" />
        </label>

        {save.error && <InlineError message={save.error} />}
        {remove.error && <InlineError message={remove.error} />}

        <div className="gather-form-actions">
          <button type="submit" className="btn-primary" disabled={save.busy || !name.trim()}>
            Save
          </button>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="btn-danger gather-form-delete" onClick={destroy} disabled={remove.busy}>
            Delete
          </button>
        </div>
      </form>
    </li>
  );
}

/**
 * No confirm: the button names its own count, and anything swept off by mistake
 * is one tap to re-add. The list delete does ask - that one isn't recoverable.
 */
function ClearChecked({ listId, count, onCleared }: { listId: string; count: number; onCleared: () => void }) {
  const clear = useMutation();

  return (
    <div className="gather-clear">
      <button
        type="button"
        onClick={() => void clear.run(() => clearChecked(listId).then(onCleared))}
        disabled={clear.busy}
      >
        Clear {count} checked
      </button>
      {clear.error && <InlineError message={clear.error} />}
    </div>
  );
}
