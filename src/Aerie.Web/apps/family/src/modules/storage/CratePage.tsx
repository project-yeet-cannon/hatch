import { useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { createItem, deleteCrate, deleteItem, getCrate, getCrateByCode, getLocations, updateCrate, updateItem } from './api';
import { CodeChip, EmptyNote, ErrorNote, InlineError, Loading } from './components';
import { cratesPath, reprintPath } from './routes';
import type { CrateDetail, Item, Location } from './types';
import { useMutation, useResource } from './useResource';

/**
 * The scan destination, and the screen that has to be instant: everything it
 * draws arrives in the single `crates/by-code` (or `crates/{id}`) response, so
 * there is never a second round trip between pointing a camera at a box and
 * seeing what's in it.
 *
 * One component serves both routes. `/c/:code` is what a label carries and what
 * the stock iOS camera opens; `/crates/:id` is what the in-app lists link to,
 * because they already know the id and shouldn't round-trip through a lookup.
 */
export function CratePage() {
  const { id, code } = useParams<{ id?: string; code?: string }>();
  const crate = useResource<CrateDetail>(code ? `code:${code}` : `id:${id}`, (signal) =>
    code ? getCrateByCode(code, signal) : getCrate(id!, signal),
  );

  if (crate.loading && !crate.data) return <Loading />;

  // A 404 on the scan path is its own thing, not a generic failure: someone is
  // standing at a box holding a label that didn't resolve, and the useful reply
  // is "check the characters", not "request failed".
  if (crate.status === 404) {
    return (
      <div className="storage-note">
        <p>
          No crate {code ? <CodeChip code={code.toUpperCase()} /> : 'here'}.
        </p>
        <p className="text-muted">
          If you typed it off a label, check the characters — the code has no I, L, O or U.
        </p>
        <Link to={cratesPath}>All crates</Link>
      </div>
    );
  }

  if (crate.error) return <ErrorNote message={crate.error} onRetry={crate.reload} />;
  if (!crate.data) return null;

  return <CrateView detail={crate.data} onChanged={crate.reload} />;
}

function CrateView({ detail, onChanged }: { detail: CrateDetail; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);
  const { crate, items } = detail;

  return (
    <>
      <div className="card crate-head">
        <div className="crate-head-top">
          <CodeChip code={crate.displayCode} />
          <button className="storage-link-btn" onClick={() => setEditing((on) => !on)}>
            {editing ? 'Done' : 'Edit'}
          </button>
        </div>
        <h2 className={crate.label ? undefined : 'text-muted'}>{crate.label ?? 'Unlabelled crate'}</h2>
        <p className="text-muted crate-head-meta">
          {crate.locationName ?? 'Not in a location'} · {items.length} {items.length === 1 ? 'item' : 'items'}
        </p>
        {crate.notes && <p className="crate-head-notes">{crate.notes}</p>}
      </div>

      {editing && <CrateForm crate={detail} onSaved={onChanged} onClose={() => setEditing(false)} />}

      <AddItemForm crateId={crate.id} onAdded={onChanged} />

      {items.length === 0 ? (
        <EmptyNote>Empty crate. Add what goes in it as you pack.</EmptyNote>
      ) : (
        <ul className="storage-list">
          {items.map((item) => (
            <ItemRow key={item.id} item={item} onChanged={onChanged} />
          ))}
        </ul>
      )}
    </>
  );
}

/** Label, location and notes. The code is absent on purpose - it's taped to a box. */
function CrateForm({ crate: detail, onSaved, onClose }: { crate: CrateDetail; onSaved: () => void; onClose: () => void }) {
  const { crate } = detail;
  const [label, setLabel] = useState(crate.label ?? '');
  const [locationId, setLocationId] = useState(crate.locationId ?? '');
  const [notes, setNotes] = useState(crate.notes ?? '');
  const save = useMutation();
  const remove = useMutation();
  const navigate = useNavigate();
  const locations = useResource<Location[]>('locations', getLocations);

  async function submit(event: FormEvent) {
    event.preventDefault();
    const ok = await save.run(() =>
      updateCrate(crate.id, {
        label: label.trim() || null,
        locationId: locationId || null,
        notes: notes.trim() || null,
      }),
    );
    if (ok) {
      onSaved();
      onClose();
    }
  }

  async function destroy() {
    const named = crate.label ? `"${crate.label}"` : crate.displayCode;
    const contents = detail.items.length > 0 ? ` and the ${detail.items.length} items in it` : '';
    if (!confirm(`Delete crate ${named}${contents}?`)) return;

    if (await remove.run(() => deleteCrate(crate.id))) navigate(cratesPath, { replace: true });
  }

  return (
    <form className="card storage-form" onSubmit={submit}>
      <label className="storage-field">
        <span>Label</span>
        <input
          type="text"
          value={label}
          onChange={(e) => setLabel(e.target.value)}
          placeholder="Christmas decorations"
          autoFocus
        />
      </label>

      <label className="storage-field">
        <span>Location</span>
        <select value={locationId} onChange={(e) => setLocationId(e.target.value)}>
          <option value="">— none —</option>
          {(locations.data ?? []).map((location) => (
            <option key={location.id} value={location.id}>
              {location.name}
            </option>
          ))}
        </select>
      </label>

      <label className="storage-field">
        <span>Notes</span>
        <textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
      </label>

      {save.error && <InlineError message={save.error} />}
      {remove.error && <InlineError message={remove.error} />}

      <div className="storage-form-actions">
        <button type="submit" className="btn-primary" disabled={save.busy}>
          {save.busy ? 'Saving…' : 'Save'}
        </button>
        <button type="button" onClick={onClose}>
          Cancel
        </button>
        {/* Reprinting one label: a code that's been through a decade of garage,
            or a box that got re-taped. The sheet is the same one the batch
            flow prints, with a single cell on it. */}
        <Link to={reprintPath([crate.code])} className="storage-link-btn">
          Print label
        </Link>
        <button type="button" className="btn-danger storage-form-delete" onClick={destroy} disabled={remove.busy}>
          Delete
        </button>
      </div>
    </form>
  );
}

/**
 * Always open, above the list: the crate screen exists to be filled in while
 * holding the box, so putting the next item behind an "Add" tap is a tap too
 * many. The name field keeps focus after a save for the same reason.
 */
function AddItemForm({ crateId, onAdded }: { crateId: string; onAdded: () => void }) {
  const [name, setName] = useState('');
  const [quantity, setQuantity] = useState('1');
  const nameInput = useRef<HTMLInputElement>(null);
  const add = useMutation();

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!name.trim()) return;

    const ok = await add.run(() =>
      createItem({ crateId, name: name.trim(), quantity: parseQuantity(quantity), notes: null }),
    );
    if (ok) {
      setName('');
      setQuantity('1');
      onAdded();
      // Unpacking a box is a run of items, not one. Returning focus keeps the
      // keyboard up so the next one is typing, not tapping the field again.
      nameInput.current?.focus();
    }
  }

  return (
    <form className="storage-add" onSubmit={submit}>
      <div className="storage-add-row">
        <input
          ref={nameInput}
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Add an item"
          aria-label="Item name"
          enterKeyHint="done"
        />
        <input
          type="text"
          inputMode="numeric"
          className="storage-qty"
          value={quantity}
          onChange={(e) => setQuantity(e.target.value)}
          aria-label="Quantity"
        />
        <button type="submit" className="btn-primary" disabled={add.busy || !name.trim()}>
          Add
        </button>
      </div>
      {add.error && <InlineError message={add.error} />}
    </form>
  );
}

