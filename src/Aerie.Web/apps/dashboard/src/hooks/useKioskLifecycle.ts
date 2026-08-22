import { useEffect, useRef, useState } from 'react';
import { fetchDeployedVersion, readLoadedVersion } from '../lib/appVersion';
import { clientLogger } from '../lib/clientLogger';
import { ACTIVITY_EVENTS, createKioskLifecycle, type KioskLifecycle } from '../lib/kioskLifecycle';

/**
 * React's half of the kiosk lifecycle: window listeners in, a reset token out.
 * The behaviour itself - the idle reset, the deploy reload, and the hold that
 * suspends both - lives in lib/kioskLifecycle.ts, where it is unit-tested.
 *
 * Returns a token that increments on each idle reset. Callers remount whatever
 * holds stray presentational state by using it as a `key` - deliberately not
 * the whole app, which would drop the loaded dashboard data and flash the
 * skeleton, and would also discard RoutinesSection's optimistic toggle state
 * (that reconciles against the server on its own 60s poll, so clearing it here
 * would visually revert a tap the user just made).
 *
 * `hold` suspends the reset and the reload together while something on screen
 * cannot survive either - GatherOverlay, which has a text field in it. One flag
 * covers both deliberately: a reload that fires mid-entry is the same bug as a
 * reset that does.
 */
export function useKioskLifecycle(hold: boolean): number {
  const [resetToken, setResetToken] = useState(0);
  const lifecycleRef = useRef<KioskLifecycle | null>(null);

  useEffect(() => {
    let storage: Storage | null = null;
    try {
      storage = window.sessionStorage;
    } catch {
      // Storage blocked; the lifecycle runs without its reload loop guard.
    }

    const lifecycle = createKioskLifecycle({
      loadedVersion: readLoadedVersion(),
      fetchDeployedVersion: () => fetchDeployedVersion(),
      onReset: () => {
        window.scrollTo(0, 0);
        setResetToken((token) => token + 1);
      },
      reload: () => location.reload(),
      storage,
      log: clientLogger,
    });
    lifecycleRef.current = lifecycle;

    const onActivity = () => lifecycle.markActivity();
    for (const event of ACTIVITY_EVENTS) {
      window.addEventListener(event, onActivity, { passive: true, capture: true });
    }

    return () => {
      for (const event of ACTIVITY_EVENTS) {
        window.removeEventListener(event, onActivity, { capture: true });
      }
      lifecycle.dispose();
      lifecycleRef.current = null;
    };
  }, []);

  // Separate from the effect above so opening an overlay doesn't tear down the
  // listeners and restart the version poll - only the hold flag changes here.
  useEffect(() => {
    const lifecycle = lifecycleRef.current;
    if (!lifecycle) return;
    if (hold) lifecycle.hold();
    else lifecycle.release();
  }, [hold]);

  return resetToken;
}
