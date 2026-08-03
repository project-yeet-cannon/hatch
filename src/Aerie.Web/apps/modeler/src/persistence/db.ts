import { migrateProjectDocument } from '../model/schema';
import type { ProjectDocument } from '../model/schema';

// Browser-only (uses the global `indexedDB`) - not unit-tested, exercised by
// hand in the app. See useProjectStore for the load-on-open/autosave wiring.

const DB_NAME = 'aerie-modeler';
const DB_VERSION = 1;
const STORE_NAME = 'project';
// The app manages exactly one active project at a time (see TODO_MODELING.md
// - multi-project support isn't part of the spec), so it's stored under a
// single fixed key rather than keyed by project id.
const ACTIVE_PROJECT_KEY = 'active';

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      request.result.createObjectStore(STORE_NAME);
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error('Failed to open IndexedDB'));
  });
}

export async function loadActiveProject(): Promise<ProjectDocument | undefined> {
  const db = await openDb();
  try {
    return await new Promise((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readonly');
      const request = tx.objectStore(STORE_NAME).get(ACTIVE_PROJECT_KEY);
      request.onsuccess = () => resolve(request.result === undefined ? undefined : migrateProjectDocument(request.result));
      request.onerror = () => reject(request.error ?? new Error('Failed to read project from IndexedDB'));
    });
  } finally {
    db.close();
  }
}

export async function saveActiveProject(project: ProjectDocument): Promise<void> {
  const db = await openDb();
  try {
    await new Promise<void>((resolve, reject) => {
      const tx = db.transaction(STORE_NAME, 'readwrite');
      tx.objectStore(STORE_NAME).put(project, ACTIVE_PROJECT_KEY);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error ?? new Error('Failed to save project to IndexedDB'));
    });
  } finally {
    db.close();
  }
}
