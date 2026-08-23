/**
 * The two things a permanently-open wall display needs that an ordinary browser
 * tab gets for free, both hanging off one notion of "last touch":
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
 *
 * This is the page's half of the idle ladder in lib/kioskIdleTimings.ts. The
 * other rung - dimming the backlight, later - belongs to the native shell,
 * because CSS cannot reach a backlight.
 *
 * ## Why this is a plain factory rather than the body of the hook
 *
 * Both behaviours are actively hostile to a text field, and Gather puts one on
 * the wall: a reset would remount a half-typed item out of existence and a
 * reload would take the whole page. `hold()`/`release()` is the seam that
 * suspends them, and a seam whose whole value is "the timer really did not fire"
 * has to be tested rather than eyeballed. Keeping the state machine out of a
 * `useEffect` closure means vitest can drive it with fake timers directly —
 * hooks/useKioskLifecycle.ts is then only the wiring — instead of the dashboard
 * taking on jsdom and a renderer to assert on a `setTimeout`.
 */

import { IDLE_TIMEOUT_MS } from './kioskIdleTimings';

const VERSION_POLL_INTERVAL_MS = 5 * 60_000;

// Scroll fires per frame; there's no reason to re-arm a 30s timer at 60Hz.
const ACTIVITY_THROTTLE_MS = 250;

// The reset scrolls the page, and that scroll fires the same events this module
// treats as user activity - without a suppression window the reset re-arms its
// own idle timer and the display resets itself every 30s forever.
const SELF_INFLICTED_ACTIVITY_MS = 1_000;

// Survives location.reload() within the session, which is exactly the span a
// reload loop would live in.
const RELOAD_GUARD_KEY = 'aerie-dashboard-reload-target';

/**
 * What counts as someone being present. Exported because anything else on the
 * kiosk with its own idle timeout - GatherOverlay's, for one - has to agree with
 * this list, or a screen stays alive under one definition and closes under the
 * other.
 *
 * `input` is in the list rather than bolted on by the one component that has a
 * text field, because the reason it belongs is not about Gather: soft keyboards
 * do not reliably produce `keydown` (docs/kiosk-architecture.md, "Text entry on
 * the wall"), so without it a run of typing looks exactly like an empty room to
 * every timer here.
 */
export const ACTIVITY_EVENTS = [
  'pointerdown',
  'pointermove',
  'touchstart',
  'touchmove',
  'wheel',
  'scroll',
  'keydown',
  'input',
] as const;

export interface KioskLifecycleDeps {
  /**
   * The version token this document was loaded from. Empty disables drift
   * checks entirely - `npm run dev` serves an unhashed /src/main.tsx, so there
   * is nothing to compare against and every poll would look like drift.
   */
  loadedVersion: string;
  fetchDeployedVersion: () => Promise<string | null>;
  /** Put the page back to base state. Called after the self-inflicted-activity window opens. */
  onReset: () => void;
  reload: () => void;
  /** Session storage for the reload loop guard, or null where it isn't available. */
  storage: Storage | null;
  log: { info: (message: string, extra?: Record<string, unknown>) => void; error: (message: string, extra?: Record<string, unknown>) => void };
}

export interface KioskLifecycle {
  /** Someone touched the screen. Throttled; re-arms the idle timer unless held. */
  markActivity: () => void;
  /**
   * Suspend both behaviours. Idempotent. Anything already armed is cancelled,
   * and a deploy noticed while held is remembered rather than acted on.
   */
  hold: () => void;
  /**
   * Resume, counting as a fresh touch: closing an overlay is not idleness -
   * someone is standing right there - so the display gets a full idle timeout
   * before it resets, and a deploy that drifted in the meantime reloads on that
   * timeout rather than yanking the page away the instant the overlay closes.
   */
  release: () => void;
  /** Exposed for tests; the interval drives it in the app. */
  pollVersion: () => Promise<void>;
  dispose: () => void;
}

