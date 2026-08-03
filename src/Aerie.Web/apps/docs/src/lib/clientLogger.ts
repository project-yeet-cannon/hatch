import { collectDynamicMetadata, collectStaticMetadata } from './deviceMetadata';

export type LogLevel = 'info' | 'warn' | 'error';

interface OutgoingEntry {
  app: string;
  level: LogLevel;
  message: string;
  timestamp: string;
  sessionId: string;
  url: string;
  metadata: Record<string, unknown>;
}

declare global {
  interface Window {
    __uiLogSessionId?: string;
  }
}

const APP_NAME = 'docs';
const ENDPOINT = '/api/ui-logs';
const FLUSH_INTERVAL_MS = 4_000;
const MAX_QUEUE = 25;

// index.html pings the endpoint before any of this module's code runs; reuse
// its session id (if present) so every log line from this page load correlates.
const sessionId = window.__uiLogSessionId ?? crypto.randomUUID();
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

export const clientLogger = {
  info(message: string, extra?: Record<string, unknown>) {
    enqueue(buildEntry('info', message, extra));
  },
  warn(message: string, extra?: Record<string, unknown>) {
    enqueue(buildEntry('warn', message, extra));
  },
  error(message: string, extra?: Record<string, unknown>) {
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

const originalConsoleWarn = console.warn.bind(console);
console.warn = (...args: unknown[]) => {
  originalConsoleWarn(...args);
  clientLogger.warn(formatConsoleArgs(args));
};

const originalConsoleError = console.error.bind(console);
console.error = (...args: unknown[]) => {
  originalConsoleError(...args);
  clientLogger.error(formatConsoleArgs(args));
};

function formatConsoleArgs(args: unknown[]): string {
  return args.map((arg) => (arg instanceof Error ? arg.stack ?? arg.message : String(arg))).join(' ');
}
