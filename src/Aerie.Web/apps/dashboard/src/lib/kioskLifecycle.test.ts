import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { IDLE_TIMEOUT_MS, createKioskLifecycle, type KioskLifecycleDeps } from './kioskLifecycle';

// The reset and the reload are the two things that can take a half-typed
// grocery item off the wall, and `hold()` exists to stop them. "The timer did
// not fire" is not something eyeballing a tablet can establish, so the whole
// state machine is driven here on fake timers instead.

const LOADED = 'index-AAAAAAAA.js';
const DEPLOYED = 'index-BBBBBBBB.js';

const VERSION_POLL_INTERVAL_MS = 5 * 60_000;

function memoryStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    clear: () => map.clear(),
    getItem: (key: string) => map.get(key) ?? null,
    key: (index: number) => [...map.keys()][index] ?? null,
    removeItem: (key: string) => void map.delete(key),
    setItem: (key: string, value: string) => void map.set(key, value),
  };
}

interface HarnessOptions extends Partial<KioskLifecycleDeps> {
  /**
   * What the API reports. Set at construction rather than mocked afterwards
   * because the factory polls immediately - a version mocked in later would be
   * preceded by a "no drift" answer that clears the reload loop guard.
   */
  deployed?: string;
}

function harness({ deployed = LOADED, ...overrides }: HarnessOptions = {}) {
  const onReset = vi.fn();
  const reload = vi.fn();
  const fetchDeployedVersion = vi.fn<() => Promise<string | null>>().mockResolvedValue(deployed);
  const log = { info: vi.fn(), error: vi.fn() };

  const lifecycle = createKioskLifecycle({
    loadedVersion: LOADED,
    fetchDeployedVersion,
    onReset,
    reload,
    storage: memoryStorage(),
    log,
    ...overrides,
  });

  return { lifecycle, onReset, reload, fetchDeployedVersion, log };
}

/** A touch, clear of the 250ms activity throttle. */
function touch(lifecycle: { markActivity: () => void }) {
  vi.advanceTimersByTime(300);
  lifecycle.markActivity();
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date('2026-08-22T12:00:00Z'));
});

afterEach(() => {
  vi.useRealTimers();
});

describe('idle reset', () => {
  it('fires an idle timeout after the last touch', () => {
    const { lifecycle, onReset } = harness();

    touch(lifecycle);
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS - 1);
    expect(onReset).not.toHaveBeenCalled();

    vi.advanceTimersByTime(1);
    expect(onReset).toHaveBeenCalledTimes(1);

    lifecycle.dispose();
  });

  it('does not arm itself on a page nobody has touched', () => {
    const { lifecycle, onReset } = harness();

    vi.advanceTimersByTime(IDLE_TIMEOUT_MS * 4);
    expect(onReset).not.toHaveBeenCalled();

    lifecycle.dispose();
  });

  it('ignores the activity its own reset produces', () => {
    // The reset scrolls the page, and that scroll arrives as activity. Without
    // the suppression window the display would reset itself every 30s forever.
    const { lifecycle, onReset } = harness();

    touch(lifecycle);
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS);
    expect(onReset).toHaveBeenCalledTimes(1);

    lifecycle.markActivity(); // the scroll the reset just caused
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS * 2);
    expect(onReset).toHaveBeenCalledTimes(1);

    lifecycle.dispose();
  });
});

describe('hold', () => {
  it('cancels an armed reset and never fires while held', () => {
    const { lifecycle, onReset } = harness();

    touch(lifecycle);
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS / 2);
    lifecycle.hold();

    // Four idle timeouts' worth of someone slowly typing a shopping list.
    for (let i = 0; i < 4; i++) {
      vi.advanceTimersByTime(IDLE_TIMEOUT_MS);
      lifecycle.markActivity();
    }
    expect(onReset).not.toHaveBeenCalled();

    lifecycle.dispose();
  });

  it('re-arms the timer on release', () => {
    const { lifecycle, onReset } = harness();

    touch(lifecycle);
    lifecycle.hold();
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS * 3);
    lifecycle.release();

    // A full timeout from the release, not from whatever was left of the one
    // cancelled by the hold - closing an overlay is a touch, not idleness.
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS - 1);
    expect(onReset).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(onReset).toHaveBeenCalledTimes(1);

    lifecycle.dispose();
  });

  it('is idempotent in both directions', () => {
    const { lifecycle, onReset } = harness();

    lifecycle.release(); // never held; must not arm anything on its own
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS * 2);
    expect(onReset).not.toHaveBeenCalled();

    touch(lifecycle);
    lifecycle.hold();
    lifecycle.hold();
    lifecycle.release();
    lifecycle.release();
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS);
    expect(onReset).toHaveBeenCalledTimes(1);

    lifecycle.dispose();
  });
});