function ItemRow({ item, onChanged }: { item: Item; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);

  if (editing) return <ItemForm item={item} onDone={onChanged} onClose={() => setEditing(false)} />;

  return (
    <li>
      <button className="storage-row storage-row-button" onClick={() => setEditing(true)}>
        <span className="storage-row-text">
          <span className="storage-row-title">{item.name}</span>
          {item.notes && <span className="storage-row-sub">{item.notes}</span>}
        </span>
        {item.quantity > 1 && <span className="storage-qty-badge">×{item.quantity}</span>}
      </button>
    </li>
  );
}

/** Editing an item in place, because the first pass at a crate is always slightly wrong. */
function ItemForm({ item, onDone, onClose }: { item: Item; onDone: () => void; onClose: () => void }) {
  const [name, setName] = useState(item.name);
  const [quantity, setQuantity] = useState(String(item.quantity));
  const [notes, setNotes] = useState(item.notes ?? '');
  const save = useMutation();
  const remove = useMutation();

  async function submit(event: FormEvent) {
    event.preventDefault();
    const ok = await save.run(() =>
      updateItem(item.id, {
        crateId: item.crateId,
        name: name.trim(),
        quantity: parseQuantity(quantity),
        notes: notes.trim() || null,
      }),
    );
    if (ok) {
      onDone();
      onClose();
    }
  }

  async function destroy() {
    if (!confirm(`Remove "${item.name}"?`)) return;
    if (await remove.run(() => deleteItem(item.id))) onDone();
  }

  return (
    <li>
      <form className="card storage-form" onSubmit={submit}>
        <div className="storage-add-row">
          <input type="text" value={name} onChange={(e) => setName(e.target.value)} aria-label="Item name" autoFocus />
          <input
            type="text"
            inputMode="numeric"
            className="storage-qty"
            value={quantity}
            onChange={(e) => setQuantity(e.target.value)}
            aria-label="Quantity"
          />
        </div>

        <label className="storage-field">
          <span>Notes</span>
          <textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>

        {save.error && <InlineError message={save.error} />}
        {remove.error && <InlineError message={remove.error} />}

        <div className="storage-form-actions">
          <button type="submit" className="btn-primary" disabled={save.busy || !name.trim()}>
            Save
          </button>
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button type="button" className="btn-danger storage-form-delete" onClick={destroy} disabled={remove.busy}>
            Remove
          </button>
        </div>
      </form>
    </li>
  );
}

/**
 * An empty or half-typed quantity box means one, not NaN. The server rejects
 * anything below 1 anyway; this just keeps a normal typing pause from producing
 * an error message.
 */
function parseQuantity(raw: string): number {
  const parsed = Number.parseInt(raw, 10);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 1;
}
