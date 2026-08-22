import { asJson, createClient } from '../../lib/http';
import type {
  ClearCheckedResult,
  Item,
  ItemAddRequest,
  ItemWriteRequest,
  ListDetail,
  ListSummary,
  ListWriteRequest,
} from './types';

/**
 * Gather's routes. The client underneath - abort signals, the sign-in redirect,
 * the server's own error message - is the shell's createClient (src/lib/http.ts).
 *
 * Check and uncheck are their own calls rather than a field on the edit,
 * matching the API. The two writes that collide in practice are a phone
 * checking things off in an aisle and the kitchen wall renaming one a second
 * earlier; keeping them apart is what stops the common collision from
 * clobbering text.
 */

const { fetchJson } = createClient('/api/gather');

// ---- Lists ----

export const getLists = (signal?: AbortSignal) => fetchJson<ListSummary[]>('/lists', { signal });

export const getList = (id: string, signal?: AbortSignal) => fetchJson<ListDetail>(`/lists/${id}`, { signal });

export const createList = (request: ListWriteRequest) =>
  fetchJson<ListSummary>('/lists', { method: 'POST', ...asJson(request) });

export const updateList = (id: string, request: ListWriteRequest) =>
  fetchJson<ListSummary>(`/lists/${id}`, { method: 'PUT', ...asJson(request) });

/** Takes the list's items with it - the FK cascades. */
export const deleteList = (id: string) => fetchJson<void>(`/lists/${id}`, { method: 'DELETE' });

// ---- Items ----

/** An upsert on the normalized name: re-adding something already there brings it back, un-checked. */
export const addItem = (listId: string, request: ItemAddRequest) =>
  fetchJson<Item>(`/lists/${listId}/items`, { method: 'POST', ...asJson(request) });

/** Overwrites exactly what it is given, nulls included. A 409 means the new name is already on the list. */
export const updateItem = (listId: string, itemId: string, request: ItemWriteRequest) =>
  fetchJson<Item>(`/lists/${listId}/items/${itemId}`, { method: 'PUT', ...asJson(request) });

export const setItemChecked = (listId: string, itemId: string, isChecked: boolean) =>
  fetchJson<Item>(`/lists/${listId}/items/${itemId}/${isChecked ? 'check' : 'uncheck'}`, { method: 'POST' });

export const deleteItem = (listId: string, itemId: string) =>
  fetchJson<void>(`/lists/${listId}/items/${itemId}`, { method: 'DELETE' });

export const clearChecked = (listId: string) =>
  fetchJson<ClearCheckedResult>(`/lists/${listId}/clear-checked`, { method: 'POST' });
