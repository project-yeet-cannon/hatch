import { collectDynamicMetadata, collectStaticMetadata } from './deviceMetadata';
import { createFailureDedupe, describeConsoleArgs, requestPath } from './logCapture';

export type LogLevel = 'info' | 'warn' | 'error';

/**
 * One entry as the health modal renders it. Deliberately not OutgoingEntry:
 * that carries the device metadata blob on every line for OpenSearch's benefit,
 * which is noise on a tablet screen, and it has no id to key a list by.
 */
export interface LoggedEntry {
  id: number;
  level: Exclude<LogLevel, 'info'>;
  message: string;
  /** ISO 8601. */
  timestamp: string;
  /** Only the fields worth showing on the wall - status and path, or source and line. */
  detail: Record<string, unknown>;
}

interface OutgoingEntry {
  app: string;
  level: LogLevel;
  message: string;
  timestamp: string;
  sessionId: string;
  deviceId: string;
  url: string;
  metadata: Record<string, unknown>;
}

declare global {
  interface Window {
    __uiLogSessionId?: string;
  }
}

const DEVICE_ID_STORAGE_KEY = 'aerie-device-id';

function getOrCreateDeviceId(): string {
  try {
    const existing = localStorage.getItem(DEVICE_ID_STORAGE_KEY);
    if (existing) return existing;
    const created = crypto.randomUUID();
    localStorage.setItem(DEVICE_ID_STORAGE_KEY, created);
    return created;
  } catch {
    // Storage disabled/unavailable (private browsing, quota) - fall back to a
    // per-load id rather than losing the field entirely.
    return crypto.randomUUID();
  }
}

const APP_NAME = 'dashboard';
const ENDPOINT = '/api/ui-logs';
const FLUSH_INTERVAL_MS = 4_000;
const MAX_QUEUE = 25;

// index.html pings the endpoint before any of this module's code runs; reuse
// its session id (if present) so every log line from this page load correlates.
const sessionId = window.__uiLogSessionId ?? crypto.randomUUID();
// Unlike sessionId, this survives across page loads/reboots (persisted in
// localStorage) so the same physical device's lines correlate over time -
// e.g. telling kiosk tablets apart in OpenSearch even when they share an IP.
const deviceId = getOrCreateDeviceId();
const staticMetadata = collectStaticMetadata();

let queue: OutgoingEntry[] = [];
let flushTimer: ReturnType<typeof setTimeout> | null = null;

function buildEntry(level: LogLevel, message: string, extra?: Record<string, unknown>): OutgoingEntry {
  return {
    app: APP_NAME,
    level,
    message,
    timestamp: new Date().toISOString(),
    sessionId,
    deviceId,
    url: location.href,
    metadata: { ...staticMetadata, ...collectDynamicMetadata(), ...extra },
  };
}

function send(entries: OutgoingEntry[], useBeacon: boolean): void {
  if (entries.length === 0) return;
  const body = JSON.stringify(entries);

  if (useBeacon && navigator.sendBeacon?.(ENDPOINT, new Blob([body], { type: 'application/json' }))) {
    return;
  }

  fetch(ENDPOINT, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body, keepalive: true }).catch(() => {
    // Best-effort - there's nowhere left to report a logging failure to.
  });
}

function flush(useBeacon = false): void {
  if (flushTimer !== null) {
    clearTimeout(flushTimer);
    flushTimer = null;
  }
  const entries = queue;
  queue = [];
  send(entries, useBeacon);
}

function enqueue(entry: OutgoingEntry): void {
  queue.push(entry);
  if (queue.length >= MAX_QUEUE) {
    flush();
    return;
  }
  flushTimer ??= setTimeout(() => flush(), FLUSH_INTERVAL_MS);
}

/**
 * Every failure path in this app already calls clientLogger.error - seventeen
 * call sites across the hooks, the components and the ErrorBoundary, plus the
 * window listeners below. A bounded buffer over that seam is therefore a
 * complete error feed for the price of one module change, with no new
 * instrumentation to keep in step as components come and go.
 *
 * Bounded at 64 because it feeds a modal someone scrolls on a wall tablet, not
 * an archive: OpenSearch already has all of it, and the tablet only needs
 * enough to answer "what is wrong right now".
 */
const BUFFER_LIMIT = 64;

let buffer: LoggedEntry[] = [];
let nextEntryId = 1;
const subscribers = new Set<(entries: readonly LoggedEntry[]) => void>();

function record(level: Exclude<LogLevel, 'info'>, message: string, detail?: Record<string, unknown>): void {
  buffer = [...buffer, { id: nextEntryId++, level, message, timestamp: new Date().toISOString(), detail: detail ?? {} }];
  if (buffer.length > BUFFER_LIMIT) buffer = buffer.slice(buffer.length - BUFFER_LIMIT);
  for (const notify of subscribers) notify(buffer);
}

/** The buffer as it stands. A new array on every change, so React can compare by reference. */
export function loggedEntries(): readonly LoggedEntry[] {
  return buffer;
}

/** Calls back on every warn/error, and returns the unsubscribe. */
export function subscribeToLog(fn: (entries: readonly LoggedEntry[]) => void): () => void {
  subscribers.add(fn);
  return () => {
    subscribers.delete(fn);
  };
}