export function createKioskLifecycle(deps: KioskLifecycleDeps): KioskLifecycle {
  const { loadedVersion, log } = deps;

  let lastActivityAt = 0; // 0 = untouched since load
  let ignoreActivityUntil = 0;
  let idleTimer: ReturnType<typeof setTimeout> | null = null;
  let pendingTarget: string | null = null;
  let abandonedTarget: string | null = null;
  let held = false;
  let disposed = false;

  const isIdle = () => lastActivityAt === 0 || Date.now() - lastActivityAt >= IDLE_TIMEOUT_MS;

  /** True if a reload is under way; false if it declined to attempt one. */
  const reload = (target: string): boolean => {
    if (abandonedTarget === target) return false;

    try {
      if (deps.storage?.getItem(RELOAD_GUARD_KEY) === target) {
        // Already reloaded once for this exact build and came back still
        // running the old one - reloading again would just spin. Something
        // upstream is serving a stale document; that's worth an error line
        // rather than a loop on the wall.
        abandonedTarget = target;
        log.error('Reload did not pick up the deployed build; giving up on it', {
          loadedVersion,
          deployedVersion: target,
        });
        return false;
      }
      deps.storage?.setItem(RELOAD_GUARD_KEY, target);
    } catch {
      // Storage unavailable - proceed without the loop guard rather than
      // never picking up a deploy.
    }

    log.info('Reloading for a newer dashboard build', { loadedVersion, deployedVersion: target });
    deps.reload();
    return true;
  };

  const onIdle = () => {
    idleTimer = null;

    // Falls through to the reset when the reload declines (loop guard, or a
    // target already given up on) - otherwise abandoning one bad deploy would
    // silently cost the display its idle reset from then on.
    if (pendingTarget !== null && reload(pendingTarget)) return;

    ignoreActivityUntil = Date.now() + SELF_INFLICTED_ACTIVITY_MS;
    deps.onReset();
    log.info('Idle reset to base state', { idleTimeoutMs: IDLE_TIMEOUT_MS });
  };

  const arm = () => {
    if (idleTimer !== null) clearTimeout(idleTimer);
    idleTimer = setTimeout(onIdle, IDLE_TIMEOUT_MS);
  };

  const markActivity = () => {
    const now = Date.now();
    if (now < ignoreActivityUntil) return;
    if (now - lastActivityAt < ACTIVITY_THROTTLE_MS) return;

    lastActivityAt = now;
    // While held, activity still counts as presence - it is what keeps
    // pollVersion from reading the screen as abandoned - but nothing is armed,
    // because release() is what re-arms.
    if (!held) arm();
  };

  const pollVersion = async () => {
    if (loadedVersion === '') return;

    let deployed: string | null;
    try {
      deployed = await deps.fetchDeployedVersion();
    } catch {
      // Offline or mid-restart; the next poll covers it. Deliberately not
      // logged - an outage would otherwise write a line every 5 minutes.
      return;
    }
    if (disposed || deployed === null) return;

    if (deployed === loadedVersion) {
      pendingTarget = null;
      try {
        deps.storage?.removeItem(RELOAD_GUARD_KEY);
      } catch {
        // Nothing to clean up if storage is unavailable.
      }
      return;
    }

    if (pendingTarget !== deployed) {
      pendingTarget = deployed;
      log.info('Newer dashboard build deployed; reloading once idle', {
        loadedVersion,
        deployedVersion: deployed,
      });
    }

    // Nobody is looking at a half-read screen, so don't make them wait for an
    // idle timer that only activity would have armed in the first place. Held
    // is the exception: someone is typing into an overlay, which is the one
    // state where "no recent touch" and "nobody is there" come apart.
    if (!held && isIdle()) reload(deployed);
  };

  if (loadedVersion === '') {
    log.info('Dashboard build has no hashed assets; version drift checks disabled');
  } else {
    log.info('Dashboard lifecycle started', { loadedVersion });
  }

  void pollVersion();
  const poll = setInterval(() => void pollVersion(), VERSION_POLL_INTERVAL_MS);

  return {
    markActivity,

    hold: () => {
      if (held) return;
      held = true;
      if (idleTimer !== null) {
        clearTimeout(idleTimer);
        idleTimer = null;
      }
      log.info('Kiosk lifecycle held; idle reset and deploy reload suspended');
    },

    release: () => {
      if (!held) return;
      held = false;
      // Deliberately bypasses the throttle and the self-inflicted window: this
      // is a state change, not a stray pointermove, and it must always leave a
      // timer armed or nothing would ever reset the display again.
      lastActivityAt = Date.now();
      arm();
      log.info('Kiosk lifecycle released', { pendingDeploy: pendingTarget !== null });
    },

    pollVersion,

    dispose: () => {
      disposed = true;
      clearInterval(poll);
      if (idleTimer !== null) clearTimeout(idleTimer);
      idleTimer = null;
    },
  };
}
