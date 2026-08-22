import { handledUnauthorized } from './signIn';

/**
 * The shell's HTTP client, shared by every module under src/modules/.
 *
 * It started inside Storage Helper and moved up here when Gather became the
 * second module wanting it - the rule App.css and useResource.ts both state:
 * a thing moves up to the shell when a second module wants it, not in
 * anticipation of one.
 *
 * Two properties matter for a phone in a garage or an aisle:
 *
 * - Reads take an AbortSignal, so a screen that unmounts mid-request (tap a
 *   crate, tap back) doesn't land its response on a dead component.
 * - Failures carry the server's own message and status. `HttpError.status` is
 *   what lets a screen tell "no crate with that code" apart from "the API is
 *   unreachable", which are the same red box otherwise and want completely
 *   different words.
 */

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

export interface ApiClient {
  /** Sends a request under the client's base path, returning the parsed body. */
  fetchJson: <T>(path: string, init?: RequestInit) => Promise<T>;
}

/** Serialises a request body, and marks the request as carrying one. */
export const asJson = (body: unknown): RequestInit => ({ body: JSON.stringify(body) });

/**
 * A client rooted at one module's API prefix ('/api/storage', '/api/gather').
 * The base is closed over rather than passed per call, so a module's api.ts
 * writes its own routes and nothing else.
 */
export function createClient(base: string): ApiClient {
  async function fetchJson<T>(path: string, init?: RequestInit): Promise<T> {
    const res = await fetch(`${base}${path}`, {
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

  return { fetchJson };
}
