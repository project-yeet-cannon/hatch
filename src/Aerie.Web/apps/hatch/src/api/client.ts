import { handledUnauthorized } from '../lib/signIn';
import type {
  Board,
  Comment,
  CommentCreateRequest,
  Issue,
  IssueCreateRequest,
  IssueEvent,
  IssueMoveRequest,
  IssuePatchRequest,
  Project,
  ProjectCreateRequest,
  ProjectPatchRequest,
  Status,
  StatusCreateRequest,
  StatusPatchRequest,
} from '../types';

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    headers: { Accept: 'application/json', ...(init?.body ? { 'Content-Type': 'application/json' } : {}) },
    ...init,
  });
  if (handledUnauthorized(res)) {
    // Navigating away; this promise is abandoned with the document.
    return await new Promise<T>(() => {});
  }
  if (!res.ok) {
    throw new Error(await failureMessage(res, init?.method ?? 'GET', path));
  }
  // 204s (every DELETE here) have no body, and res.json() throws on empty input.
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

/**
 * What a failed request says out loud.
 *
 * Hatch's refusals are written to be read: "AER still has 3 issues in it",
 * "a story hangs under an epic, not a task". Reporting "DELETE /api/hatch/…
 * failed: 409" instead throws away the only sentence that says what to do
 * about it.
 *
 * Falls back to the status line when the body is not a sentence - empty, HTML,
 * a ProblemDetails blob, or long enough to be a stack trace - because one of
 * those under a text input is worse than nothing.
 */
async function failureMessage(res: Response, method: string, path: string): Promise<string> {
  const fallback = `${method} ${path} failed: ${res.status} ${res.statusText}`;

  try {
    const body = (await res.text()).trim();
    if (body === '' || body.length > 300) return fallback;

    // A bare string body arrives JSON-quoted; anything structured is left to
    // the fallback rather than guessed at.
    const parsed: unknown = body.startsWith('"') ? JSON.parse(body) : body;
    return typeof parsed === 'string' && parsed !== '' && !parsed.startsWith('<') ? parsed : fallback;
  } catch {
    return fallback;
  }
}

const asJson = (body: unknown): RequestInit => ({ body: JSON.stringify(body) });

/** The display key in a URL. Keys are ASCII by construction, but a malformed
    one must not be able to reach outside its path segment. */
const seg = (key: string) => encodeURIComponent(key);

// ---- Board ----

export const getBoard = () => fetchJson<Board>('/api/hatch/board');

// ---- Projects ----

export const getProjects = () => fetchJson<Project[]>('/api/hatch/projects');
export const createProject = (request: ProjectCreateRequest) =>
  fetchJson<Project>('/api/hatch/projects', { method: 'POST', ...asJson(request) });
export const patchProject = (id: number, request: ProjectPatchRequest) =>
  fetchJson<Project>(`/api/hatch/projects/${id}`, { method: 'PATCH', ...asJson(request) });
export const deleteProject = (id: number) =>
  fetchJson<void>(`/api/hatch/projects/${id}`, { method: 'DELETE' });

// ---- Statuses ----

export const getStatuses = () => fetchJson<Status[]>('/api/hatch/statuses');
export const createStatus = (request: StatusCreateRequest) =>
  fetchJson<Status>('/api/hatch/statuses', { method: 'POST', ...asJson(request) });
export const patchStatus = (id: number, request: StatusPatchRequest) =>
  fetchJson<Status>(`/api/hatch/statuses/${id}`, { method: 'PATCH', ...asJson(request) });
export const deleteStatus = (id: number) =>
  fetchJson<void>(`/api/hatch/statuses/${id}`, { method: 'DELETE' });

// ---- Issues ----

export const getIssue = (key: string) => fetchJson<Issue>(`/api/hatch/issues/${seg(key)}`);
export const createIssue = (request: IssueCreateRequest) =>
  fetchJson<Issue>('/api/hatch/issues', { method: 'POST', ...asJson(request) });
export const patchIssue = (key: string, request: IssuePatchRequest) =>
  fetchJson<Issue>(`/api/hatch/issues/${seg(key)}`, { method: 'PATCH', ...asJson(request) });
export const deleteIssue = (key: string) =>
  fetchJson<void>(`/api/hatch/issues/${seg(key)}`, { method: 'DELETE' });
export const moveIssue = (key: string, request: IssueMoveRequest) =>
  fetchJson<Issue>(`/api/hatch/issues/${seg(key)}/move`, { method: 'POST', ...asJson(request) });

// ---- Comments and events ----

export const getComments = (key: string) => fetchJson<Comment[]>(`/api/hatch/issues/${seg(key)}/comments`);
export const addComment = (key: string, request: CommentCreateRequest) =>
  fetchJson<Comment>(`/api/hatch/issues/${seg(key)}/comments`, { method: 'POST', ...asJson(request) });
export const getEvents = (key: string) => fetchJson<IssueEvent[]>(`/api/hatch/issues/${seg(key)}/events`);
