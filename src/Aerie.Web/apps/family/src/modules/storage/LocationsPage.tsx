import { useState } from 'react';
import type { FormEvent } from 'react';
import { EmptyNote, ErrorNote, InlineError, Loading } from '../../components/Notices';
import { useMutation, useResource } from '../../lib/useResource';
import { createLocation, deleteLocation, getLocations, updateLocation } from './api';
import type { Location } from './types';

/**
 * Where crates live. Flat by design - nesting is a guess until someone actually
 * misses it, and a flat list is what a phone screen wants anyway.
 */
export function LocationsPage() {
  const locations = useResource<Location[]>('locations', getLocations);

  if (locations.loading && !locations.data) return <Loading />;
  if (locations.error) return <ErrorNote message={locations.error} onRetry={locations.reload} />;

  const rows = locations.data ?? [];

  return (
    <>
      <NewLocationForm onCreated={locations.reload} />

      {rows.length === 0 ? (
        <EmptyNote>No locations yet. “Garage”, “Attic”, “Basement shelf 3”.</EmptyNote>
      ) : (
        <ul className="storage-list">
          {rows.map((location) => (
            <LocationRow key={location.id} location={location} onChanged={locations.reload} />
          ))}
        </ul>
      )}
    </>
  );
}

function NewLocationForm({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState('');
  const create = useMutation();

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!name.trim()) return;

    if (await create.run(() => createLocation({ name: name.trim(), description: null }))) {
      setName('');
      onCreated();
    }
  }

  return (
    <form className="storage-add" onSubmit={submit}>
      <div className="storage-add-row">
        <input
          type="text"
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Add a location"
          aria-label="Location name"
          enterKeyHint="done"
        />
        <button type="submit" className="btn-primary" disabled={create.busy || !name.trim()}>
          Add
        </button>
      </div>
      {create.error && <InlineError message={create.error} />}
    </form>
  );
}

function LocationRow({ location, onChanged }: { location: Location; onChanged: () => void }) {
  const [editing, setEditing] = useState(false);

  if (editing) return <LocationForm location={location} onDone={onChanged} onClose={() => setEditing(false)} />;

  return (
    <li>
      <button className="storage-row storage-row-button" onClick={() => setEditing(true)}>
        <span className="storage-row-text">
          <span className="storage-row-title">{location.name}</span>
          <span className="storage-row-sub">
            {location.crateCount} {location.crateCount === 1 ? 'crate' : 'crates'}
            {location.description && ` · ${location.description}`}
          </span>
        </span>
      </button>
    </li>
  );
}

function LocationForm({
  location,
  onDone,
  onClose,
}: {
  location: Location;
  onDone: () => void;
  onClose: () => void;
}) {
  const [name, setName] = useState(location.name);
  const [description, setDescription] = useState(location.description ?? '');
  const save = useMutation();
  const remove = useMutation();

  async function submit(event: FormEvent) {
    event.preventDefault();
    const ok = await save.run(() =>
      updateLocation(location.id, { name: name.trim(), description: description.trim() || null }),
    );
    if (ok) {
      onDone();
      onClose();
    }
  }

  /**
   * Deleting a location does not delete its crates - they go unplaced, because
   * losing a shelf must not silently lose the record of everything on it. Worth
   * saying in the prompt: it's the opposite of what "delete" usually implies.
   */
  async function destroy() {
    const fate =
      location.crateCount > 0
        ? ` The ${location.crateCount} ${location.crateCount === 1 ? 'crate' : 'crates'} in it will stay, with no location.`
        : '';
    if (!confirm(`Delete "${location.name}"?${fate}`)) return;

    if (await remove.run(() => deleteLocation(location.id))) onDone();
  }

  return (
    <li>
      <form className="card storage-form" onSubmit={submit}>
        <label className="storage-field">
          <span>Name</span>
          <input type="text" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
        </label>

        <label className="storage-field">
          <span>Description</span>
          <input type="text" value={description} onChange={(e) => setDescription(e.target.value)} />
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
            Delete
          </button>
        </div>
      </form>
    </li>
  );
}
