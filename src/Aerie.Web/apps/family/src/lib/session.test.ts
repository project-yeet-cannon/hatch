import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/*
  The shell's answer to "whose device is this", and specifically the two rules
  that are only visible when something goes wrong:

  - A failed fetch must not forget who this is. The cached answer is what keeps
    Quill on the home screen on a plane, which is the entire point of Quill.
  - A refusal must forget. A device that was unlinked or revoked has to stop
    showing an app whose every request is about to 404.
*/

const KEY = 'aerie-family-session';

function fakeStorage() {
  const values = new Map<string, string>();
  return {
    getItem: (k: string) => values.get(k) ?? null,
    setItem: (k: string, v: string) => void values.set(k, v),
    removeItem: (k: string) => void values.delete(k),
    clear: () => values.clear(),
    key: () => null,
    length: 0,
  } as unknown as Storage;
}

/** A fresh module each time: loadSession memoizes its request for the page load. */
async function load() {
  vi.resetModules();
  return await import('./session');
}

beforeEach(() => {
  vi.stubGlobal('localStorage', fakeStorage());
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function respond(status: number, body?: unknown) {
  vi.stubGlobal(
    'fetch',
    vi.fn(() =>
      Promise.resolve({
        ok: status >= 200 && status < 300,
        status,
        json: () => Promise.resolve(body),
      } as Response),
    ),
  );
}

describe('loadSession', () => {
  it('reports the person on the grant, and remembers them', async () => {
    respond(200, { personId: 'ada', personName: 'Ada' });
    const { loadSession } = await load();

    expect(await loadSession()).toEqual({ personId: 'ada', personName: 'Ada' });
    expect(JSON.parse(localStorage.getItem(KEY)!)).toMatchObject({ personId: 'ada' });
  });

  it('reports nobody for a device nobody has claimed, and remembers nothing', async () => {
    respond(200, { personId: null, personName: null });
    const { loadSession } = await load();

    expect(await loadSession()).toBeNull();
    expect(localStorage.getItem(KEY)).toBeNull();
  });

  /**
   * `/api/auth/me` answers 401 whenever there is no cookie, including when the
   * wall is switched off entirely (local dev, AUTH_MODE=none). Redirecting here
   * the way every other fetch in the shell does would send a developer to a
   * sign-in screen for an app that is not asking anyone to sign in.
   */
  it('treats a refusal as an answer rather than as a reason to navigate', async () => {
    respond(401);
    const replace = vi.fn();
    vi.stubGlobal('location', { pathname: '/apps/family/', search: '', replace });
    const { loadSession } = await load();

    expect(await loadSession()).toBeNull();
    expect(replace).not.toHaveBeenCalled();
  });

  it('forgets a person when the device stops being theirs', async () => {
    localStorage.setItem(KEY, JSON.stringify({ personId: 'ada', personName: 'Ada' }));
    respond(401);
    const { loadSession } = await load();

    await loadSession();

    expect(localStorage.getItem(KEY)).toBeNull();
  });

  it('does not forget a person just because Aerie is unreachable', async () => {
    localStorage.setItem(KEY, JSON.stringify({ personId: 'ada', personName: 'Ada' }));
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))));
    const { loadSession } = await load();

    await expect(loadSession()).rejects.toThrow();

    // The cached answer survives, which is what keeps an owner-requiring module
    // on the home screen in airplane mode.
    expect(JSON.parse(localStorage.getItem(KEY)!)).toMatchObject({ personId: 'ada' });
  });

  it('asks once per page load however many callers ask', async () => {
    respond(200, { personId: 'ada', personName: 'Ada' });
    const { loadSession } = await load();

    await Promise.all([loadSession(), loadSession(), loadSession()]);

    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(1);
  });

  it('does not cache a failure, so a reconnect can still get the real answer', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))));
    const { loadSession } = await load();

    await expect(loadSession()).rejects.toThrow();
    await expect(loadSession()).rejects.toThrow();

    expect(vi.mocked(fetch)).toHaveBeenCalledTimes(2);
  });

  it('ignores a cached shape it does not recognise', async () => {
    localStorage.setItem(KEY, 'not json at all');
    respond(200, { personId: 'ada', personName: 'Ada' });
    const { loadSession } = await load();

    expect(await loadSession()).toEqual({ personId: 'ada', personName: 'Ada' });
  });
});
