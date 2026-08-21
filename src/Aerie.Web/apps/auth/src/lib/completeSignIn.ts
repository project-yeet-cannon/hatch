import { clientLogger } from './clientLogger';

/**
 * Leaves the shell for wherever this sign-in was headed.
 *
 * `location.replace` rather than `assign` or a router navigation, for two
 * reasons. It drops the sign-in URL out of history, so Back from the landing
 * page goes where the person originally came from instead of to a form whose
 * code has now been spent; and on the deep-link route it is what gets the
 * invite code out of the address bar, and out of anything that later reads
 * history or session restore.
 *
 * A full document load rather than a client-side route because the target
 * belongs to another app's bundle, and this one is finished. The queued log
 * line survives it: `clientLogger` flushes on `pagehide` with `sendBeacon`.
 */
export function completeSignIn(landing: string): void {
  clientLogger.info('Signed in, leaving the shell', { landing });
  location.replace(landing);
}
