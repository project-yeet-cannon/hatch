import { useEffect, useState } from 'react';
import { fetchDeployedVersion, readLoadedVersion } from '../lib/appVersion';
import { clientLogger } from '../lib/clientLogger';

/**
 * The two things a permanently-open wall display needs that an ordinary browser
 * tab gets for free, both hanging off one idle timer:
 *
 * - **Reset after use.** People scroll or expand a card to look at something and
 *   then walk away, leaving the dashboard parked wherever they left it. A short
 *   while after the last touch, put it back to its base state.
 * - **Pick up new deploys.** apps/kiosk loads this page once at tablet boot and
 *   never navigates again, so a deploy is invisible to it. Poll for build drift
 *   (see lib/appVersion.ts) and reload when one appears.
 *
 * The reload waits for idle rather than firing the moment drift is noticed, so
 * the page never blanks out from under someone mid-interaction.
 */

const IDLE_TIMEOUT_MS = 30_000;
const VERSION_POLL_INTERVAL_MS = 5 * 60_000;

// Scroll fires per frame; there's no reason to re-arm a 30s timer at 60Hz.
const ACTIVITY_THROTTLE_MS = 250;

// The reset scrolls the page, and that scroll fires the same events this hook
// treats as user activity - without a suppression window the reset re-arms its
// own idle timer and the display resets itself every 30s forever.
const SELF_INFLICTED_ACTIVITY_MS = 1_000;

// Survives location.reload() within the session, which is exactly the span a
// reload loop would live in.
const RELOAD_GUARD_KEY = 'aerie-dashboard-reload-target';

const ACTIVITY_EVENTS = ['pointerdown', 'pointermove', 'touchstart', 'touchmove', 'wheel', 'scroll', 'keydown'] as const;

/**
 * Returns a token that increments on each idle reset. Callers remount whatever
 * holds stray presentational state by using it as a `key` - deliberately not
 * the whole app, which would drop the loaded dashboard data and flash the
 * skeleton, and would also discard RoutinesSection's optimistic toggle state
 * (that reconciles against the server on its own 60s poll, so clearing it here
 * would visually revert a tap the user just made).
 */
export function useKioskLifecycle(): number {
  const [resetToken, setResetToken] = useState(0);

  useEffect(() => {
    const loadedVersion = readLoadedVersion();

    let lastActivityAt = 0; // 0 = untouched since load
    let ignoreActivityUntil = 0;
    let idleTimer: ReturnType<typeof setTimeout> | null = null;
    let pendingTarget: string | null = null;
    let abandonedTarget: string | null = null;
    let disposed = false;

    if (loadedVersion === '') {
      // `npm run dev` serves an unhashed /src/main.tsx, so there is nothing to
      // compare against and every poll would look like drift.
      clientLogger.info('Dashboard build has no hashed assets; version drift checks disabled');
    } else {
      clientLogger.info('Dashboard lifecycle started', { loadedVersion });
    }

    const isIdle = () => lastActivityAt === 0 || Date.now() - lastActivityAt >= IDLE_TIMEOUT_MS;

    /** True if a reload is under way; false if it declined to attempt one. */
    const reload = (target: string): boolean => {
      if (abandonedTarget === target) return false;

      try {
        if (sessionStorage.getItem(RELOAD_GUARD_KEY) === target) {
          // Already reloaded once for this exact build and came back still
          // running the old one - reloading again would just spin. Something
          // upstream is serving a stale document; that's worth an error line
          // rather than a loop on the wall.
          abandonedTarget = target;
          clientLogger.error('Reload did not pick up the deployed build; giving up on it', {
            loadedVersion,
            deployedVersion: target,
          });
          return false;
        }
        sessionStorage.setItem(RELOAD_GUARD_KEY, target);
      } catch {
        // Storage unavailable - proceed without the loop guard rather than
        // never picking up a deploy.
      }

      clientLogger.info('Reloading for a newer dashboard build', { loadedVersion, deployedVersion: target });
      location.reload();
      return true;
    };

    const onIdle = () => {
      idleTimer = null;

      // Falls through to the reset when the reload declines (loop guard, or a
      // target already given up on) - otherwise abandoning one bad deploy would
      // silently cost the display its idle reset from then on.
      if (pendingTarget !== null && reload(pendingTarget)) return;

      ignoreActivityUntil = Date.now() + SELF_INFLICTED_ACTIVITY_MS;
      window.scrollTo(0, 0);
      setResetToken((token) => token + 1);
      clientLogger.info('Idle reset to base state', { idleTimeoutMs: IDLE_TIMEOUT_MS });
    };

    const markActivity = () => {
      const now = Date.now();
      if (now < ignoreActivityUntil) return;
      if (now - lastActivityAt < ACTIVITY_THROTTLE_MS) return;

      lastActivityAt = now;
      if (idleTimer !== null) clearTimeout(idleTimer);
      idleTimer = setTimeout(onIdle, IDLE_TIMEOUT_MS);
    };

    const pollVersion = async () => {
      if (loadedVersion === '') return;

      let deployed: string | null;
      try {
        deployed = await fetchDeployedVersion();
      } catch {
        // Offline or mid-restart; the next poll covers it. Deliberately not
        // logged - an outage would otherwise write a line every 5 minutes.
        return;
      }
      if (disposed || deployed === null) return;

      if (deployed === loadedVersion) {
        pendingTarget = null;
        try {
          sessionStorage.removeItem(RELOAD_GUARD_KEY);
        } catch {
          // Nothing to clean up if storage is unavailable.
        }
        return;
      }

      if (pendingTarget !== deployed) {
        pendingTarget = deployed;
        clientLogger.info('Newer dashboard build deployed; reloading once idle', {
          loadedVersion,
          deployedVersion: deployed,
        });
      }

      // Nobody is looking at a half-read screen, so don't make them wait for an
      // idle timer that only activity would have armed in the first place.
      if (isIdle()) reload(deployed);
    };

    for (const event of ACTIVITY_EVENTS) {
      window.addEventListener(event, markActivity, { passive: true, capture: true });
    }

    void pollVersion();
    const poll = setInterval(() => void pollVersion(), VERSION_POLL_INTERVAL_MS);

    return () => {
      disposed = true;
      clearInterval(poll);
      if (idleTimer !== null) clearTimeout(idleTimer);
      for (const event of ACTIVITY_EVENTS) {
        window.removeEventListener(event, markActivity, { capture: true });
      }
    };
  }, []);

  return resetToken;
}
