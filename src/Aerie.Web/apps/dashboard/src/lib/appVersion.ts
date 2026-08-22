import { handledUnauthorized } from './signIn';
/**
 * Build-drift detection for the kiosk dashboard.
 *
 * The tablet shell (apps/kiosk) loads this page once at boot and never
 * navigates again, so nothing here reloads on its own the way a browser tab
 * eventually does. Without a drift check, a wall display keeps rendering
 * whichever bundle was deployed the last time the tablet rebooted.
 *
 * The comparison token on both sides is the app's content-hashed asset
 * filenames, deduplicated, sorted and joined with '+'. The server derives it
 * from the index.html it is serving (Services/AppVersionService.cs); this
 * module derives it from the tags in the live document — i.e. from the files
 * this page was *actually* loaded from. That asymmetry is deliberate: a client
 * that instead learned its own version by asking a replica could latch onto
 * the wrong answer mid-rolling-deploy (loaded from the old image, told the new
 * version) and then never reload at all.
 *
 * versionFromAssetUrls is the half that has to stay byte-identical to
 * AppVersionService.ExtractAssetFileNames; it's kept free of DOM access so it
 * can be tested against the same fixtures the C# side uses.
 */

const APP_NAME = 'dashboard';
const ASSET_PREFIX = `/apps/${APP_NAME}/assets/`;

interface AppVersionResponse {
  version?: unknown;
}

/**
 * The filename of a hashed asset in this app's own assets/ directory, or null
 * for anything else — cross-app references and third-party URLs (the Google
 * Fonts stylesheet in index.html's head) aren't part of this build's identity.
 * Resolved through URL so an absolute `el.src` and a root-relative attribute
 * both reduce to the same thing; the nested-path rejection keeps this in step
 * with the server's regex, which can't cross a '/' either.
 */
function assetFileName(raw: string | null | undefined, baseUrl: string): string | null {
  if (!raw) return null;

  let pathname: string;
  try {
    pathname = new URL(raw, baseUrl).pathname;
  } catch {
    return null;
  }

  if (!pathname.startsWith(ASSET_PREFIX)) return null;
  const name = pathname.slice(ASSET_PREFIX.length);
  return name.length > 0 && !name.includes('/') ? name : null;
}

/**
 * The version token for a set of candidate asset URLs. Empty when none of them
 * are this app's hashed assets — which is what `npm run dev` looks like, since
 * it serves an unhashed /src/main.tsx — and callers read that as "drift
 * detection doesn't apply here" rather than as a mismatch.
 */
export function versionFromAssetUrls(urls: readonly (string | null | undefined)[], baseUrl: string): string {
  const names = new Set<string>();

  for (const url of urls) {
    const name = assetFileName(url, baseUrl);
    if (name) names.add(name);
  }

  // Default sort is by UTF-16 code unit, which for these ASCII filenames is
  // the same order as the server's StringComparer.Ordinal.
  return [...names].sort().join('+');
}

/** The version this document is running, read from the tags it was loaded from. */
export function readLoadedVersion(): string {
  const urls = [...document.querySelectorAll('script[src], link[href]')].map(
    (el) => el.getAttribute('src') ?? el.getAttribute('href'),
  );

  return versionFromAssetUrls(urls, document.baseURI);
}

/** The version this API replica is serving, or null if it couldn't be read. */
export async function fetchDeployedVersion(signal?: AbortSignal): Promise<string | null> {
  const response = await fetch(`/api/app-version/${APP_NAME}`, { cache: 'no-store', signal });
  // Checked before the !ok bail-out below, which cannot tell a refusal from a
  // pod mid-restart and answers "no drift" to both.
  if (handledUnauthorized(response)) return null;
  if (!response.ok) return null;

  const body = (await response.json()) as AppVersionResponse;
  return typeof body.version === 'string' && body.version.length > 0 ? body.version : null;
}
