import type { Note } from './types';

/*
  The local mirror: every note this person has, on this device, readable with no
  network at all.

  This is the module's reason for existing as much as the notes API is. A note
  written on Monday has to be readable on Thursday from a plane, or from a house
  whose internet is down, or from a phone that is on the internet but off the
  tailnet - three situations that look identical to a fetch and none of which
  should cost someone the thing they wrote.

  The shell's service worker already network-first caches /api GETs, which
  covers some of this for free. It is not enough on its own for two reasons: it
  can only serve URLs this device happened to request, and it cannot tell the UI
  that what it served was old. A mirror the module owns can do both, and the
  second is what makes offline honestly read-only instead of a save button that
  fails for reasons nobody can see.

  What it deliberately is not: a write buffer. Offline writes need conflict
  resolution, and per docs/family-apps-architecture.md nothing here justifies it
  yet. Offline is read-only, and says so.
*/

/**
 * The bit of a key-value store this module needs.
 *
 * An interface rather than IndexedDB calls inline, for two reasons that are
 * both about correctness rather than taste: the mirror's rules - whose notes
 * these are, when to throw them away - are the part that can be wrong, and they
 * are only testable against a store that is not a browser. The second is that
 * IndexedDB is genuinely absent sometimes (Safari private browsing has shipped
 * builds where opening a database throws), and a module whose storage is a
 * parameter degrades to in-memory instead of to a white screen.
 */
export interface KeyValueStore {
  get<T>(key: string): Promise<T | null>;
  set(key: string, value: unknown): Promise<void>;
  remove(key: string): Promise<void>;
}

/**
 * What is actually stored: the notes, and whose they are.
 *
 * The person id is the load-bearing field. A device can be re-linked to someone
 * else on the admin Sessions page, and a mirror that only held notes would show
 * the previous owner's notes to the new one the first time the API was
 * unreachable - the exact failure the module is built to prevent, arriving
 * through the feature meant to prevent it.
 */
export interface Snapshot {
  personId: string;
  notes: Note[];
  savedAt: string;
}

const KEY = 'notes';

export interface Mirror {
  /** This person's mirrored notes, or null when there is nothing usable for them. */
  read: (personId: string) => Promise<Note[] | null>;
  write: (personId: string, notes: Note[]) => Promise<void>;
  clear: () => Promise<void>;
}

export function createMirror(store: KeyValueStore): Mirror {
  return {
    async read(personId) {
      const snapshot = await store.get<Snapshot>(KEY);
      if (!snapshot || !Array.isArray(snapshot.notes)) return null;

      if (snapshot.personId !== personId) {
        // Not ours. Dropped rather than ignored: leaving one person's notes on
        // a device that now belongs to someone else is the thing this field
        // exists to stop, and doing it lazily leaves them there until the next
        // successful sync, which offline may never come.
        await store.remove(KEY);
        return null;
      }

      return snapshot.notes;
    },

    async write(personId, notes) {
      await store.set(KEY, { personId, notes, savedAt: new Date().toISOString() } satisfies Snapshot);
    },

    clear() {
      return store.remove(KEY);
    },
  };
}

/** A store that forgets on reload. The fallback when IndexedDB is unavailable, and what the tests run against. */
export function memoryStore(): KeyValueStore {
  const values = new Map<string, string>();
  return {
    get: <T,>(key: string) => Promise.resolve((values.has(key) ? (JSON.parse(values.get(key)!) as T) : null)),
    set: (key, value) => {
      values.set(key, JSON.stringify(value));
      return Promise.resolve();
    },
    remove: (key) => {
      values.delete(key);
      return Promise.resolve();
    },
  };
}

const DB_NAME = 'aerie-quill';
const DB_STORE = 'mirror';

/**
 * IndexedDB, and not localStorage.
 *
 * localStorage is synchronous on the main thread and capped around 5 MB across
 * the whole origin - which the shell is already using for the device id and the
 * session. Writing every note on every sync through it would jank the list on a
 * phone and would eventually start throwing quota errors that surface as a
 * mirror that silently stopped updating.
 */
export function indexedDbStore(): KeyValueStore {
  let opening: Promise<IDBDatabase> | null = null;

  function open(): Promise<IDBDatabase> {
    opening ??= new Promise<IDBDatabase>((resolve, reject) => {
      const request = indexedDB.open(DB_NAME, 1);
      request.onupgradeneeded = () => request.result.createObjectStore(DB_STORE);
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => reject(request.error ?? new Error('indexedDB.open failed'));
      // Another tab holding an old version open. Rejecting rather than hanging
      // forever: the caller falls back to memory and the app still works.
      request.onblocked = () => reject(new Error('indexedDB.open blocked'));
    }).catch((err: unknown) => {
      opening = null;
      throw err;
    });
    return opening;
  }

  function run<T>(mode: IDBTransactionMode, act: (store: IDBObjectStore) => IDBRequest): Promise<T> {
    return open().then(
      (db) =>
        new Promise<T>((resolve, reject) => {
          const request = act(db.transaction(DB_STORE, mode).objectStore(DB_STORE));
          request.onsuccess = () => resolve(request.result as T);
          request.onerror = () => reject(request.error ?? new Error('indexedDB request failed'));
        }),
    );
  }

  return {
    get: <T,>(key: string) => run<T | undefined>('readonly', (s) => s.get(key)).then((v) => v ?? null),
    set: (key, value) => run<unknown>('readwrite', (s) => s.put(value, key)).then(() => undefined),
    remove: (key) => run<unknown>('readwrite', (s) => s.delete(key)).then(() => undefined),
  };
}

/**
 * The mirror the app uses. One per page load, and never a reason to fail: a
 * browser with no usable IndexedDB gets an in-memory mirror, which is worth
 * nothing offline but keeps every online path identical rather than growing a
 * second code path for a rare browser.
 */
export const mirror: Mirror = createMirror(
  typeof indexedDB === 'undefined' ? memoryStore() : guarded(indexedDbStore(), memoryStore()),
);

/**
 * Falls back the first time the real store throws, and stays fallen back.
 *
 * IndexedDB's failures are not at `open()` - they are at the first transaction,
 * in private browsing modes that hand you a database and then refuse to write
 * to it. A mirror that reported those as read failures would look like a device
 * that had simply never synced.
 */
function guarded(primary: KeyValueStore, fallback: KeyValueStore): KeyValueStore {
  let broken = false;

  async function attempt<T>(act: (store: KeyValueStore) => Promise<T>): Promise<T> {
    if (broken) return act(fallback);
    try {
      return await act(primary);
    } catch {
      broken = true;
      return act(fallback);
    }
  }

  return {
    get: (key) => attempt((s) => s.get(key)),
    set: (key, value) => attempt((s) => s.set(key, value)),
    remove: (key) => attempt((s) => s.remove(key)),
  };
}
