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
  // Per segment, not encodeURIComponent(slug) - a slug is a path, and escaping
  // its separators to %2F is what the catch-all route can't match.
  const path = slug.split('/').map(encodeURIComponent).join('/');
  const res = await fetch(`/api/docs/${path}`, { headers: { Accept: 'text/markdown' } });
  if (!res.ok) {
    throw new Error(`GET /api/docs/${slug} failed: ${res.status} ${res.statusText}`);
  }
  return res.text();
}
