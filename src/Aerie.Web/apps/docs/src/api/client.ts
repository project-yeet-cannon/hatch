import type { DocSummary } from '../types';

async function fetchJson<T>(path: string): Promise<T> {
  const res = await fetch(path, { headers: { Accept: 'application/json' } });
  if (!res.ok) {
    throw new Error(`GET ${path} failed: ${res.status} ${res.statusText}`);
  }
  return res.json() as Promise<T>;
}

export const getDocs = () => fetchJson<DocSummary[]>('/api/docs');

export async function getDocContent(slug: string): Promise<string> {
  const res = await fetch(`/api/docs/${encodeURIComponent(slug)}`, { headers: { Accept: 'text/markdown' } });
  if (!res.ok) {
    throw new Error(`GET /api/docs/${slug} failed: ${res.status} ${res.statusText}`);
  }
  return res.text();
}
