import { handledUnauthorized } from '../lib/signIn';
import type {
  Board,
  Comment,
  CommentCreateRequest,
  ImportRequest,
  ImportResult,
  Issue,
  IssueBulkEditRequest,
  IssueBulkResult,
  IssueCard,
  IssueCreateRequest,
  IssueEvent,
  IssueMoveRequest,
  IssuePatchRequest,
  IssueRollup,
  IssueSearch,
  ParsedEpic,
  PastedPlan,
  Plan,
  Playbook,
  PlaybookCreateRequest,
  PlaybookPatchRequest,
  Project,
  ProjectCreateRequest,
  ProjectPatchRequest,
  Status,
  StatusCreateRequest,
  StatusPatchRequest,
} from '../types';

async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    // Only a string body is ours to label. FormData sets its own content type
    // with the multipart boundary in it, and stating one here would produce a
    // header the server cannot parse the body against.
    headers: {
      Accept: 'application/json',
      ...(typeof init?.body === 'string' ? { 'Content-Type': 'application/json' } : {}),
    },
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

// ---- Playbooks ----
//
// Reading is all a Hatch-scoped API key may do here; the three writes are
// refused for one, which is why they exist only on this page and never in a
// script. See PlaybooksController.

export const getPlaybooks = () => fetchJson<Playbook[]>('/api/hatch/playbooks');
export const createPlaybook = (request: PlaybookCreateRequest) =>
  fetchJson<Playbook>('/api/hatch/playbooks', { method: 'POST', body: JSON.stringify(request) });
export const patchPlaybook = (id: number, request: PlaybookPatchRequest) =>
  fetchJson<Playbook>(`/api/hatch/playbooks/${id}`, { method: 'PATCH', body: JSON.stringify(request) });
export const deletePlaybook = (id: number) =>
  fetchJson<void>(`/api/hatch/playbooks/${id}`, { method: 'DELETE' });

export const getIssue = (key: string) => fetchJson<Issue>(`/api/hatch/issues/${seg(key)}`);

/** The issues a filter finds, as cards. An absent field is left off the query
    string entirely - an empty `parentKey` means "no parent" to the server, so
    sending one for a field nobody filled in would silently ask a different
    question. */
export const searchIssues = (filter: IssueSearch) => {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(filter)) {
    if (value !== null && value !== undefined) params.set(name, String(value));
  }
  const query = params.toString();
  return fetchJson<IssueCard[]>(`/api/hatch/issues${query ? `?${query}` : ''}`);
};

export const bulkEditIssues = (request: IssueBulkEditRequest) =>
  fetchJson<IssueBulkResult>('/api/hatch/issues/bulk', { method: 'POST', ...asJson(request) });
export const createIssue = (request: IssueCreateRequest) =>
  fetchJson<Issue>('/api/hatch/issues', { method: 'POST', ...asJson(request) });
export const patchIssue = (key: string, request: IssuePatchRequest) =>
  fetchJson<Issue>(`/api/hatch/issues/${seg(key)}`, { method: 'PATCH', ...asJson(request) });
export const deleteIssue = (key: string) =>
  fetchJson<void>(`/api/hatch/issues/${seg(key)}`, { method: 'DELETE' });
export const moveIssue = (key: string, request: IssueMoveRequest) =>
  fetchJson<Issue>(`/api/hatch/issues/${seg(key)}/move`, { method: 'POST', ...asJson(request) });

// ---- Progress ----

/** What one subtree adds up to, and what each of its direct children adds up
    to. Server-side arithmetic on purpose: a meter re-derived here would
    disagree with the CLI the first time a column was added. See Rollup.cs. */
export const getIssuePlan = (key: string) => fetchJson<IssueRollup>(`/api/hatch/plan/${seg(key)}`);

/** Every epic in the tracker and what it adds up to, plus what hangs under no
    epic at all. Its own read rather than a corner of the board: the board is
    refetched after every drag, and a rollup bolted onto it would be recomputed
    on every drop for a screen nobody is looking at. */
export const getPlan = (projectId?: number) =>
  fetchJson<Plan>(`/api/hatch/plan${projectId === undefined ? '' : `?projectId=${projectId}`}`);

// ---- Comments and events ----

export const getComments = (key: string) => fetchJson<Comment[]>(`/api/hatch/issues/${seg(key)}/comments`);
export const addComment = (key: string, request: CommentCreateRequest) =>
  fetchJson<Comment>(`/api/hatch/issues/${seg(key)}/comments`, { method: 'POST', ...asJson(request) });
export const getEvents = (key: string) => fetchJson<IssueEvent[]>(`/api/hatch/issues/${seg(key)}/events`);

// ---- The importer ----

/** What these files would become, without writing anything. Multipart because
    it is several files at once; the browser sets the boundary, so this is the
    one request here that does not name its own content type. */
export const previewImport = (files: File[]) => {
  const form = new FormData();
  for (const file of files) form.append('files', file, file.name);
  return fetchJson<ParsedEpic[]>('/api/hatch/import/preview', { method: 'POST', body: form });
};

/** The same look for a plan that was pasted rather than uploaded. Plain JSON:
    there is no file, so there is no multipart envelope to build. Comes back as
    one epic, which the page wraps in the array the upload path returns so that
    both roads meet at the same preview and the same import. */
export const previewText = (request: PastedPlan) =>
  fetchJson<ParsedEpic>('/api/hatch/import/preview-text', { method: 'POST', ...asJson(request) });

export const runImport = (request: ImportRequest) =>
  fetchJson<ImportResult>('/api/hatch/import', { method: 'POST', ...asJson(request) });
