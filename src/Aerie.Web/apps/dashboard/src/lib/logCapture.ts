/**
 * The parts of clientLogger's console and network capture that are worth
 * asserting rather than eyeballing, pulled out of the module that installs
 * them. clientLogger itself cannot be imported under test - it touches window,
 * location, navigator, crypto and localStorage at module scope, and the app
 * carries no jsdom - so anything left inside it is untestable by construction.
 *
 * Everything here is pure: no globals, no clock of its own.
 */

/**
 * console.error takes anything at all. This turns a call's arguments into one
 * line short enough for a wall tablet, without letting a circular structure or
 * a DOM node throw on the way - the capture path must never be able to fail
 * louder than the thing it is reporting.
 */
export const CONSOLE_MESSAGE_LIMIT = 300;

export function describeConsoleArgs(args: readonly unknown[]): string {
  const text = args
    .map((arg) => {
      if (arg instanceof Error) return arg.message;
      if (typeof arg === 'string') return arg;
      try {
        return JSON.stringify(arg) ?? String(arg);
      } catch {
        // Circular, or a DOM node. The type is more use than a throw.
        return Object.prototype.toString.call(arg);
      }
    })
    .join(' ');
  return text.length > CONSOLE_MESSAGE_LIMIT ? `${text.slice(0, CONSOLE_MESSAGE_LIMIT)}…` : text;
}

/** The path a fetch was aimed at, or the raw input when it will not parse as a URL. */
export function requestPath(input: RequestInfo | URL, baseHref: string): string {
  const raw = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  try {
    return new URL(raw, baseHref).pathname;
  } catch {
    return raw;
  }
}

export interface FailureDedupe {
  /**
   * Whether this failure should be logged now, and how many were swallowed
   * since the last time it was. Null means "suppressed - one just went out".
   */
  note(key: string, now: number): { sinceLast: number } | null;
}

/**
 * A 60s poll failing for an hour is one problem, not sixty. Without this it
 * would be sixty entries, which in a 64-slot buffer means every other fault on
 * the page is pushed out by the loudest one - the opposite of what the modal is
 * for.
 *
 * The first occurrence of a key always goes out immediately: the point is to
 * collapse repeats, not to delay the news.
 */
export function createFailureDedupe(windowMs: number): FailureDedupe {
  const seen = new Map<string, { lastLoggedAt: number; suppressed: number }>();

  return {
    note(key, now) {
      const previous = seen.get(key);
      if (previous && now - previous.lastLoggedAt < windowMs) {
        previous.suppressed += 1;
        return null;
      }
      seen.set(key, { lastLoggedAt: now, suppressed: 0 });
      return { sinceLast: previous ? previous.suppressed : 0 };
    },
  };
}