describe('deploy reload', () => {
  it('reloads immediately when the screen is untouched', async () => {
    const { lifecycle, reload } = harness({ deployed: DEPLOYED });

    await vi.advanceTimersByTimeAsync(0); // let the construction-time poll settle
    expect(reload).toHaveBeenCalledTimes(1);

    lifecycle.dispose();
  });

  it('waits for idle when someone is mid-interaction', async () => {
    const { lifecycle, reload } = harness({ deployed: DEPLOYED });

    touch(lifecycle);
    await vi.advanceTimersByTimeAsync(0);
    expect(reload).not.toHaveBeenCalled();

    vi.advanceTimersByTime(IDLE_TIMEOUT_MS);
    expect(reload).toHaveBeenCalledTimes(1);

    lifecycle.dispose();
  });

  it('defers a deploy noticed while held until after the release', async () => {
    const { lifecycle, reload, onReset } = harness({ deployed: DEPLOYED });

    lifecycle.hold();
    await vi.advanceTimersByTimeAsync(0);

    // The poll interval keeps running underneath the overlay; none of it may
    // take the page out from under someone typing.
    vi.advanceTimersByTime(VERSION_POLL_INTERVAL_MS * 2);
    await vi.advanceTimersByTimeAsync(0);
    expect(reload).not.toHaveBeenCalled();

    lifecycle.release();
    expect(reload).not.toHaveBeenCalled(); // still not: the release is a touch

    vi.advanceTimersByTime(IDLE_TIMEOUT_MS);
    expect(reload).toHaveBeenCalledTimes(1);
    // The reload takes the page; the reset must not also run behind it.
    expect(onReset).not.toHaveBeenCalled();

    lifecycle.dispose();
  });

  it('resets instead of reloading once a target has been given up on', async () => {
    // Same build already reloaded once and came back stale: reloading again
    // would spin, but abandoning it must not cost the display its idle reset.
    const storage = memoryStorage();
    storage.setItem('aerie-dashboard-reload-target', DEPLOYED);
    const { lifecycle, reload, onReset, log } = harness({ storage, deployed: DEPLOYED });

    touch(lifecycle);
    await vi.advanceTimersByTimeAsync(0);
    vi.advanceTimersByTime(IDLE_TIMEOUT_MS);

    expect(reload).not.toHaveBeenCalled();
    expect(onReset).toHaveBeenCalledTimes(1);
    expect(log.error).toHaveBeenCalled();

    lifecycle.dispose();
  });

  it('stays quiet when the build has no hashed assets', async () => {
    // `npm run dev` serves an unhashed /src/main.tsx; every poll would look
    // like drift.
    const { lifecycle, reload, fetchDeployedVersion } = harness({ loadedVersion: '' });

    await lifecycle.pollVersion();
    expect(fetchDeployedVersion).not.toHaveBeenCalled();
    expect(reload).not.toHaveBeenCalled();

    lifecycle.dispose();
  });

  it('swallows a failed version fetch', async () => {
    const { lifecycle, reload, log, fetchDeployedVersion } = harness();
    fetchDeployedVersion.mockRejectedValue(new Error('offline'));

    await lifecycle.pollVersion();
    expect(reload).not.toHaveBeenCalled();
    expect(log.error).not.toHaveBeenCalled(); // an outage must not write a line every 5 minutes

    lifecycle.dispose();
  });
});

describe('dispose', () => {
  it('stops the armed reset and the version poll', async () => {
    const { lifecycle, onReset, fetchDeployedVersion } = harness();

    touch(lifecycle);
    lifecycle.dispose();
    const pollsAtDispose = fetchDeployedVersion.mock.calls.length;

    vi.advanceTimersByTime(IDLE_TIMEOUT_MS + VERSION_POLL_INTERVAL_MS * 2);
    await vi.advanceTimersByTimeAsync(0);

    expect(onReset).not.toHaveBeenCalled();
    expect(fetchDeployedVersion).toHaveBeenCalledTimes(pollsAtDispose);
  });
});
