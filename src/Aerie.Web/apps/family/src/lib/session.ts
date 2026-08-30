import { useEffect, useState } from 'react';

/**
 * Who is holding this device, as far as the shell needs to know.
 *
 * The wall authenticates a *device* (docs/auth-architecture.md), so for four
 * modules the shell never had to ask. Quill's notes belong to a person, and a
 * module the household cannot see is a module the household cannot ask about -
 * so the shell asks once, and modules that need an owner are simply absent
 * without one (see modules/registry.ts).
 */
export interface Session {
  /** Whose device this is, or null for one nobody has claimed. */
  personId: string | null;
  /** Their name, for anything that wants to say it out loud. */
  personName: string | null;
}

/**
 * The last answer, kept so a cold launch in airplane mode still knows who it
 * is.
 *
 * This is load-bearing rather than an optimization. Quill exists to be readable
 * with no connectivity; if the shell forgot whose device this was every time it
 * could not reach the API, the app would vanish from the home screen at exactly
 * the moment it is wanted. The stored value is an id and a name - not a
 * credential, and not a note.
 */
const STORAGE_KEY = 'aerie-family-session';

function readCached(): Session | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return null;
    const { personId, personName } = parsed as Session;
    return typeof personId === 'string' ? { personId, personName: personName ?? null } : null;
  } catch {
    // Storage disabled, or a shape from an older bundle. Neither is worth
    // failing over: the fetch below is about to answer properly anyway.
    return null;
  }
}

function writeCached(session: Session | null): void {
  try {
    if (session?.personId) localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
    else localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing to do and nothing worth saying - the session still works for
    // this page load, it just won't survive an offline relaunch.
  }
}

/** The subset of AuthGrantDto the shell reads - see src/Aerie.Api/Models/Auth/Dtos.cs. */
interface MeResponse {
  personId?: string | null;
  personName?: string | null;
}

let inflight: Promise<Session | null> | null = null;

/**
 * Asks the API who this device is. Fetched once per page load; a failure isn't
 * cached, so a reconnect can still get the real answer.
 *
 * A 401 deliberately does **not** redirect to sign-in the way every other fetch
 * in the shell does (lib/signIn.ts). Two reasons, and the second is the one
 * that bites: `/api/auth/me` answers 401 whenever there is no cookie, including
 * when the wall is switched off entirely - `Auth:Enabled` is false in local
 * development and under AUTH_MODE=none - so redirecting here would send every
 * developer straight to a sign-in screen for an app that is not asking anyone
 * to sign in. And a 401 is a complete answer to the question being asked:
 * nobody. The gated endpoint a module calls next will do the redirecting, at a
 * point where it means something.
 */
export function loadSession(): Promise<Session | null> {
  inflight ??= fetch('/api/auth/me', { headers: { Accept: 'application/json' } })
    .then(async (res) => {
      if (!res.ok) {
        // Revoked, unenrolled, or unlinked since last time: forget, or the
        // shell shows an app whose every request is about to 404.
        if (res.status === 401 || res.status === 403) writeCached(null);
        return null;
      }
      const me = (await res.json()) as MeResponse;
      const session: Session = { personId: me.personId ?? null, personName: me.personName ?? null };
      writeCached(session.personId ? session : null);
      return session.personId ? session : null;
    })
    .catch((err: unknown) => {
      inflight = null;
      throw err;
    });
  return inflight;
}

export interface SessionState {
  /** Null means nobody: no grant, or a grant nobody has claimed. The two are one answer here, as they are in the API. */
  session: Session | null;
  /** True until the first answer. A cached session answers immediately, so this is false from the first frame on any device that has been online once. */
  loading: boolean;
}

/**
 * The session, starting from whatever the last launch learned.
 *
 * Starting from the cache rather than from null is what stops a module
 * appearing a beat after the home screen paints - and, offline, what stops it
 * never appearing at all. The live answer overwrites it when one arrives; a
 * failed fetch leaves it alone, because "I could not reach the API" is not
 * evidence that this device has stopped belonging to anyone.
 */
export function useSession(): SessionState {
  const [cached] = useState(readCached);
  const [state, setState] = useState<SessionState>({ session: cached, loading: cached === null });

  useEffect(() => {
    let live = true;
    loadSession().then(
      (session) => {
        if (live) setState({ session, loading: false });
      },
      () => {
        if (live) setState((prev) => ({ session: prev.session, loading: false }));
      },
    );
    return () => {
      live = false;
    };
  }, []);

  return state;
}
