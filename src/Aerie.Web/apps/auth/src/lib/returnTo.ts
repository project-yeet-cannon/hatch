/**
 * Where to land after redemption, and the one thing this app must not get
 * wrong.
 *
 * This is the page everybody in the house is trained to trust and follow, which
 * makes an open redirect through it the classic own-goal: a link to
 * `/apps/auth/?r=https://evil.example/login` would send someone who just
 * authenticated to a page of somebody else's choosing, from a URL that starts
 * with the household's own host.
 *
 * `AuthChallenge.SafeReturnTo` (src/Aerie.Api/Services/Auth/AuthChallenge.cs)
 * already keeps `?r=` to a rooted relative path when the gate writes it. This
 * re-checks the same shape on arrival, because the query string is whatever the
 * address bar says by the time it gets here - the server's check covers the
 * redirect it issued, not the link someone was sent.
 */

/** Where redemption lands when nothing said otherwise: the app picker. */
export const DEFAULT_LANDING = '/apps/';

/** This app's own base path - a return target inside it is a loop, not a destination. */
const SELF_PREFIX = '/apps/auth';

/** The short alias Program.cs redirects into the shell. Same loop, one hop longer. */
const SELF_ALIAS = '/auth';

/**
 * The `?r=` value if it is a same-origin path, else null. Rejections, in order:
 *
 * - anything not starting with `/` - `https://evil.example`, and also
 *   `javascript:...`, which `location.replace` would happily execute;
 * - `//evil.example` (protocol-relative) and `/\evil.example` (the same thing
 *   to every browser that folds the backslash) - both start with a slash and
 *   both leave the origin;
 * - `/apps/auth...` itself, and the `/auth` alias that redirects into it, which
 *   would land back on sign-in and read as a redemption that silently did
 *   nothing.
 */
export function safeReturnTo(target: string | null | undefined): string | null {
  if (!target || target[0] !== '/') return null;
  if (target.length > 1 && (target[1] === '/' || target[1] === '\\')) return null;

  const path = target.split(/[?#]/)[0];
  if (path === SELF_ALIAS || path === `${SELF_ALIAS}/`) return null;
  if (path === SELF_PREFIX || path.startsWith(`${SELF_PREFIX}/`)) return null;

  return target;
}

/** The `?r=` carried by a URL's query string, validated. */
export function returnToFromSearch(search: string): string | null {
  return safeReturnTo(new URLSearchParams(search).get('r'));
}

/** Where to send the browser once a grant exists. Always a same-origin path. */
export function landingFor(returnTo: string | null): string {
  return returnTo ?? DEFAULT_LANDING;
}