export const clientLogger = {
  info(message: string, extra?: Record<string, unknown>) {
    enqueue(buildEntry('info', message, extra));
  },
  warn(message: string, extra?: Record<string, unknown>) {
    record('warn', message, extra);
    enqueue(buildEntry('warn', message, extra));
  },
  error(message: string, extra?: Record<string, unknown>) {
    record('error', message, extra);
    // Errors flush immediately rather than waiting for the batch timer -
    // if the page is about to die, this is the log line that matters most.
    enqueue(buildEntry('error', message, extra));
    flush();
  },
};

window.addEventListener('pagehide', () => flush(true));
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') flush(true);
});

// capture: true so this also sees resource load failures (script/img/link),
// which only fire on the capturing phase and never bubble to window.
window.addEventListener(
  'error',
  (event) => {
    if (event.target instanceof Element) {
      const el = event.target as HTMLScriptElement | HTMLLinkElement | HTMLImageElement;
      clientLogger.error(`Resource failed to load: <${el.tagName.toLowerCase()}>`, {
        src: 'src' in el ? el.src : undefined,
        href: 'href' in el ? el.href : undefined,
      });
      return;
    }

    clientLogger.error(event.message || 'Uncaught error', {
      source: event.filename,
      line: event.lineno,
      column: event.colno,
      stack: event.error instanceof Error ? event.error.stack : undefined,
    });
  },
  true,
);

window.addEventListener('unhandledrejection', (event: PromiseRejectionEvent) => {
  const reason = event.reason as unknown;
  clientLogger.error('Unhandled promise rejection', {
    reason: reason instanceof Error ? reason.message : String(reason),
    stack: reason instanceof Error ? reason.stack : undefined,
  });
});

/**
 * Console errors. Anything that reaches the console on a wall tablet reaches
 * nobody, since there is no console to look at - this puts those lines in front
 * of the one person who can act on them, whoever walks past next.
 *
 * The re-entry guard matters more than it looks: clientLogger.error can throw
 * (a circular structure in `extra`, storage gone), and without the flag that
 * throw would land back in console.error and recurse until the stack goes.
 */
const originalConsoleError = console.error.bind(console);
let insideConsoleCapture = false;

console.error = (...args: unknown[]) => {
  originalConsoleError(...args);
  if (insideConsoleCapture) return;
  insideConsoleCapture = true;
  try {
    record('error', describeConsoleArgs(args), { source: 'console.error' });
  } catch {
    // Nowhere left to report a reporting failure to.
  } finally {
    insideConsoleCapture = false;
  }
};

/**
 * Network failures, from one place rather than seventeen. Wrapping fetch is
 * what makes the health modal able to say "the dashboard API returned 502"
 * rather than only "something failed", and it covers callers that handle their
 * own errors quietly as well as the ones that log.
 *
 * Two things keep it from becoming its own outage. The endpoint this module
 * posts to is excluded, or a logging failure would log a logging failure
 * forever. And repeats are collapsed: a 60s poll failing for an hour is one
 * entry per minute-window carrying an occurrence count, not sixty entries that
 * push everything else out of a 64-slot buffer.
 */
/**
 * Five minutes, and it has to stay comfortably longer than App.tsx's
 * REFRESH_INTERVAL_MS - the two are only correct relative to each other, the
 * same way lib/kioskIdleTimings.ts's two rungs are.
 *
 * At 60s each they were exactly equal, and a failing poll landed on the window
 * boundary every single time: the dedupe passed its own unit test and collapsed
 * nothing at all in the one case it was written for. An hour of a broken
 * dashboard API would still have been sixty entries, which in a 64-slot buffer
 * pushes out every other fault on the page.
 */
const NETWORK_DEDUPE_MS = 5 * 60_000;
const requestDedupe = createFailureDedupe(NETWORK_DEDUPE_MS);

function noteRequestFailure(key: string, message: string, detail: Record<string, unknown>): void {
  const verdict = requestDedupe.note(key, Date.now());
  if (verdict === null) return;
  record('error', message, verdict.sinceLast > 0 ? { ...detail, sinceLast: verdict.sinceLast } : detail);
}

const originalFetch = window.fetch.bind(window);

window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  const path = requestPath(input, location.href);
  if (path === ENDPOINT) return await originalFetch(input, init);

  const method = init?.method ?? (typeof input === 'object' && 'method' in input ? input.method : 'GET');
  try {
    const response = await originalFetch(input, init);
    // 401 is excluded: it is not a fault, it is the sign-in contract (see
    // lib/signIn.ts), and the browser is already on its way somewhere else.
    if (!response.ok && response.status !== 401) {
      noteRequestFailure(`${method} ${path} ${response.status}`, `${response.status} from ${path}`, {
        method,
        path,
        status: response.status,
      });
    }
    return response;
  } catch (err: unknown) {
    const reason = err instanceof Error ? err.message : String(err);
    noteRequestFailure(`${method} ${path} threw`, `Request to ${path} failed`, { method, path, reason });
    throw err;
  }
};
