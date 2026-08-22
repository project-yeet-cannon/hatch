import { handledUnauthorized } from '../../lib/signIn';
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
 * Storage Helper's client. Same shape as apps/admin's api/client.ts, with two
 * differences that matter for a phone in a garage:
 *
 * - Reads take an AbortSignal, so a screen that unmounts mid-request (tap a
 *   crate, tap back) doesn't land its response on a dead component.
 * - Failures carry the server's own message and status. `HttpError.status` is
 *   what lets the crate screen tell "no crate with that code" apart from "the
 *   API is unreachable", which are the same red box otherwise and want
 *   completely different words.
 */

const BASE = '/api/storage';

export class HttpError extends Error {
  // A field and an assignment rather than a constructor parameter property:
  // tsconfig sets erasableSyntaxOnly, so no TypeScript here may emit runtime code.
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'HttpError';
    this.status = status;
  }
}

/**
 * ASP.NET returns `BadRequest("name is required")` as a bare JSON string and
 * unhandled failures as a ProblemDetails object. Both are worth showing - these
 * messages ("quantity must be at least 1") are the actual explanation, and
 * "400 Bad Request" is not.
 */
function messageFrom(body: string, res: Response): string {
  const fallback = `${res.status} ${res.statusText}`.trim();
  if (!body) return fallback;

  try {
    const parsed: unknown = JSON.parse(body);
    if (typeof parsed === 'string') return parsed;
    if (parsed && typeof parsed === 'object') {
      const problem = parsed as { detail?: string; title?: string };
      return problem.detail ?? problem.title ?? fallback;
    }
  } catch {
    // Not JSON - plain text is already the message.
    return body;
  }
  return fallback;
}

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { Accept: 'application/json', ...(init?.body ? { 'Content-Type': 'application/json' } : {}) },
    ...init,
  });

  // A cold-scanned bin label lands here un-enrolled: sign in, then the bin.
  if (handledUnauthorized(res)) return await new Promise<T>(() => {});

  // 204 responses (every DELETE here) have no body, and res.json() throws on empty input.
  const text = await res.text();
  if (!res.ok) throw new HttpError(res.status, messageFrom(text, res));
  return (text ? JSON.parse(text) : undefined) as T;
}

const asJson = (body: unknown): RequestInit => ({ body: JSON.stringify(body) });

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
