import { handledUnauthorized } from '../lib/signIn';
import type { GatherItem, GatherListDetail, GatherListSummary, GatherSource } from '../types';

/**
 * Gather over HTTP — the wall's half of src/Aerie.Api/Modules/Gather.
 *
 * Unlike routinesClient this both reads and writes, so `handledUnauthorized`
 * matters on every call and not just as a courtesy: a 401 the caller treats as
 * "offline, the next poll covers it" is exactly the failure that left the
 * tablets showing hours-old data with nobody being asked to sign in (see
 * lib/signIn.ts). A refusal navigates and this promise never settles, so no
 * caller renders an error over a page that is already leaving.
 */

const BASE = '/api/gather';

/**
 * The API answers a validation failure with a plain-text reason naming the
 * field ("name must be at most 120 characters"), which is worth putting in
 * front of someone at the wall — a bare 400 is not.
 */
async function failureFrom(res: Response): Promise<Error> {
  let detail = '';
  try {
    detail = (await res.text()).trim().replace(/^"|"$/g, '');
  } catch {
    // Body already consumed or the connection dropped; the status still says
    // something useful.
  }
  // A ProblemDetails document is machine noise, not a sentence; fall back to
  // the status line rather than putting JSON on the kitchen wall.
  if (detail.startsWith('{') || detail.length === 0) detail = `${res.status} ${res.statusText}`;
  return new Error(detail);
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { headers: { Accept: 'application/json' }, ...init });
  if (handledUnauthorized(res)) return await new Promise<T>(() => {});
  if (!res.ok) throw await failureFrom(res);
  return (await res.json()) as T;
}

export class ApiGatherSource implements GatherSource {
  getLists(): Promise<GatherListSummary[]> {
    return request<GatherListSummary[]>('/lists');
  }

  getList(id: string): Promise<GatherListDetail> {
    return request<GatherListDetail>(`/lists/${id}`);
  }

  /**
   * An upsert, not an insert — the server merges onto whatever is already
   * there under the same normalized name and un-checks it. Quantity and note
   * are omitted deliberately: the wall has no field for either, and sending
   * nulls would erase what someone put on the item from their phone.
   */
  addItem(listId: string, name: string): Promise<GatherItem> {
    return request<GatherItem>(`/lists/${listId}/items`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ name }),
    });
  }

  setChecked(listId: string, itemId: string, isChecked: boolean): Promise<GatherItem> {
    const verb = isChecked ? 'check' : 'uncheck';
    return request<GatherItem>(`/lists/${listId}/items/${itemId}/${verb}`, { method: 'POST' });
  }

  async clearChecked(listId: string): Promise<number> {
    const result = await request<{ deleted: number }>(`/lists/${listId}/clear-checked`, { method: 'POST' });
    return result.deleted;
  }
}
