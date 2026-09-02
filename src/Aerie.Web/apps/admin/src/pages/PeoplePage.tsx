import { useEffect, useRef, useState } from 'react';
import { Badge, Button, Card, Field, Modal, PageHeader, Table, Text } from '@aerie/ui';
import {
  createPerson,
  deletePerson,
  deletePersonPhoto,
  getPeople,
  getPersonSessions,
  personPhotoUrl,
  updatePerson,
  uploadPersonPhoto,
} from '../api/client';
import { downscaleImage, initials } from '../lib/avatar';
import { formatAge } from '../lib/format';
import type { Person, PersonSession } from '../types';

/**
 * Everyone the household knows about.
 *
 * A person here is not an account. There is nothing to sign in as and no
 * password anywhere in this app - the wall authenticates a *device*
 * (docs/auth-architecture.md), and this page is where a device acquires a human
 * to point at. Which is why the list starts empty and stays that way until
 * someone is added: an install with no people is not a broken install.
 *
 * The link to a session is deliberately read-only here. One session has at most
 * one person, so it is a single value on a row that already exists over on the
 * Sessions page - inverting it into "pick this person's devices" would turn a
 * one-to-many into a multi-picker, on the page nobody is looking at during the
 * one moment it is wanted, which is while enrolling the device.
 */
export function PeoplePage() {
  const [people, setPeople] = useState<Person[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Person | 'new' | null>(null);
  // Which row has its devices open. One at a time, because the answer is a
  // handful of rows and a page of every person's every device is a page.
  const [expanded, setExpanded] = useState<string | null>(null);

  useEffect(() => {
    load();
  }, []);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      setPeople(await getPeople());
    } catch (err) {
      setError(message(err));
    } finally {
      setLoading(false);
    }
  }

  async function handleDelete(person: Person) {
    const devices =
      person.sessionCount === 0
        ? ''
        : ` Their ${person.sessionCount === 1 ? 'device stays' : `${person.sessionCount} devices stay`} enrolled — just unclaimed.`;
    if (!confirm(`Delete “${person.name}”?${devices}`)) return;

    setError(null);
    try {
      await deletePerson(person.id);
      setPeople((prev) => prev.filter((p) => p.id !== person.id));
    } catch (err) {
      setError(message(err));
    }
  }

  function replace(person: Person) {
    setPeople((prev) => {
      const others = prev.filter((p) => p.id !== person.id);
      return [...others, person].sort((a, b) => a.name.localeCompare(b.name));
    });
  }

  return (
    <div>
      <PageHeader
        title="People"
        actions={
          <>
            <Button variant="primary" onClick={() => setEditing('new')}>
              Add person
            </Button>
            <Button onClick={load}>Refresh</Button>
          </>
        }
      />

      {error && <Text tone="danger" className="mb-2">{error}</Text>}
      {loading && <Text tone="muted">Loading…</Text>}

      {!loading && people.length === 0 && (
        <Card>
          <p>
            Nobody here yet. <strong>Add person</strong> creates someone the household knows about — a name, and a
            photo if you want one.
          </p>
          <Text tone="muted">
            A person isn’t an account. There’s nothing to sign in as; it’s the human a device and a log line can point
            at, which is what makes “who opened the dashboard at 6am” a question with an answer.
          </Text>
        </Card>
      )}

      {!loading && people.length > 0 && (
        <Card>
          <Table>
            <thead>
              <tr>
                <th />
                <th>Name</th>
                <th>Sessions</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {people.map((person) => (
                <PersonRow
                  key={person.id}
                  person={person}
                  expanded={expanded === person.id}
                  onToggle={() => setExpanded(expanded === person.id ? null : person.id)}
                  onChanged={replace}
                  onEdit={() => setEditing(person)}
                  onDelete={() => handleDelete(person)}
                  onError={setError}
                />
              ))}
            </tbody>
          </Table>
        </Card>
      )}

      <PersonModal
        person={editing}
        onClose={() => setEditing(null)}
        onSaved={(person) => {
          replace(person);
          setEditing(null);
        }}
      />
    </div>
  );
}

