import { asJson, createClient } from '../../lib/http';
import type {
  Crate,
  CrateDetail,
  CrateWriteRequest,
  Item,
  ItemIndexRow,
  ItemWriteRequest,
  Location,
  LocationWriteRequest,
} from './types';

/**
 * Storage Helper's routes. Everything underneath the routes - the abort signal,
 * the sign-in redirect, the server's own error message - is the shell's
 * createClient (src/lib/http.ts), which this file used to own before Gather
 * became the second module needing it.
 */

const { fetchJson } = createClient('/api/storage');

// ---- Locations ----

export const getLocations = (signal?: AbortSignal) => fetchJson<Location[]>('/locations', { signal });

export const createLocation = (request: LocationWriteRequest) =>
  fetchJson<Location>('/locations', { method: 'POST', ...asJson(request) });

export const updateLocation = (id: string, request: LocationWriteRequest) =>
  fetchJson<Location>(`/locations/${id}`, { method: 'PUT', ...asJson(request) });

export const deleteLocation = (id: string) => fetchJson<void>(`/locations/${id}`, { method: 'DELETE' });

// ---- Crates ----

export const getCrates = (signal?: AbortSignal) => fetchJson<Crate[]>('/crates', { signal });

export const getCrate = (id: string, signal?: AbortSignal) => fetchJson<CrateDetail>(`/crates/${id}`, { signal });

/**
 * The scan path. `code` goes through untouched - the server normalises dashes,
 * case, and the I/L/O a person reads off a worn label.
 */
export const getCrateByCode = (code: string, signal?: AbortSignal) =>
  fetchJson<CrateDetail>(`/crates/by-code/${encodeURIComponent(code)}`, { signal });

export const createCrate = (request: CrateWriteRequest) =>
  fetchJson<CrateDetail>('/crates', { method: 'POST', ...asJson(request) });

/**
 * Mints `count` blank crates for a label sheet. Capped server-side at
 * MAX_BATCH_COUNT - kept in step here so the count box can't offer a number the
 * API will refuse.
 */
export const createCrateBatch = (count: number) =>
  fetchJson<Crate[]>('/crates/batch', { method: 'POST', ...asJson({ count }) });

/** StorageService.MaxBatchCount. */
export const MAX_BATCH_COUNT = 200;

export const updateCrate = (id: string, request: CrateWriteRequest) =>
  fetchJson<Crate>(`/crates/${id}`, { method: 'PUT', ...asJson(request) });

export const deleteCrate = (id: string) => fetchJson<void>(`/crates/${id}`, { method: 'DELETE' });

// ---- Items ----

export const getItems = (signal?: AbortSignal) => fetchJson<ItemIndexRow[]>('/items', { signal });

/**
 * Postgres full-text search across item, crate and location text - the same rows
 * as getItems, chosen by the server, so a match screen and the whole list are the
 * same screen.
 *
 * `no-store` keeps the service worker out of it: a search runs once per settled
 * keystroke, and caching every prefix somebody ever typed would fill the data
 * cache with answers to questions nobody will ask twice. Offline searching falls
 * back to filtering the cached index instead, which is fresher than a stale
 * result would be anyway.
 */
export const searchItems = (q: string, signal?: AbortSignal) =>
  fetchJson<ItemIndexRow[]>(`/search?q=${encodeURIComponent(q)}`, { signal, cache: 'no-store' });

export const createItem = (request: ItemWriteRequest) =>
  fetchJson<Item>('/items', { method: 'POST', ...asJson(request) });

export const updateItem = (id: string, request: ItemWriteRequest) =>
  fetchJson<Item>(`/items/${id}`, { method: 'PUT', ...asJson(request) });

export const deleteItem = (id: string) => fetchJson<void>(`/items/${id}`, { method: 'DELETE' });
