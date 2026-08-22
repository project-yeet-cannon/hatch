import { handledUnauthorized } from './signIn';
import { useEffect, useState } from 'react';

/**
 * Platform config from `GET /api/apps/config` - the deploy-time values a module
 * can't derive for itself. It lives in the shell rather than a module because
 * the values describe the install, not any one app.
 *
 * Today that's one field, and the shell needs a server round trip for it at all
 * for exactly one reason: a QR label is printed once and taped to a box for a
 * decade, so it must carry the install's canonical host rather than whichever
 * hostname or IP the laptop doing the printing happened to be using.
 */
export interface AppConfig {
  /** Absolute base URL of this install (no trailing slash), or null if unset - see AppsOptions. */
  publicBaseUrl: string | null;
}

let inflight: Promise<AppConfig> | null = null;

/** Fetched once per page load; a failure isn't cached, so a reconnect can still get the real value. */
export function loadAppConfig(): Promise<AppConfig> {
  inflight ??= fetch('/api/apps/config', { headers: { Accept: 'application/json' } })
    .then((res) => {
      // Never resolves - the document is being replaced by the sign-in shell,
      // and rejecting instead would clear `inflight` and refetch into the same
      // 401 while the navigation is still in flight.
      if (handledUnauthorized(res)) return new Promise<AppConfig>(() => {});
      return res.ok ? (res.json() as Promise<AppConfig>) : Promise.reject(new Error(String(res.status)));
    })
    .catch((err: unknown) => {
      inflight = null;
      throw err;
    });
  return inflight;
}

export interface AppConfigState {
  config: AppConfig | null;
  loading: boolean;
}

/**
 * `config` stays null when the fetch fails rather than surfacing an error:
 * every caller has a usable fallback (this browser's own origin), and an
 * unreachable API is not a reason to refuse to draw a screen.
 */
export function useAppConfig(): AppConfigState {
  const [state, setState] = useState<AppConfigState>({ config: null, loading: true });

  useEffect(() => {
    let live = true;
    const settle = (config: AppConfig | null) => {
      if (live) setState({ config, loading: false });
    };
    loadAppConfig().then(settle, () => settle(null));
    return () => {
      live = false;
    };
  }, []);

  return state;
}