function PersonRow({
  person,
  expanded,
  onToggle,
  onChanged,
  onEdit,
  onDelete,
  onError,
}: {
  person: Person;
  expanded: boolean;
  onToggle: () => void;
  onChanged: (person: Person) => void;
  onEdit: () => void;
  onDelete: () => void;
  onError: (error: string) => void;
}) {
  const fileInput = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);

  async function pickPhoto(file: File | undefined) {
    if (!file) return;

    setBusy(true);
    try {
      // Shrunk here rather than on the server: the file a phone hands over is a
      // photo of someone in a garden, and what this table draws is a circle
      // 40 pixels across. The server still caps and sniffs whatever arrives.
      onChanged(await uploadPersonPhoto(person.id, await downscaleImage(file)));
    } catch (err) {
      onError(message(err));
    } finally {
      setBusy(false);
      // Cleared so picking the same file twice in a row still fires a change.
      if (fileInput.current) fileInput.current.value = '';
    }
  }

  async function removePhoto() {
    setBusy(true);
    try {
      await deletePersonPhoto(person.id);
      onChanged({ ...person, hasPhoto: false, photoUpdatedAt: null });
    } catch (err) {
      onError(message(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <tr>
        <td>
          <button
            className="person-avatar-button"
            disabled={busy}
            title={person.hasPhoto ? 'Change photo' : 'Add a photo'}
            onClick={() => fileInput.current?.click()}
          >
            <Avatar person={person} />
          </button>
          <input
            ref={fileInput}
            type="file"
            accept="image/png,image/jpeg,image/gif,image/webp"
            hidden
            onChange={(e) => pickPhoto(e.target.files?.[0])}
          />
        </td>
        <td>
          <div className="flex gap-1" style={{ alignItems: 'center' }}>
            <span>{person.name}</span>
            {person.isAdmin && (
              /* Muted rather than coloured on purpose. Whether this badge means
                 anything depends on the install's ADMIN_MODE, which the client
                 has no way to ask about - a badge that looked like a live
                 permission would be a lie on the installs where it is not. */
              <Badge title="Served this app, and the operator verbs behind it — where the install enforces it. See docs/auth-architecture.md, “The admin flag”.">
                Admin
              </Badge>
            )}
          </div>
        </td>
        <td>
          {person.sessionCount === 0 ? (
            <Text as="span" tone="muted">None</Text>
          ) : (
            <button className="person-link" onClick={onToggle}>
              {person.sessionCount} {person.sessionCount === 1 ? 'device' : 'devices'}
            </button>
          )}
        </td>
        <td>
          <div className="flex gap-1" style={{ justifyContent: 'flex-end' }}>
            {person.hasPhoto && (
              <Button disabled={busy} onClick={removePhoto}>
                Remove photo
              </Button>
            )}
            <Button onClick={onEdit}>
              Edit
            </Button>
            <Button variant="danger" onClick={onDelete}>
              Delete
            </Button>
          </div>
        </td>
      </tr>
      {expanded && <SessionsRow person={person} onError={onError} />}
    </>
  );
}

/** The person's devices, fetched when the row is opened rather than with the list - most rows are never opened. */
function SessionsRow({ person, onError }: { person: Person; onError: (error: string) => void }) {
  const [sessions, setSessions] = useState<PersonSession[] | null>(null);

  useEffect(() => {
    getPersonSessions(person.id).then(setSessions, (err: unknown) => onError(message(err)));
  }, [person.id, onError]);

  return (
    <tr>
      <td />
      <td colSpan={3}>
        {sessions === null ? (
          <Text tone="muted">Loading…</Text>
        ) : (
          <ul className="person-sessions">
            {sessions.map((session) => (
              <li key={session.id}>
                {session.label}{' '}
                <Text as="span" tone="muted">
                  · {session.kind === 'Device' ? 'tablet' : 'browser'} · last seen{' '}
                  {session.lastSeenAt ? formatAge(session.lastSeenAt) : 'never'}
                </Text>
              </li>
            ))}
          </ul>
        )}
        <Text tone="muted">Devices are claimed and revoked on the Sessions page.</Text>
      </td>
    </tr>
  );
}

function Avatar({ person }: { person: Person }) {
  if (!person.hasPhoto) {
    return (
      <span className="person-avatar person-avatar-empty" aria-hidden>
        {initials(person.name)}
      </span>
    );
  }

  return <img className="person-avatar" src={personPhotoUrl(person)} alt="" />;
}

/**
 * Create and edit are one form because they are one form: a name and a
 * checkbox. The photo is not here - it needs an id to attach to, so a new
 * person gets one from their row a moment later, and the row is where you are
 * already looking when you think "she needs a picture".
 */
function PersonModal({
  person,
  onClose,
  onSaved,
}: {
  person: Person | 'new' | null;
  onClose: () => void;
  onSaved: (person: Person) => void;
}) {
  const [name, setName] = useState('');
  const [isAdmin, setIsAdmin] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (person === null) return;
    setName(person === 'new' ? '' : person.name);
    setIsAdmin(person === 'new' ? false : person.isAdmin);
    setError(null);
  }, [person]);

  async function save() {
    if (person === null) return;

    setSaving(true);
    setError(null);
    try {
      const request = { name, isAdmin };
      onSaved(person === 'new' ? await createPerson(request) : await updatePerson(person.id, request));
    } catch (err) {
      setError(message(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal open={person !== null} onClose={onClose} title={person === 'new' ? 'Add person' : 'Edit person'}>
      <Field
        label="Name"
        className="mb-2"
        hint="Anything you like, emoji included — up to 60 characters. This is a home, not a directory."
      >
        <input
          type="text"
          value={name}
          autoFocus
          placeholder="Ada"
          onChange={(e) => setName(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') save();
          }}
        />
      </Field>

      <label className="flex gap-1 mb-2" style={{ alignItems: 'center' }}>
        <input type="checkbox" checked={isAdmin} onChange={(e) => setIsAdmin(e.target.checked)} />
        <span>Administrator</span>
      </label>
      <Text tone="muted" className="mb-2">
        Who gets this app, and the settings, sessions and device wiring behind it. The family apps — lights,
        thermostats, lists, notes, photos — are unaffected either way. Where the install doesn’t enforce it, every
        enrolled device can already do everything and this is just a note; turning enforcement on is a deploy.
      </Text>
      <Text tone="muted" className="mb-2">
        Tick it for yourself <em>and</em> set your device’s Person on the Sessions page before asking for enforcement.
        Backwards locks everyone out of this page — including the button that hands out invites.
      </Text>

      {error && <Text tone="danger">{error}</Text>}

      <div className="flex gap-1 mt-2">
        <Button variant="primary" disabled={saving || name.trim() === ''} onClick={save}>
          {saving ? 'Saving…' : 'Save'}
        </Button>
        <Button onClick={onClose}>
          Cancel
        </Button>
      </div>
    </Modal>
  );
}

const message = (err: unknown) => (err instanceof Error ? err.message : String(err));
